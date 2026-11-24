using System.Runtime.InteropServices;

namespace PadDeck;

/// <summary>
/// Music controls for whichever app is playing (Spotify, Apple Music, a browser...). Uses Windows' own media controls
/// (the ones behind the volume pop-up), so there are no per-app shortcuts and it works while the app is in the
/// background: play/pause, next, previous, shuffle, repeat and seeking. Volume and mute go to the app's own volume
/// (the same as the mixer), and "like" presses the app's own heart / Favourite button.
/// </summary>
internal static unsafe partial class Player
{
    public static readonly Worker Thread = new("player", ThreadPriority.AboveNormal, () => RoInitialize(1 /* MTA */));

    // What the lights show. Written on the player thread.
    public static volatile bool Playing, Shuffle, HasSession;
    public static volatile int Repeat;                    // 0 off, 1 one song, 2 all
    public static volatile string? Aumid;                 // the playing app's id
    public static volatile string? AudioProcess;          // its process in the volume mixer
    public static volatile string? WindowProcess;         // the process that owns its window
    public static uint Color = 0x334155;                  // its icon colour
    public static Action? Changed;

    static nint _mgr;
    static int _busy;
    static long _lastRefresh = -99999;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static readonly Dictionary<string, (string window, string audio, uint color)> AppCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>From the engine's poll while a music pad is showing: re-read the state about once a second.</summary>
    public static void Tick()
    {
        long now = Clock.ElapsedMilliseconds;
        if (now - Volatile.Read(ref _lastRefresh) < 1000) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Volatile.Write(ref _lastRefresh, now);
        Thread.Post(() => { try { Refresh(); } finally { Volatile.Write(ref _busy, 0); } });
    }

