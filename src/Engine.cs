using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PadDeck;

/// <summary>
/// The brain: maps pad presses to actions, tracks pages/toggles/feedback, and renders LED frames.
/// </summary>
internal sealed class Engine : IDisposable
{
    readonly object _sync = new();
    Config _cfg;
    int _page;
    volatile bool _paused;

    readonly Launchpad _pad;
    readonly Audio _audio;
    readonly Obs _obs;
    readonly Worker _actions = new("actions", ThreadPriority.AboveNormal);

    readonly bool[] _held = new bool[100];
    readonly Dictionary<string, bool> _toggles = new();
    readonly ConcurrentDictionary<string, float> _levels = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, bool> _mutes = new(StringComparer.OrdinalIgnoreCase);

    readonly Thread _renderThread;
    readonly AutoResetEvent _dirty = new(true);
    volatile bool _running = true;
    readonly Timer _pollTimer;
    int _pollBusy;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    uint[] _lastFrame = new uint[100];
    long _lastLedBroadcast;

    // auto mixer
    List<FaderDef> _autoFaders = new();
    readonly Dictionary<string, ButtonDef> _autoMutes = new();
    long _lastAutoRefresh = -10_000;
    int _autoBusy;

    // light show
    public static readonly string[] Effects = { "rainbow", "plasma", "rain", "sparkle", "ripple", "fire", "breathe", "radar", "ocean" };
    long _lastActivity;
    volatile bool _showActive;
    int _showIndex;
    double _showStart;
    readonly bool[] _swallow = new bool[100];
    public Func<bool>? EditorOpen;

    // default audio devices (for "device is default" lights)
    volatile string? _defOut, _defIn;

    /// <summary>JSON messages for the editor's live view.</summary>
    public event Action<string>? Broadcast;