    /// <summary>Do one music action. Runs on the player thread.</summary>
    public static void Do(string? op, double? value)
    {
        Refresh();
        nint s = Session();
        try
        {
            switch (op)
            {
                case "playpause":
                    if (s != 0) Fire(s, 20);
                    else Keys("mediaplaypause");                              // no app has a session yet: let Windows decide
                    break;
                case "next":
                    if (s != 0) Fire(s, 16); else Keys("medianext");
                    break;
                case "prev":
                    if (s != 0) Fire(s, 17); else Keys("mediaprev");
                    break;
                case "shuffle":
                    if (s == 0) break;
                    if (!CanShuffle) { PressOwn(ShuffleKeys, "shuffle"); break; }
                    nint op1 = 0;
                    ((delegate* unmanaged[Stdcall]<nint, byte, nint*, int>)Vt(s)[23])(s, (byte)(Shuffle ? 0 : 1), &op1);
                    Release(op1);
                    break;
                case "repeat":
                    // off → all → one → off
                    if (s == 0) break;
                    if (!CanRepeat) { PressOwn(RepeatKeys, "repeat"); break; }
                    int next = Repeat switch { 0 => 2, 2 => 1, _ => 0 };
                    nint op2 = 0;
                    ((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(s)[21])(s, next, &op2);
                    Release(op2);
                    break;
                case "seek":
                    if (s == 0) break;
                    if (CanSeek) Seek(s, value ?? 10);
                    else SeekOwn(value ?? 10);
                    break;
                case "app":
                    BringToFront();
                    break;
                case "like":
                    Like();
                    break;
            }
        }
        finally { Release(s); }
        System.Threading.Thread.Sleep(250);
        _forceOwn = true;
        Refresh();
    }

    // ------------------------------------------------------------------ state

    public static void Refresh()
    {
        _lastRefresh = Clock.ElapsedMilliseconds;
        nint s = Session();
        bool playing = false, shuffle = false, has = s != 0, canShuffle = false, canRepeat = false, canSeek = false;
        int repeat = 0;
        string? aumid = null;
        if (s != 0)
        {
            try
            {
                nint h = 0;
                if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(s)[6])(s, &h) == 0) aumid = TakeHString(h);
                nint info = 0;
                if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(s)[9])(s, &info) == 0 && info != 0)
                {
                    try
                    {
                        int status = 0;
                        ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(info)[7])(info, &status);
                        playing = status == 4;
                        // Which controls the app takes through Windows (Apple Music, for one, doesn't take shuffle / repeat).
                        nint ctl = 0;
                        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(info)[6])(info, &ctl) == 0 && ctl != 0)
                        {
                            byte sh1 = 0, rp1 = 0, sk1 = 0;
                            ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Vt(ctl)[17])(ctl, &sh1);
                            ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Vt(ctl)[18])(ctl, &rp1);
                            ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Vt(ctl)[20])(ctl, &sk1);
                            canShuffle = sh1 != 0; canRepeat = rp1 != 0; canSeek = sk1 != 0;
                            Release(ctl);
                        }
                        nint r = 0;
                        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(info)[9])(info, &r) == 0 && r != 0)
                        {
                            int v = 0;
                            ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(r)[6])(r, &v);
                            repeat = v;
                            Release(r);
                        }
                        nint sh = 0;
                        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(info)[11])(info, &sh) == 0 && sh != 0)
                        {
                            byte v = 0;
                            ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Vt(sh)[6])(sh, &v);
                            shuffle = v != 0;
                            Release(sh);
                        }
                    }
                    finally { Release(info); }
                }
            }
            finally { Release(s); }
        }

        if (aumid != Aumid)
        {
            _hwnd = 0;
            if (aumid != null)
            {
                var (w, a, c) = AppInfo(aumid);
                WindowProcess = w; AudioProcess = a; Color = c;
            }
            else { WindowProcess = null; AudioProcess = null; Color = 0x334155; }
        }
        CanShuffle = canShuffle; CanRepeat = canRepeat; CanSeek = canSeek;

        // Shuffle / repeat the app doesn't share with Windows: read them off its own buttons (every 3 s, or right after a press).
        bool readOwn = aumid != null && (!canShuffle || !canRepeat) && (aumid != Aumid || _forceOwn || Clock.ElapsedMilliseconds - _lastOwn > 3000);
        if (!readOwn) { shuffle = canShuffle ? shuffle : Shuffle; repeat = canRepeat ? repeat : Repeat; }
        if (readOwn && PlayerWindow(aumid!) is var hwnd && hwnd != 0)
        {
            _lastOwn = Clock.ElapsedMilliseconds;
            _forceOwn = false;
            if (!canShuffle)
            {
                var (name, toggle) = ReadButton(hwnd, ShuffleKeys);
                shuffle = toggle == 1 || string.Equals(name, "Disable shuffle", StringComparison.OrdinalIgnoreCase);
            }
            if (!canRepeat)
            {
                var (name, _) = ReadButton(hwnd, RepeatKeys);
                repeat = (name ?? "").ToLowerInvariant() switch
                {
                    "repeat one" or "disable repeat" => 1,                                        // Apple Music / Spotify: repeating one song
                    "repeat all" or "repeat" or "enable repeat one" => 2,                         // repeating everything
                    _ => 0,                                                                       // "do not repeat", "enable repeat"
                };
            }
        }

        bool changed = playing != Playing || shuffle != Shuffle || repeat != Repeat || has != HasSession || aumid != Aumid;
        Playing = playing; Shuffle = shuffle; Repeat = repeat; HasSession = has; Aumid = aumid;
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Work out the playing app's window process, the process its sound comes from, and its icon colour.
    /// Apple Music plays through a helper (AMPLibraryAgent), so its volume lives there.
    /// </summary>
    static (string window, string audio, uint color) AppInfo(string aumid)
    {
        if (AppCache.TryGetValue(aumid, out var hit)) return hit;
        string window = aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(aumid).ToLowerInvariant() : "";
        string? exePath = null;
        var wins = Windows(aumid, window);
        if (wins.Count > 0)
        {
            User32.GetWindowThreadProcessId(wins[0].Hwnd, out var pid);
            exePath = Gdi.ProcessPath(pid);
            if (exePath != null) window = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        }
        if (window.Length == 0) window = aumid.Split('!', '_', '.')[^1].ToLowerInvariant();
        string audio = aumid.StartsWith("AppleInc.AppleMusic", StringComparison.OrdinalIgnoreCase) ? "amplibraryagent" : window;
        // Icon colour: Store apps through the shell (their exe icons can't be read directly), others from the exe.
        uint color = 0xE2E8F0;
        string? shellName = aumid.Contains('!') ? @"shell:AppsFolder\" + aumid : exePath;
        if (shellName != null)
        {
            var t = AppWindows.Thread.Invoke(() => { try { return Gdi.AppIconColor(shellName); } catch { return null; } });
            if (t.Wait(2000) && t.Result is { } sc) color = sc;
        }
        if (color == 0xE2E8F0 && exePath != null) { try { color = Gdi.IconColor(exePath); } catch { } }
        var r = (window, audio, color);
        if (wins.Count > 0) AppCache[aumid] = r;                              // only cache once we found it properly
        return r;
    }

    static List<AppWindows.Win> Windows(string aumid, string? process)
    {
        var path = aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? null : @"shell:AppsFolder\" + aumid;
        var match = string.IsNullOrEmpty(process) ? null : process;
        if (path == null && match == null) return new();
        var task = AppWindows.Thread.Invoke(() =>
        {
            var t = AppWindows.Resolve(path, match);
            if (t == null) return new List<AppWindows.Win>();
            AppWindows.Find(t, out var w);
            return w;
        });
        return task.Wait(2000) ? task.Result : new();
    }

    // ------------------------------------------------------------------ the app's own buttons

    public static volatile bool CanShuffle, CanRepeat, CanSeek;

    // Apple Music by automation id; Spotify by its button names (they change with the state).
    static readonly string[] ShuffleKeys = { "#ShuffleButton", "Enable shuffle", "Disable shuffle" };
    static readonly string[] RepeatKeys = { "#RepeatButton", "Enable repeat", "Enable repeat one", "Disable repeat" };

    static nint _hwnd;
    static long _lastOwn = -99999;
    static bool _forceOwn;

    static nint PlayerWindow(string aumid)
    {
        if (_hwnd != 0 && IsWindow(_hwnd) != 0) return _hwnd;
        var wins = Windows(aumid, WindowProcess);
        return _hwnd = wins.Count > 0 ? wins[0].Hwnd : 0;
    }

    static (string? name, int toggle) ReadButton(nint hwnd, string[] keys)
    {
        var t = DiscordCtl.Thread.Invoke(() => DiscordCtl.ReadButton(hwnd, keys));
        return t.Wait(2000) ? t.Result : (null, -1);
    }

    static void PressOwn(string[] keys, string what)
    {
        var aumid = Aumid;
        var hwnd = aumid == null ? 0 : PlayerWindow(aumid);
        if (hwnd == 0) { Log.Write($"Music {what}: the player's window isn't open"); return; }
        var t = DiscordCtl.Thread.Invoke(() => DiscordCtl.ClickNamed(hwnd, keys));
        if (!t.Wait(3000) || !t.Result) Log.Write($"Music {what}: no {what} button in {WindowProcess ?? aumid}");
    }

    /// <summary>Apps that don't let Windows jump within a song (Apple Music): move their own position slider (in seconds).</summary>
    static void SeekOwn(double seconds)
    {
        var aumid = Aumid;
        var hwnd = aumid == null ? 0 : PlayerWindow(aumid);
        if (hwnd == 0) return;
        string[] keys = { "#LCDScrubber" };
        var t = DiscordCtl.Thread.Invoke(() => DiscordCtl.NudgeSlider(hwnd, keys, seconds));
        if (!t.Wait(3000) || !t.Result) Log.Write($"Music: can't jump within the song in {WindowProcess ?? aumid}");
    }

    [LibraryImport("user32.dll")] private static partial int IsWindow(nint h);

    // ------------------------------------------------------------------ actions

    /// <summary>Bring the playing app up, or minimize it if it's already in front.</summary>
    static void BringToFront()
    {
        var aumid = Aumid;
        if (aumid == null) return;
        var path = aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? null : @"shell:AppsFolder\" + aumid;
        var match = WindowProcess;
        AppWindows.Thread.Invoke(() =>
        {
            var t = AppWindows.Resolve(path, match);
            if (t == null) return 0;
            var st = AppWindows.Find(t, out var wins);
            if (st == AppWindows.State.Front) AppWindows.Minimize(wins);
            else if (wins.Count > 0)
            {
                var h = wins.FirstOrDefault(w => !w.Minimized).Hwnd;
                AppWindows.Activate(h != 0 ? h : wins[0].Hwnd);
            }
            return 0;
        }).Wait(3000);
    }

    /// <summary>The app's own heart / Favourite button, found by name in its window.</summary>
    static void Like()
    {
        var aumid = Aumid;
        if (aumid == null) return;
        var wins = Windows(aumid, WindowProcess);
        if (wins.Count == 0) { Log.Write("Music like: the player's window isn't open"); return; }
        string[] names = { "Favourite", "Favorite", "Love", "Add to Liked Songs", "Save to Your Library", "Like" };
        var hwnd = PlayerWindow(aumid);
        var ok = DiscordCtl.Thread.Invoke(() => DiscordCtl.ClickNamed(hwnd, names));
        if (!ok.Wait(3000) || !ok.Result) Log.Write("Music like: no heart / Favourite button in " + (WindowProcess ?? aumid));
    }

    static void Seek(nint s, double seconds)
    {
        nint tl = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(s)[8])(s, &tl) != 0 || tl == 0) return;
        long pos = 0, end = 0, updated = 0;
        try
        {
            ((delegate* unmanaged[Stdcall]<nint, long*, int>)Vt(tl)[10])(tl, &pos);
            ((delegate* unmanaged[Stdcall]<nint, long*, int>)Vt(tl)[7])(tl, &end);
            ((delegate* unmanaged[Stdcall]<nint, long*, int>)Vt(tl)[11])(tl, &updated);
        }
        finally { Release(tl); }
        // The position is as of the last update: add the time since, while playing.
        if (Playing && updated > 0) pos += Math.Max(0, DateTime.UtcNow.ToFileTimeUtc() - updated);
        long target = Math.Clamp(pos + (long)(seconds * 10_000_000), 0, end > 10_000_000 ? end - 10_000_000 : long.MaxValue);
        nint op = 0;
        ((delegate* unmanaged[Stdcall]<nint, long, nint*, int>)Vt(s)[24])(s, target, &op);
        Release(op);
    }

    static void Fire(nint s, int slot)
    {
        nint op = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(s)[slot])(s, &op);
        Release(op);                                                           // the request carries on without us waiting
    }

    static void Keys(string name)
    {
        if (Keyboard.Parse(name) is { } k) Keyboard.Tap(k, 5);
    }

    // ------------------------------------------------------------------ WinRT plumbing

    static nint Session()
    {
        var m = Manager();
        if (m == 0) return 0;
        nint s = 0;
        return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(m)[6])(m, &s) == 0 ? s : 0;     // GetCurrentSession
    }

    static nint Manager()
    {
        if (_mgr != 0) return _mgr;
        const string cls = "Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager";
        nint h = 0, statics = 0, op = 0;
        fixed (char* p = cls) if (WindowsCreateString(p, (uint)cls.Length, &h) != 0) return 0;
        try
        {
            var iid = IID_ManagerStatics;
            if (RoGetActivationFactory(h, &iid, &statics) != 0 || statics == 0) return 0;
        }
        finally { WindowsDeleteString(h); }
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(statics)[6])(statics, &op) != 0 || op == 0) return 0;   // RequestAsync
        }
        finally { Release(statics); }
        return _mgr = Await(op);
    }

    /// <summary>Wait for an IAsyncOperation and take its result (releases the operation).</summary>
    static nint Await(nint op, int ms = 3000)
    {
        nint info = 0, result = 0;
        try
        {
            var iid = IID_AsyncInfo;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vt(op)[0])(op, &iid, &info) != 0 || info == 0) return 0;
            long until = Clock.ElapsedMilliseconds + ms;
            int status = 0;
            while (true)
            {
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(info)[7])(info, &status);
                if (status != 0 || Clock.ElapsedMilliseconds > until) break;
                System.Threading.Thread.Sleep(10);
            }
            if (status != 1) return 0;                                         // 1 = completed
            ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(op)[8])(op, &result);                 // GetResults
            return result;
        }
        finally { Release(info); Release(op); }
    }

    static string? TakeHString(nint h)
    {
        if (h == 0) return null;
        try
        {
            uint len = 0;
            char* p = WindowsGetStringRawBuffer(h, &len);
            return p == null ? null : new string(p, 0, (int)len);
        }
        finally { WindowsDeleteString(h); }
    }

    static nint* Vt(nint p) => *(nint**)p;
    static void Release(nint p) { if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vt(p)[2])(p); }

    static readonly Guid IID_ManagerStatics = new("2050c4ee-11a0-57de-aed7-c97c70338245");
    static readonly Guid IID_AsyncInfo = new("00000036-0000-0000-C000-000000000046");

    [LibraryImport("combase.dll")] private static partial int RoInitialize(int type);
    [LibraryImport("combase.dll")] private static partial int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
    [LibraryImport("combase.dll")] private static partial int WindowsCreateString(char* s, uint len, nint* h);
    [LibraryImport("combase.dll")] private static partial int WindowsDeleteString(nint h);
    [LibraryImport("combase.dll")] private static partial char* WindowsGetStringRawBuffer(nint h, uint* len);
}