    public Engine(Config cfg, Launchpad pad, Audio audio, Obs obs)
    {
        _cfg = cfg;
        _pad = pad;
        _audio = audio;
        _obs = obs;
        DiscordCtl.Changed = MarkDirty;
        Player.Changed = MarkDirty;
        _pad.Button += OnButton;
        _pad.ConnectionChanged += () =>
        {
            if (_pad.Connected) _pad.SetBrightness(_cfg.Settings.Brightness);
            MarkDirty();
            SendStatus();
        };
        _obs.Changed += () => { MarkDirty(); SendStatus(); };
        ApplyRuntime();
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "leds", Priority = ThreadPriority.AboveNormal };
        _renderThread.Start();
        _pollTimer = new Timer(_ => Poll(), null, 300, 300);
    }

    // ------------------------------------------------------------------ state

    public Config Config { get { lock (_sync) return _cfg; } }
    public int PageIndex { get { lock (_sync) return _page; } }
    public bool Paused => _paused;
    public Launchpad Pad => _pad;
    public Obs Obs => _obs;
    public Audio Audio => _audio;

    int _revision;
    /// <summary>Goes up every time the whole layout is replaced, so an editor window holding an older copy can't save over a newer one.</summary>
    public int Revision { get { lock (_sync) return _revision; } }

    /// <summary>Replaces the layout. With <paramref name="expectedRevision"/>, refuses (returns -1) if the layout changed since that revision.</summary>
    public int Apply(Config cfg, int? expectedRevision = null)
    {
        int rev;
        lock (_sync)
        {
            if (expectedRevision is int want && want != _revision) return -1;
            _cfg = cfg;
            _page = Math.Clamp(_page, 0, cfg.Pages.Count - 1);
            rev = ++_revision;
        }
        ConfigStore.Save(cfg);
        _lastActivity = _clock.ElapsedMilliseconds;
        ApplyRuntime();
        RefreshAutoMixer(true);
        MarkDirty();
        SendStatus();
        Broadcast?.Invoke($"{{\"type\":\"config\",\"revision\":{rev}}}");
        return rev;
    }

    void ApplyRuntime()
    {
        var s = _cfg.Settings;
        _pad.SetBrightness(s.Brightness);
        _obs.Configure(s.ObsEnabled, s.ObsUrl, s.ObsPassword);
        var inputs = AllButtons().Where(b => b.Feedback == "obs.mute" && b.FeedbackArg != null).Select(b => b.FeedbackArg!);
        _obs.WatchInputs(inputs);
    }

    IEnumerable<ButtonDef> AllButtons() =>
        _cfg.Pages.SelectMany(p => p.Buttons.Values).Concat(_cfg.Global.Values);

    public void SetPaused(bool paused) { _paused = paused; MarkDirty(); SendStatus(); }

    public void SetPage(int index)
    {
        lock (_sync)
        {
            if (_cfg.Pages.Count == 0) return;
            _page = ((index % _cfg.Pages.Count) + _cfg.Pages.Count) % _cfg.Pages.Count;
            _autoFaders = new();
            _autoMutes.Clear();
        }
        RefreshAutoMixer(true);
        MarkDirty();
        SendStatus();
        Poll();
    }

    Page CurrentPage => _cfg.Pages[Math.Clamp(_page, 0, _cfg.Pages.Count - 1)];

    static readonly string[] IdStr = Enumerable.Range(0, 100).Select(i => i.ToString()).ToArray();

    /// <summary>scope: 1 = this page, 2 = auto-mixer mute, 3 = every page. Allocation-free: runs 25x a second.</summary>
    ButtonDef? Resolve(int id, out int scope)
    {
        var sid = IdStr[id];
        if (CurrentPage.Buttons.TryGetValue(sid, out var b)) { scope = 1; return b; }
        if (AutoMute(id) is { } mute) { scope = 2; return mute; }
        if (_cfg.Global.TryGetValue(sid, out b)) { scope = 3; return b; }
        scope = 0;
        return null;
    }

    string Key(int id, int scope) => scope switch { 1 => $"{_page}:{id}", 2 => $"{_page}:auto{id}", _ => "g:" + IdStr[id] };

    List<FaderDef> PageFaders => CurrentPage.AutoMixer ? _autoFaders : CurrentPage.Faders;

    /// <summary>The fader covering a grid pad, and the pad's step along it (1 = 0%, 8 = 100%).</summary>
    FaderDef? FaderAt(int id, out int pos)
    {
        pos = 0;
        int row = id / 10, col = id % 10;
        if (row < 1 || row > 8 || col < 1 || col > 8) return null;
        foreach (var f in PageFaders)
        {
            if (string.IsNullOrWhiteSpace(f.Target)) continue;
            if (!f.Horizontal && f.Column == col) { pos = row; return f; }
            if (f.Horizontal && f.Row == row) { pos = col; return f; }
        }
        return null;
    }

    /// <summary>Auto mixer (vertical): the top-row button above each app's column mutes it.</summary>
    ButtonDef? AutoMute(int id)
    {
        var p = CurrentPage;
        if (!p.AutoMixer || p.AutoHorizontal || id < 91 || id > 98) return null;
        FaderDef? f = null;
        foreach (var x in _autoFaders) if (x.Column == id - 90) { f = x; break; }
        if (f == null) return null;
        if (!_autoMutes.TryGetValue(f.Target, out var b))
        {
            b = new ButtonDef
            {
                Label = "Mute " + (f.Label ?? f.Target),
                Color = "#" + Scale(Hex(f.Color), 0.22).ToString("x6"),
                ActiveColor = "#dc2626",
                Feedback = f.Target switch { "master" => "master.mute", "mic" => "mic.mute", _ => "app.mute" },
                FeedbackArg = f.Target,
                Actions = { new ActionDef { Type = "volume", Op = "togglemute", Target = f.Target } },
            };
            _autoMutes[f.Target] = b;
        }
        return b;
    }

    // ------------------------------------------------------------------ auto mixer

    void RefreshAutoMixer(bool force = false)
    {
        bool auto;
        lock (_sync) auto = CurrentPage.AutoMixer;
        if (!auto) return;
        long now = _clock.ElapsedMilliseconds;
        if (!force && now - _lastAutoRefresh < 1500) return;
        if (Interlocked.Exchange(ref _autoBusy, 1) == 1) return;
        _lastAutoRefresh = now;
        _audio.Thread.Post(() =>
        {
            try
            {
                var apps = _audio.Apps();
                bool changed;
                lock (_sync) changed = AssignAuto(apps);
                if (changed) { MarkDirty(); SendStatus(); Poll(); }
            }
            finally { Volatile.Write(ref _autoBusy, 0); }
        });
    }

    /// <summary>Give each running app a fader. Apps keep their slot while they run, so faders don't jump around.</summary>
    bool AssignAuto(List<Audio.AppAudio> apps)
    {
        var p = CurrentPage;
        if (!p.AutoMixer) return false;
        var entries = new List<(string target, string label, uint color)>();
        if (p.AutoSystem) { entries.Add(("master", "Speakers", 0xE2E8F0)); entries.Add(("mic", "Mic", 0xF59E0B)); }
        int appSlots = 8 - entries.Count;

        var byName = apps.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var order = _autoFaders.Select(f => f.Target).Where(byName.ContainsKey).ToList();
        foreach (var a in apps.OrderByDescending(a => a.Playing).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase))
            if (!order.Contains(a.Name, StringComparer.OrdinalIgnoreCase)) order.Add(a.Name);
        if (order.Count > appSlots)
        {
            // Too many apps: keep the ones making sound, in their existing order.
            var keep = order.Where(n => byName[n].Playing).Concat(order.Where(n => !byName[n].Playing)).Take(appSlots).ToHashSet();
            order = order.Where(keep.Contains).ToList();
        }
        foreach (var n in order) entries.Add((n, byName[n].Label, byName[n].Color));

        var next = new List<FaderDef>();
        for (int i = 0; i < entries.Count; i++)
        {
            var (target, label, color) = entries[i];
            next.Add(new FaderDef
            {
                Target = target, Label = label, Color = "#" + color.ToString("x6"),
                Horizontal = p.AutoHorizontal, Column = i + 1, Row = 8 - i,
            });
        }
        bool same = next.Count == _autoFaders.Count && next.Zip(_autoFaders).All(z =>
            z.First.Target == z.Second.Target && z.First.Horizontal == z.Second.Horizontal && z.First.Color == z.Second.Color);
        if (same) return false;
        _autoFaders = next;
        _autoMutes.Clear();
        return true;
    }

    // ------------------------------------------------------------------ light show

    public bool ShowActive => _showActive;
    public string? ShowName => _showActive ? Effects[CurrentEffect()] : null;

    int CurrentEffect()
    {
        int idx = _showIndex;
        if (_cfg.Settings.IdleEffect == "cycle" && !_showFixed)
            idx += (int)((_clock.Elapsed.TotalSeconds - _showStart) / 30);   // a new effect every 30 s
        return ((idx % Effects.Length) + Effects.Length) % Effects.Length;
    }

    bool _showFixed;

    void StartShow()
    {
        if (!_showActive)
        {
            _showFixed = false;
            int fixedIdx = Array.IndexOf(Effects, _cfg.Settings.IdleEffect);
            if (fixedIdx >= 0) _showIndex = fixedIdx;
        }
        _showStart = _clock.Elapsed.TotalSeconds;
        _showActive = true;
        MarkDirty();
        SendStatus();
    }

    void StopShow()
    {
        if (!_showActive) return;
        _showActive = false;
        _lastActivity = _clock.ElapsedMilliseconds;
        Mem.TrimSoon(3000);
        MarkDirty();
        SendStatus();
    }

    /// <summary>op: next | toggle | start | stop | brighter | dimmer | play:&lt;effect name&gt;</summary>
    public void ShowCommand(string op)
    {
        _lastActivity = _clock.ElapsedMilliseconds;
        if (op.StartsWith("play:", StringComparison.Ordinal))
        {
            // Start straight on one effect (it stays on it, even when Settings say "cycle").
            int idx = Array.IndexOf(Effects, op[5..]);
            if (idx < 0) return;
            _showIndex = idx;
            _showStart = _clock.Elapsed.TotalSeconds;
            _showFixed = true;
            _showActive = true;
            MarkDirty();
            SendStatus();
            return;
        }
        switch (op)
        {
            case "toggle": if (_showActive) StopShow(); else StartShow(); break;
            case "stop": StopShow(); break;
            case "start": StartShow(); break;
            case "brighter":
            case "dimmer":
                lock (_sync)
                {
                    var s = _cfg.Settings;
                    s.ShowBrightness = Math.Clamp(s.ShowBrightness + (op == "brighter" ? 15 : -15), 10, 100);
                }
                var snapshot = _cfg;
                Task.Run(() => { try { ConfigStore.Save(snapshot); } catch (Exception ex) { Log.Write("save: " + ex.Message); } });
                MarkDirty();
                SendStatus();
                break;
            default:
                if (_showActive) { _showIndex = CurrentEffect() + 1; }
                StartShow();
                break;
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>Called on the pad input thread for every press/release.</summary>
    void OnButton(int id, bool down)
    {
        if (id == 99) return;
        lock (_sync)
        {
            _held[id] = down;
            Broadcast?.Invoke($"{{\"type\":\"press\",\"id\":{id},\"down\":{(down ? "true" : "false")}}}");
            _lastActivity = _clock.ElapsedMilliseconds;
            if (!down && _swallow[id]) { _swallow[id] = false; MarkDirty(); return; }
            if (_showActive && down)
            {
                var sb = Resolve(id, out _);
                bool showButton = sb != null && sb.Actions.Any(a => a.Type == "lightshow");
                if (!showButton)
                {
                    StopShow();
                    if (_cfg.Settings.WakePressOnly) { _swallow[id] = true; MarkDirty(); return; }
                }
            }
            if (_paused) { MarkDirty(); return; }

            var fader = FaderAt(id, out int pos);
            if (fader != null)
            {
                if (down)
                {
                    float level = (pos - 1) / 7f;
                    var target = fader.Target;
                    // The app may have no sound open yet (Discord before a call): keep the level and set it
                    // the moment the app opens its audio, instead of the press doing nothing.
                    if (!IsSystemTarget(target) && (!_levels.TryGetValue(target, out var known) || known < 0)) _pendingLevels[target] = level;
                    else _pendingLevels.TryRemove(target, out _);
                    _levels[target] = level;
                    if (level > 0) _mutes[target] = false;
                    _audio.Thread.Post(() => _audio.SetLevel(target, level));
                }
                MarkDirty();
                return;
            }

            var b = Resolve(id, out var scope);
            if (b != null)
            {
                switch (b.Mode)
                {
                    case "hold":
                        if (down) Run(b.Actions, Phase.Down);
                        else { Run(b.Actions, Phase.Up); Run(b.OffActions, Phase.Tap); }
                        break;
                    case "toggle":
                        if (!down) break;
                        bool on = IsActive(b, id, scope);
                        if (string.IsNullOrEmpty(b.Feedback)) _toggles[Key(id, scope)] = !on;
                        Run(on && b.OffActions.Count > 0 ? b.OffActions : b.Actions, Phase.Tap);
                        break;
                    default:
                        if (down)
                        {
                            var hc = HoldCloseAction(b);
                            var share = ShareAction(b);
                            if (hc != null)
                            {
                                BeginHoldClose(id, hc);
                                Run(b.Actions.Where(x => !ReferenceEquals(x, hc)).ToList(), Phase.Tap);
                            }
                            else if (share != null)
                            {
                                BeginShareHold(id, share);
                                Run(b.Actions.Where(x => !ReferenceEquals(x, share)).ToList(), Phase.Tap);
                            }
                            else Run(b.Actions, Phase.Tap);
                        }
                        else { EndHoldClose(id); EndShareHold(id); }
                        break;
                }
            }
        }
        MarkDirty();
    }

    // ------------------------------------------------------------------ hold to close

    const int HoldCloseMs = 600;

    sealed class HoldCtx { public bool Done, MinimizeOnRelease; public List<AppWindows.Win> Wins = new(); }
    readonly HoldCtx?[] _hold = new HoldCtx?[100];

    static ActionDef? HoldCloseAction(ButtonDef b)
    {
        foreach (var a in b.Actions)
            if (a.Type == "open" && (a.HoldClose ?? true) && WindowMode(a) is "toggle" or "focus") return a;
        return null;
    }

    /// <summary>
    /// What an app button does when the app is already open. Unset means the default: bring it to the front,
    /// or minimize it if it's in front. "launch" opens it again. Files, folders and websites always just open.
    /// </summary>
    internal static string WindowMode(ActionDef a) =>
        !AppWindows.IsAppTarget(a.Path) ? "launch" : a.Window ?? "toggle";

    /// <summary>
    /// Press: act straight away (launch / restore / bring to front). If the app is already in front, minimizing
    /// waits for the release, so a long press closes it instead of minimizing and then closing.
    /// </summary>
    void BeginHoldClose(int id, ActionDef a)
    {
        var ctx = new HoldCtx();
        _hold[id] = ctx;
        AppWindows.Thread.Post(() =>
        {
            var t = AppWindows.Resolve(a.Path, a.Match);
            var st = t == null ? AppWindows.State.None : AppWindows.Find(t, out ctx.Wins);
            switch (st)
            {
                case AppWindows.State.None: Launch(a.Path, a.Args); break;
                case AppWindows.State.Minimized: AppWindows.Activate(ctx.Wins[0].Hwnd); break;
                case AppWindows.State.Behind: AppWindows.Activate(ctx.Wins.First(w => !w.Minimized).Hwnd); break;
                case AppWindows.State.Front: ctx.MinimizeOnRelease = WindowMode(a) == "toggle"; break;
            }
        });
        Task.Delay(HoldCloseMs).ContinueWith(_ => AppWindows.Thread.Post(() =>
        {
            if (ctx.Done) return;
            ctx.Done = true;
            if (ctx.Wins.Count > 0) { AppWindows.Close(ctx.Wins); FlashClosed(id); }
        }));
    }

    void EndHoldClose(int id)
    {
        var ctx = _hold[id];
        if (ctx == null) return;
        _hold[id] = null;
        AppWindows.Thread.Post(() =>
        {
            if (ctx.Done) return;
            ctx.Done = true;
            if (ctx.MinimizeOnRelease) AppWindows.Minimize(ctx.Wins);
        });
    }

    // ------------------------------------------------------------------ Discord share: tap shares, hold opens the picker

    sealed class ShareCtx { public int Done; public string? Target; }
    readonly ShareCtx?[] _share = new ShareCtx?[100];

    static ActionDef? ShareAction(ButtonDef b) => b.Actions.FirstOrDefault(a => a.Type == "discord" && a.Op == "screen");

    void BeginShareHold(int id, ActionDef a)
    {
        var ctx = new ShareCtx { Target = a.Target };
        _share[id] = ctx;
        Task.Delay(HoldCloseMs).ContinueWith(_ =>
        {
            if (Interlocked.Exchange(ref ctx.Done, 1) == 0) DiscordCtl.Thread.Post(() => DiscordCtl.Press("picker"));
        });
    }

    void EndShareHold(int id)
    {
        var ctx = _share[id];
        if (ctx == null) return;
        _share[id] = null;
        var target = ctx.Target;
        if (Interlocked.Exchange(ref ctx.Done, 1) == 0) DiscordCtl.Thread.Post(() => DiscordCtl.Press("screen", target));
    }

    /// <summary>A short red blink on the pad so you can feel the close happen.</summary>
    void FlashClosed(int id)
    {
        lock (_sync) _closedFlashUntil[id] = _clock.ElapsedMilliseconds + 350;
        MarkDirty();
        Task.Delay(380).ContinueWith(_ => MarkDirty());
    }

    readonly long[] _closedFlashUntil = new long[100];

    public void TestPress(int id, int holdMs = 120)
    {
        OnButton(id, true);
        Task.Delay(holdMs).ContinueWith(_ => OnButton(id, false));
    }

    enum Phase { Tap, Down, Up }

    void Run(List<ActionDef> list, Phase phase)
    {
        if (list.Count == 0) return;
        var copy = list.ToArray();
        int tapMs = _cfg.Settings.TapMs;
        bool slow = phase == Phase.Tap && copy.Any(a => a.Type is "delay" or "open" or "command");
        if (slow) Task.Run(() => Execute(copy, phase, tapMs));
        else _actions.Post(() => Execute(copy, phase, tapMs));
    }

    void Execute(ActionDef[] list, Phase phase, int tapMs)
    {
        if (phase == Phase.Up)
        {
            for (int i = list.Length - 1; i >= 0; i--)
                if (list[i].Type == "hotkey" && Keyboard.Parse(list[i].Keys) is { } k) Keyboard.Up(k);
            return;
        }
        foreach (var a in list)
        {
            try { ExecuteOne(a, phase, tapMs); }
            catch (Exception ex) { Log.Write($"action {a.Type}: {ex.Message}"); }
        }
    }

    void ExecuteOne(ActionDef a, Phase phase, int tapMs)
    {
        switch (a.Type)
        {
            case "hotkey":
                if (Keyboard.Parse(a.Keys) is not { } keys) return;
                if (phase == Phase.Down) Keyboard.Down(keys);
                else Keyboard.Tap(keys, tapMs);
                break;
            case "type":
                if (!string.IsNullOrEmpty(a.Text)) Keyboard.TypeText(a.Text);
                break;
            case "media":
                var name = (a.Op ?? "playpause") switch
                {
                    "next" => "medianext", "prev" => "mediaprev", "stop" => "mediastop",
                    "volup" => "volumeup", "voldown" => "volumedown", "mute" => "volumemute",
                    _ => "mediaplaypause"
                };
                Keyboard.Tap(Keyboard.Parse(name)!, 5);
                break;
            case "volume":
                var target = string.IsNullOrWhiteSpace(a.Target) ? "master" : a.Target!;
                var op = a.Op ?? "togglemute";
                _audio.Thread.Post(() => { _audio.Apply(target, op, a.Value); RefreshTarget(target); });
                break;
            case "open":
                var wm = WindowMode(a);
                if (wm is "toggle" or "focus")
                    AppWindows.Thread.Invoke(() => AppWindows.Smart(a, wm == "toggle", out _)).Wait(3000);
                else Launch(a.Path, a.Args);
                break;
            case "player":
                var pop = a.Op ?? "playpause";
                var pval = a.Value;
                if (pop is "volup" or "voldown" or "mute" or "setvol")
                {
                    // The playing app's own volume, like the mixer.
                    Player.Thread.Post(() =>
                    {
                        if (Player.Aumid == null) Player.Refresh();
                        var proc = Player.AudioProcess;
                        if (proc == null) return;
                        _audio.Thread.Post(() =>
                        {
                            switch (pop)
                            {
                                case "volup": _audio.Apply(proc, "up", pval ?? 5); _audio.Apply(proc, "unmute", null); break;
                                case "voldown": _audio.Apply(proc, "down", pval ?? 5); break;
                                case "mute": _audio.Apply(proc, "togglemute", null); break;
                                default: _audio.SetLevel(proc, (float)Math.Clamp((pval ?? 50) / 100.0, 0, 1)); break;
                            }
                            RefreshTarget(proc);
                        });
                    });
                }
                else Player.Thread.Post(() => Player.Do(pop, pval));
                break;
            case "discord":
                var discordOp = a.Op;
                var discordTarget = a.Target;
                string? discordChannel = a.Text, discordLink = a.Path;
                DiscordCtl.Thread.Post(() => DiscordCtl.Press(discordOp, discordTarget, discordChannel, discordLink));
                break;
            case "command":
                if (string.IsNullOrWhiteSpace(a.Path)) return;
                Process.Start(new ProcessStartInfo("cmd.exe", "/c " + a.Path)
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
                break;
            case "obs":
                _obs.Do(a.Op, a.Target);
                break;
            case "page":
                int count = _cfg.Pages.Count;
                switch (a.Op)
                {
                    case "next": SetPage(PageIndex + 1); break;
                    case "prev": SetPage(PageIndex - 1); break;
                    default: SetPage(Math.Clamp(a.Page ?? 0, 0, Math.Max(0, count - 1))); break;
                }
                break;
            case "sound":
                Sound.Run(a.Path, a.Op ?? "play", a.Value);
                break;
            case "audiodevice":
                bool input = a.Target == "input";
                string dop = a.Op ?? "next", did = a.Path ?? "", dname = a.Text ?? "";
                _audio.Thread.Post(() => { _audio.SwitchDevice(input, dop, did, dname); RefreshDefaults(); });
                break;
            case "lightshow":
                ShowCommand(a.Op ?? "next");
                break;
            case "delay":
                Thread.Sleep(Math.Clamp(a.Ms ?? 100, 0, 60_000));
                break;
        }
    }

    internal static void Launch(string? path, string? args)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
        {
            // Start-menu entry (works for Microsoft Store apps too): let Explorer launch it exactly like the Start menu does.
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = false })?.Dispose();
            return;
        }
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (File.Exists(path) && !string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
        }
        catch { }
        Process.Start(psi)?.Dispose();
    }

    // ------------------------------------------------------------------ feedback

    bool IsActive(ButtonDef b, int id, int scope)
    {
        switch (b.Feedback)
        {
            case "mic.mute": return _mutes.TryGetValue("mic", out var m1) && m1;
            case "master.mute": return _mutes.TryGetValue("master", out var m2) && m2;
            case "app.mute": return b.FeedbackArg != null && _mutes.TryGetValue(b.FeedbackArg, out var m3) && m3;
            case "obs.record": return _obs.Connected && _obs.Recording;
            case "obs.stream": return _obs.Connected && _obs.Streaming;
            case "obs.replay": return _obs.Connected && _obs.ReplayActive;
            case "obs.virtualcam": return _obs.Connected && _obs.VirtualCam;
            case "obs.scene": return _obs.Connected && b.FeedbackArg != null && string.Equals(_obs.Scene, b.FeedbackArg, StringComparison.OrdinalIgnoreCase);
            case "obs.mute": return _obs.Connected && _obs.IsMuted(b.FeedbackArg);
            case "audio.device": return b.FeedbackArg != null && (b.FeedbackArg == _defOut || b.FeedbackArg == _defIn);
            case "discord.mute": return DiscordCtl.Muted;
            case "discord.deafen": return DiscordCtl.Deafened;
            case "discord.camera": return DiscordCtl.Camera;
            case "discord.screen": return DiscordCtl.Sharing;
            case "discord.call": return DiscordCtl.InCall;
            case "player.playing": return Player.Playing;
            case "player.shuffle": return Player.Shuffle;
            case "player.repeat": return Player.Repeat != 0;
            case "player.mute": return Player.AudioProcess is { } pm && _mutes.TryGetValue(pm, out var m4) && m4;
            case "player.level":
                // A row of "set volume" pads reads as a meter: lit up to the current level.
                if (Player.AudioProcess is not { } pl || !_levels.TryGetValue(pl, out var lv) || lv < 0) return false;
                if (_mutes.TryGetValue(pl, out var m5) && m5) return false;
                return double.TryParse(b.FeedbackArg, out var at) && lv >= at / 100.0 - 0.02;
        }
        if (b.Mode == "toggle") return _toggles.TryGetValue(Key(id, scope), out var t) && t;
        // A "go to page" button lights up while its page is showing.
        foreach (var a in b.Actions)
            if (a.Type == "page" && (a.Op ?? "goto") == "goto" && (a.Page ?? 0) == _page) return true;
        return false;
    }

    static string? AudioTarget(ButtonDef b) => b.Feedback switch
    {
        "mic.mute" => "mic",
        "master.mute" => "master",
        "app.mute" => b.FeedbackArg,
        "player.mute" or "player.level" => Player.AudioProcess,
        _ => null
    };

    static bool UsesPlayer(ButtonDef b) =>
        b.Color == "player" || (b.Feedback != null && b.Feedback.StartsWith("player.", StringComparison.Ordinal)) ||
        b.Actions.Exists(a => a.Type == "player");

    /// <summary>Poll Windows audio for what is visible right now (mute LEDs, fader levels).</summary>
    bool _editorWasOpen;
    long _lastTrim, _lastGen0;

    void Poll()
    {
        // Idle light show (not while the editor is open: you'd lose the colour preview while editing).
        var s = _cfg.Settings;
        bool editor = EditorOpen?.Invoke() ?? false;
        long nowMs = _clock.ElapsedMilliseconds;
        if (_editorWasOpen && !editor) Mem.TrimSoon();                  // editor closed: hand its memory back
        _editorWasOpen = editor;
        if (!editor && !_showActive && nowMs - _lastTrim > 10 * 60_000) { _lastTrim = nowMs; Mem.TrimSoon(0); }
        // The runtime sizes its young-object area from the CPU cache (24 MB here) and lets the heap fill it before
        // cleaning up. A young-only collection every 5 s keeps that area small; with a heap this size it takes well under 1 ms.
        if (nowMs - _lastGen0 > 5000) { _lastGen0 = nowMs; GC.Collect(0, GCCollectionMode.Forced, true, false); }
        if (!_showActive && s.IdleAuto && s.IdleSeconds > 0 && _pad.Connected && !editor &&
            _clock.ElapsedMilliseconds - _lastActivity > s.IdleSeconds * 1000L)
            StartShow();

        RefreshAutoMixer();

        HashSet<string> mutes, levels;
        bool devices = false, discord = false, player = false;
        lock (_sync)
        {
            mutes = new(StringComparer.OrdinalIgnoreCase);
            levels = new(StringComparer.OrdinalIgnoreCase);
            foreach (var b in CurrentPage.Buttons.Values.Concat(_cfg.Global.Values))
            {
                if (AudioTarget(b) is { Length: > 0 } t) { mutes.Add(t); if (b.Feedback == "player.level") levels.Add(t); }
                if (UsesPlayer(b)) player = true;
                if (b.Feedback == "audio.device") devices = true;
                if (b.Feedback != null && b.Feedback.StartsWith("discord.", StringComparison.Ordinal)) discord = true;
            }
            foreach (var f in PageFaders)
                if (!string.IsNullOrWhiteSpace(f.Target)) { levels.Add(f.Target); mutes.Add(f.Target); }
        }
        if (devices) _audio.Thread.Post(RefreshDefaults);
        if (discord) DiscordCtl.Tick();
        if (player) Player.Tick();
        if (mutes.Count == 0 && levels.Count == 0) return;
        if (Interlocked.Exchange(ref _pollBusy, 1) == 1) return;
        _audio.Thread.Post(() =>
        {
            try
            {
                bool changed = false;
                _appTargets.Clear();
                foreach (var t in mutes) if (!IsSystemTarget(t)) _appTargets.Add(t);
                foreach (var t in levels) if (!IsSystemTarget(t)) _appTargets.Add(t);
                _audio.AppStates(_appTargets, _appStates);
                foreach (var (t, want) in _pendingLevels)
                {
                    if (!_appStates.TryGetValue(t, out var now)) continue;        // still no sound open
                    _audio.SetLevel(t, want);
                    _appStates[t] = (want, want > 0.001f ? false : now.mute);
                    _pendingLevels.TryRemove(t, out _);
                }
                foreach (var t in levels)
                {
                    float? v = IsSystemTarget(t) ? _audio.GetLevel(t) : _appStates.TryGetValue(t, out var st) ? st.level : null;
                    changed |= Store(_levels, t, v ?? -1f);
                }
                foreach (var t in mutes)
                {
                    bool? m = IsSystemTarget(t) ? _audio.GetMute(t) : _appStates.TryGetValue(t, out var st) ? st.mute : null;
                    changed |= Store(_mutes, t, m ?? false);
                }
                if (changed) MarkDirty();
            }
            finally { Volatile.Write(ref _pollBusy, 0); }
        });
    }

    /// <summary>Fader levels picked while the app had no sound open; applied when it opens some.</summary>
    readonly ConcurrentDictionary<string, float> _pendingLevels = new(StringComparer.OrdinalIgnoreCase);

    // reused by Poll on the audio thread
    readonly HashSet<string> _appTargets = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (float level, bool mute)> _appStates = new(StringComparer.OrdinalIgnoreCase);

    static bool IsSystemTarget(string t) =>
        t.Equals("master", StringComparison.OrdinalIgnoreCase) || t.Equals("mic", StringComparison.OrdinalIgnoreCase) ||
        t.Equals("focused", StringComparison.OrdinalIgnoreCase);

    void RefreshDefaults()
    {
        var o = _audio.DefaultId(false);
        var i = _audio.DefaultId(true);
        if (o != _defOut || i != _defIn) { _defOut = o; _defIn = i; MarkDirty(); }
    }

    void RefreshTarget(string target)
    {
        bool changed = Store(_levels, target, _audio.GetLevel(target) ?? -1f);
        changed |= Store(_mutes, target, _audio.GetMute(target) ?? false);
        if (changed) MarkDirty();
    }

    static bool Store<T>(ConcurrentDictionary<string, T> d, string k, T v)
    {
        if (d.TryGetValue(k, out var old) && EqualityComparer<T>.Default.Equals(old, v)) return false;
        d[k] = v;
        return true;
    }

    // ------------------------------------------------------------------ LEDs

    public void MarkDirty() => _dirty.Set();

    void RenderLoop()
    {
        bool animating = false, editorBehind = false;
        while (_running)
        {
            _dirty.WaitOne(animating ? 40 : editorBehind ? 90 : Timeout.Infinite);
            if (!_running) break;
            try
            {
                var frame = Compose(out animating);
                _pad.Show(frame);
                editorBehind = !MaybeBroadcastLeds(frame);
            }
            catch (Exception ex) { Log.Write("render: " + ex.Message); }
        }
    }

    /// <summary>Returns false when the editor still needs this frame (throttled), so the loop retries.</summary>
    bool MaybeBroadcastLeds(uint[] frame)
    {
        if (Broadcast == null) return true;
        if (frame.AsSpan().SequenceEqual(_lastFrame)) return true;
        long now = _clock.ElapsedMilliseconds;
        if (now - _lastLedBroadcast < 80) return false;     // the editor doesn't need 30 fps
        _lastLedBroadcast = now;
        Array.Copy(frame, _lastFrame, 100);
        var sb = new StringBuilder("{\"type\":\"leds\",\"c\":{");
        bool first = true;
        foreach (var id in Launchpad.AllIds)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(id).Append("\":").Append(frame[id]);
        }
        Broadcast.Invoke(sb.Append("}}").ToString());
        return true;
    }

    public void ForceLedBroadcast() { _lastLedBroadcast = 0; _lastFrame = new uint[100]; MarkDirty(); }

    static uint Hex(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var t = s.AsSpan().TrimStart('#');
        if (t.Length == 3)
        {
            Span<char> six = stackalloc char[6] { t[0], t[0], t[1], t[1], t[2], t[2] };
            return uint.TryParse(six, System.Globalization.NumberStyles.HexNumber, null, out var v3) ? v3 & 0xFFFFFF : 0;
        }
        return uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v & 0xFFFFFF : 0;
    }

    static uint Scale(uint c, double f)
    {
        f = Math.Clamp(f, 0, 1);
        uint r = (uint)(((c >> 16) & 0xFF) * f), g = (uint)(((c >> 8) & 0xFF) * f), b = (uint)((c & 0xFF) * f);
        return r << 16 | g << 8 | b;
    }

    static uint Mix(uint c, uint d, double t)
    {
        uint L(int sh) => (uint)Math.Round(((c >> sh) & 0xFF) * (1 - t) + ((d >> sh) & 0xFF) * t);
        return L(16) << 16 | L(8) << 8 | L(0);
    }

    static uint Brighten(uint c)
    {
        // Default "active" colour: same hue, pushed to full brightness, with a little white mixed in.
        uint max = Math.Max((c >> 16) & 0xFF, Math.Max((c >> 8) & 0xFF, c & 0xFF));
        if (max == 0) return 0xFFFFFF;
        return Mix(Scale(c, 255.0 / max), 0xFFFFFF, 0.25);
    }

    readonly uint[][] _frames = { new uint[100], new uint[100] };
    int _flip;

    uint[] Compose(out bool animating)
    {
        var f = _frames[_flip ^= 1];          // reuse two buffers: no garbage per frame
        Array.Clear(f);
        animating = false;
        double t = _clock.Elapsed.TotalSeconds;
        long nowMs = _clock.ElapsedMilliseconds;
        bool blinkOn = (int)(t * 2.5) % 2 == 0;
        double pulse = 0.2 + 0.8 * (0.5 + 0.5 * Math.Sin(t * Math.PI * 2 * 0.8));

        if (_showActive)
        {
            animating = true;
            int fx = CurrentEffect();
            double st = t - _showStart;
            double level = _cfg.Settings.ShowBrightness / 100.0;
            foreach (var id in Launchpad.AllIds)
                f[id] = _held[id] ? 0xFFFFFF : Scale(LightShow.Color(fx, id % 10, id / 10, st), level);
            return f;
        }

        lock (_sync)
        {
            var s = _cfg.Settings;
            var page = CurrentPage;
            f[99] = _paused ? (blinkOn ? 0xF59E0Bu : 0u) : Hex(s.LogoColor);
            if (_paused) animating = true;

            foreach (var id in Launchpad.AllIds)
            {
                if (id == 99) continue;
                uint c;
                var fader = FaderAt(id, out int pos);
                if (fader != null)
                {
                    float level = _levels.TryGetValue(fader.Target, out var lv) ? lv : -1;
                    bool muted = _mutes.TryGetValue(fader.Target, out var mu) && mu;
                    uint fc = Hex(fader.Color);
                    // App has no sound open right now: show the level you picked (dimmer, it's waiting for the app),
                    // or the whole track faintly so it still reads as a slider.
                    if (level < 0 && _pendingLevels.TryGetValue(fader.Target, out var want))
                    {
                        int lit = want <= 0.001f ? 0 : (int)Math.Round(want * 7) + 1;
                        c = pos <= lit ? Scale(fc, 0.4) : Scale(fc, 0.05);
                    }
                    else if (level < 0) c = Scale(fc, pos == 1 ? 0.14 : 0.05);
                    else
                    {
                        int lit = level <= 0.001f ? 0 : (int)Math.Round(level * 7) + 1;
                        c = pos <= lit ? (muted ? Scale(0xDC2626, 0.5) : fc) : Scale(fc, 0.06);
                        if (pos == 1 && lit == 0) c = muted ? 0xDC2626 : Scale(fc, 0.25);
                    }
                }
                else
                {
                    var b = Resolve(id, out var scope);
                    if (b == null) c = 0;
                    else
                    {
                        bool active = IsActive(b, id, scope);
                        // "player": the colour of whichever music app is playing (its icon colour).
                        uint baseC = b.Color == "player" ? (Player.HasSession ? Player.Color : 0x1E293B) : Hex(b.Color);
                        c = active ? (b.ActiveColor != null ? Hex(b.ActiveColor) : Brighten(baseC)) : baseC;
                        bool animate = b.Effect switch
                        {
                            "flash" or "pulse" => true,
                            "flash-active" or "pulse-active" => active,
                            _ => false
                        };
                        if (b.Feedback == "player.repeat" && Player.Repeat == 1) animate = true;   // repeating one song: pulses
                        if (animate)
                        {
                            animating = true;
                            c = b.Effect.StartsWith("flash") ? (blinkOn ? c : Scale(c, 0.08)) : Scale(c, pulse);
                        }
                    }
                }
                if (_held[id] && s.PressFlash) c = c == 0 ? 0x505050 : Mix(c, 0xFFFFFF, 0.55);
                if (_closedFlashUntil[id] > nowMs) c = 0xDC2626;
                f[id] = c;
            }
        }
        return f;
    }

    // ------------------------------------------------------------------ status

    public string StatusJson()
    {
        var sb = new StringBuilder("{");
        void P(string k, string v, bool last = false) => sb.Append('"').Append(k).Append("\":").Append(v).Append(last ? "" : ",");
        P("connected", _pad.Connected ? "true" : "false");
        P("device", Json.Str(_pad.DeviceName));
        P("playerColor", Player.HasSession ? Json.Str("#" + Player.Color.ToString("x6")) : "null");
        P("playerApp", Json.Str(Player.WindowProcess ?? ""));
        P("playerState", $"{{\"playing\":{(Player.Playing ? "true" : "false")},\"shuffle\":{(Player.Shuffle ? "true" : "false")},\"repeat\":{Player.Repeat},\"audio\":{Json.Str(Player.AudioProcess ?? "")}}}");
        P("status", Json.Str(_pad.Status));
        P("ports", "[" + string.Join(",", _pad.PortNames.Select(Json.Str)) + "]");
        P("obsConnected", _obs.Connected ? "true" : "false");
        P("obsStatus", Json.Str(_obs.Status));
        P("paused", _paused ? "true" : "false");
        P("show", Json.Str(ShowName));
        P("showBrightness", _cfg.Settings.ShowBrightness.ToString());
        List<FaderDef> auto;
        lock (_sync) auto = CurrentPage.AutoMixer ? _autoFaders : new();
        P("mixer", "[" + string.Join(",", auto.Select(f =>
            $"{{\"target\":{Json.Str(f.Target)},\"label\":{Json.Str(f.Label)},\"color\":{Json.Str(f.Color)},\"column\":{f.Column},\"row\":{f.Row},\"horizontal\":{(f.Horizontal ? "true" : "false")}}}")) + "]");
        P("page", PageIndex.ToString(), true);
        return sb.Append('}').ToString();
    }

    void SendStatus() => Broadcast?.Invoke("{\"type\":\"status\",\"status\":" + StatusJson() + "}");

    public void Dispose()
    {
        _running = false;
        _dirty.Set();
        _pollTimer.Dispose();
        _actions.Dispose();
    }
}

/// <summary>Idle animations. Pure functions of position and time, so they cost nothing between frames.</summary>
internal static class LightShow
{
    static double Hash(int a, int b)
    {
        unchecked
        {
            uint h = (uint)(a * 374761393 + b * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (double)0xFFFFFF;
        }
    }

    static uint Hsv(double h, double s, double v)
    {
        h = (h % 1 + 1) % 1 * 6;
        v = Math.Clamp(v, 0, 1);
        int i = (int)h;
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), u = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, u, p), 1 => (q, v, p), 2 => (p, v, u), 3 => (p, q, v), 4 => (u, p, v), _ => (v, p, q) };
        return (uint)(r * 255) << 16 | (uint)(g * 255) << 8 | (uint)(b * 255);
    }

    static double Smooth(double a, double b, double v)
    {
        double k = Math.Clamp((v - a) / (b - a), 0, 1);
        return k * k * (3 - 2 * k);
    }

    static double Lerp(double a, double b, double k) => a + (b - a) * k;

    /// <summary>Smooth 3D value noise, 0..1. Cheap enough for 81 pads at 30 fps.</summary>
    static double Noise(double x, double y, double z)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
        double xf = x - xi, yf = y - yi, zf = z - zi;
        double u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf), w = zf * zf * (3 - 2 * zf);
        double C(int a, int b, int c) => Hash(a * 73856093 ^ c * 83492791, b * 19349663 + c);
        double x00 = Lerp(C(xi, yi, zi), C(xi + 1, yi, zi), u), x10 = Lerp(C(xi, yi + 1, zi), C(xi + 1, yi + 1, zi), u);
        double x01 = Lerp(C(xi, yi, zi + 1), C(xi + 1, yi, zi + 1), u), x11 = Lerp(C(xi, yi + 1, zi + 1), C(xi + 1, yi + 1, zi + 1), u);
        return Lerp(Lerp(x00, x10, v), Lerp(x01, x11, v), w);
    }

    static uint Rgb(double r, double g, double b) =>
        (uint)(Math.Clamp(r, 0, 1) * 255) << 16 | (uint)(Math.Clamp(g, 0, 1) * 255) << 8 | (uint)(Math.Clamp(b, 0, 1) * 255);

    /// <summary>x, y: 1..9 (programmer layout, y = 1 is the bottom row).</summary>
    public static uint Color(int effect, int x, int y, double t)
    {
        switch (effect)
        {
            case 0: // rainbow wave
                return Hsv((x + y) * 0.055 - t * 0.25, 1, 1);

            case 1: // plasma
            {
                double v = Math.Sin(x * 0.65 + t * 1.1) + Math.Sin(y * 0.55 - t * 1.4) + Math.Sin((x + y) * 0.45 + t * 0.8)
                         + Math.Sin(Math.Sqrt((x - 5) * (x - 5) + (y - 5) * (y - 5)) * 0.9 - t * 1.6);
                return Hsv(v * 0.12 + t * 0.04, 1, 1);
            }

            case 2: // digital rain
            {
                double speed = 4 + Hash(x, 1) * 5, len = 4, period = 9 + len + Hash(x, 2) * 6;
                double head = 9.5 - (t * speed + Hash(x, 3) * period) % period;
                double d = y - head;
                if (d < 0 || d >= len) return 0;
                double b = Math.Pow(1 - d / len, 2);
                return d < 1 ? Rgb(0.75, 1, 0.8) : Hsv(0.36, 1, b);
            }

            case 3: // sparkle
            {
                int i = y * 10 + x;
                double ph = t * (0.5 + Hash(i, 1) * 1.1) + Hash(i, 2) * 10;
                int cycle = (int)Math.Floor(ph);
                double b = Math.Pow(Math.Max(0, Math.Sin((ph - cycle) * Math.PI)), 6);
                return Hsv(Hash(i, cycle), 0.75, b);
            }

            case 4: // ripples from a wandering centre
            {
                double cx = 5 + 3 * Math.Sin(t * 0.6), cy = 5 + 3 * Math.Cos(t * 0.45);
                double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                double b = 0.5 + 0.5 * Math.Sin(d * 1.5 - t * 5);
                return Hsv(t * 0.08 + d * 0.05, 1, b * b);
            }

            case 5: // fire
            {
                double flicker = Math.Sin(x * 1.9 + t * 7.3) * Math.Sin(y * 1.1 - t * 5.1) * 0.25
                               + (Hash(x * 10 + y, (int)(t * 12)) - 0.5) * 0.3;
                double heat = Math.Clamp(1.15 - (y - 1) / 7.0 + flicker, 0, 1);
                return Rgb(heat * 1.8, (heat * 1.8 - 0.8) * 0.8, (heat * 3 - 2.6) * 0.6);
            }

            case 6: // breathe
            {
                double v = 0.12 + 0.88 * (0.5 - 0.5 * Math.Cos(t * Math.PI * 2 / 4.5));
                return Hsv(t * 0.03 + (x + y) * 0.012, 0.9, v);
            }

            case 8: // ocean: deep blue, azure, cyan and mint drifting like light under water, with soft white glints and twinkling stars
            {
                // Colour: slow domain-warped noise, so the blues and greens wander and fold into each other instead of scrolling.
                double wx = Noise(x * 0.23 + 3.1, y * 0.23, t * 0.07) * 2.2, wy = Noise(x * 0.23, y * 0.23 + 7.7, t * 0.07 + 5) * 2.2;
                // 0..1 along the palette. Raw noise bunches up around the middle, so stretch it to reach deep blue and mint as often as the video does.
                double hue = Smooth(0.22, 0.78, Noise(x * 0.38 + wx, y * 0.38 + wy, t * 0.11));
                // Brightness: a slow swell with deep troughs, so bright patches stand out against darker water.
                double swell = 0.16 + 0.84 * Smooth(0.2, 0.8, Noise(x * 0.42 - t * 0.09, y * 0.42 + 11, t * 0.16));
                // Caustics: thin bright ridges where two drifting wave fields cross, like sunlight on water.
                double c1 = Math.Sin(x * 0.85 + y * 0.35 + t * 0.9 + wx * 1.4), c2 = Math.Sin(y * 0.8 - x * 0.45 - t * 0.75 + wy * 1.4);
                double glint = Math.Pow(Math.Max(0, 1 - Math.Abs(c1 + c2) * 0.9), 6) * Smooth(0.45, 0.8, Noise(x * 0.5, y * 0.5, t * 0.2 + 20));
                // Stars: now and then a pad brightens to white and fades back over a couple of seconds.
                int id = y * 10 + x;
                double ph = t / (2.6 + Hash(id, 7) * 2.4) + Hash(id, 8) * 9;
                int cyc = (int)Math.Floor(ph);
                double star = Hash(id, cyc) > 0.87 ? Math.Pow(Math.Sin((ph - cyc) * Math.PI), 3) : 0;

                // Palette from the video: deep blue → azure → cyan → mint.
                double r, g, b;
                if (hue < 0.35) { double k = Smooth(0, 0.35, hue); r = Lerp(0.03, 0.10, k); g = Lerp(0.16, 0.42, k); b = Lerp(0.70, 1.00, k); }
                else if (hue < 0.65) { double k = Smooth(0.35, 0.65, hue); r = Lerp(0.10, 0.16, k); g = Lerp(0.42, 0.80, k); b = Lerp(1.00, 1.00, k); }
                else { double k = Smooth(0.65, 1.0, hue); r = Lerp(0.16, 0.22, k); g = Lerp(0.80, 0.95, k); b = Lerp(1.00, 0.76, k); }   // to teal-mint
                r *= swell; g *= swell; b *= swell;
                double w = Math.Clamp(glint * 0.85 + star, 0, 1);                              // mix toward icy white
                return Rgb(Lerp(r, 0.82, w), Lerp(g, 0.95, w), Lerp(b, 1.0, w));
            }

            default: // radar sweep
            {
                double a = Math.Atan2(y - 5, x - 5);
                double diff = ((t * 2.2 - a) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) / (2 * Math.PI);
                double b = Math.Pow(1 - diff, 5);
                bool blip = Hash(y * 10 + x, (int)(t / 3)) > 0.94;
                return blip && diff < 0.35 ? Rgb(1, 1, 1) : Hsv(0.38, 1, b);
            }
        }
    }
}

internal static class Json
{
    public static string Str(string? s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
