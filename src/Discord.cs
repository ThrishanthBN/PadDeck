using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PadDeck;

/// <summary>
/// Discord has no keyboard shortcut for the camera, screen share or hanging up, so PadDeck presses Discord's own
/// on-screen buttons through Windows UI Automation (the accessibility interface screen readers use). It also reads
/// whether the mic, camera and screen share are on, for the pad lights. This works while Discord is in the
/// background or closed to the tray. It matches Discord's English button names.
/// </summary>
internal static unsafe partial class DiscordCtl
{
    public const string AppPath = @"shell:AppsFolder\com.squirrel.Discord.Discord";

    public static readonly Worker Thread = new("discord", ThreadPriority.AboveNormal, () => Ole32.CoInitializeEx(0, 0 /* MTA */));

    // What the pad lights show. Written on the discord thread.
    public static volatile bool Muted, Deafened, Camera, Sharing, InCall;
    public static Action? Changed;

    static readonly HashSet<string> Wanted = new(StringComparer.Ordinal)
    {
        "mute", "unmute", "deafen", "undeafen",
        "turn on camera", "turn off camera",
        "share your screen", "share screen", "stop streaming", "stop sharing",
        "disconnect", "leave call",
        "decline", "ignore",
        "show chat", "hide chat",
    };

    sealed class Btn { public string Name = ""; public nint El; }
    static readonly List<Btn> _btns = new();
    static int _seen;                                   // all buttons found in the last scan, wanted or not
    static nint _uia, _cond;
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static long _lastScan = -99999, _lastRead = -99999;
    static int _busy;

    // ------------------------------------------------------------------ public

    /// <summary>Called from the engine's poll while a Discord-light pad is showing: light reads every second, full look every 4 s.</summary>
    public static void Tick()
    {
        long now = Clock.ElapsedMilliseconds;
        if (now - Volatile.Read(ref _lastRead) < 1000) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Volatile.Write(ref _lastRead, now);
        Thread.Post(() =>
        {
            try
            {
                if (Clock.ElapsedMilliseconds - _lastScan > 4000 || _btns.Count == 0 || !ReadStates()) Scan();
            }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    /// <summary>Do one Discord action. Runs on the discord thread. <paramref name="target"/>: what to share, for "screen".</summary>
    public static void Press(string? op, string? target = null, string? channel = null, string? link = null)
    {
        long t0 = Clock.ElapsedMilliseconds;
        try
        {
            if (op == "join") JoinVoice(target, channel, link);
            else PressCore(op, target);
        }
        catch (Exception ex) { Log.Write($"Discord {op}: {ex.Message}"); }
        finally { Log.Write($"Discord {op}: {Clock.ElapsedMilliseconds - t0} ms, {_seen} buttons seen, {_btns.Count} known"); }
    }

    static void PressCore(string? op, string? target)
    {
        Scan();
        if (_seen == 0 && Windows().Count > 0) WakeUp();
        if (Uia() == 0 && op is not ("mute" or "deafen")) return;
        switch (op)
        {
            case "mute":
                if (!Click("mute", "unmute")) Keys("ctrl+shift+m");        // Discord's own shortcut, works in the background
                break;
            case "deafen":
                if (!Click("deafen", "undeafen")) Keys("ctrl+shift+d");
                break;
            case "camera":
                bool wasOn = Camera;
                if (Click("turn on camera", "turn off camera") && !wasOn)
                {
                    // Discord may show a camera preview to confirm first: bring it up so you can see it.
                    System.Threading.Thread.Sleep(500);
                    Scan();
                    if (!Camera) Front();
                }
                break;
            case "screen":
                if (Sharing) Click("stop streaming", "stop sharing");
                else ShareScreen(target);
                break;
            case "picker":
                // Long press: Discord's own screen-share picker (or, while sharing, Discord with its stream controls).
                if (Sharing) Front();
                else OpenPicker();
                break;
            case "disconnect":
                Click("disconnect", "leave call");
                break;
            case "decline":
                Click("decline", "ignore");
                break;
            case "chat":
                ToggleChat();
                break;
        }
        System.Threading.Thread.Sleep(300);
        Scan();
    }

    // ------------------------------------------------------------------ steps

    static void Keys(string combo)
    {
        if (Keyboard.Parse(combo) is { } k) Keyboard.Tap(k, 30);
    }

    /// <summary>Bring Discord up. Anything that changes what Discord shows needs this: it doesn't redraw while hidden or minimized.</summary>
    static void Front()
    {
        var a = new ActionDef { Type = "open", Path = AppPath, Window = "focus" };
        var st = AppWindows.Thread.Invoke(() => AppWindows.Smart(a, false, out _));
        if (st.Wait(3000) && st.Result == AppWindows.State.None) System.Threading.Thread.Sleep(700);   // was in the tray
        System.Threading.Thread.Sleep(300);                                                         // let it redraw
    }

    static bool OpenPicker()
    {
        Front();
        Scan();
        return Click("share your screen", "share screen");
    }

    /// <summary>
    /// Tap: share straight away. Opens Discord's picker, picks the screen or app by name ("Screen 1", "Screen 2", or an
    /// app's name), starts the stream, then puts Discord and the window you were in back the way they were.
    /// If anything doesn't turn up, Discord is left in front with the picker open so you can finish by hand.
    /// </summary>
    static void ShareScreen(string? target)
    {
        target = string.IsNullOrWhiteSpace(target) ? "Screen 1" : target.Trim();
        bool screen = target.StartsWith("screen", StringComparison.OrdinalIgnoreCase) ||
                      target.Equals("Entire Screen", StringComparison.OrdinalIgnoreCase);
        var before = Remember();

        if (!OpenPicker()) return;
        nint root = Root(MainWindow());
        if (root == 0) return;
        try
        {
            nint tab = WaitFor(() => FindIn(root, screen ? "Entire Screen" : "Applications", TabItem), 3000);
            if (tab == 0) { Log.Write("Discord: the screen picker didn't open"); return; }
            Select(tab);
            Release(tab);

            // Look only inside the picker, so an app name can't match a channel with the same name.
            nint dlg = FindType(root, WindowType);
            nint scope = dlg != 0 ? dlg : root;
            nint tile = WaitFor(() => { var t = FindIn(scope, target, Button); return t != 0 || screen ? t : FindIn(scope, target, Button, 3 /* substring */); }, 2500);
            if (dlg != 0) Release(dlg);
            if (tile == 0) { Log.Write($"Discord: nothing called \"{target}\" in the picker. It offers: {PickerChoices(root)}"); return; }
            bool ok = Invoke(tile);
            Release(tile);
            if (!ok) return;

            // Discord may ask to confirm with Go Live. Done when the picker closes.
            bool pressedGo = false;
            long until = Clock.ElapsedMilliseconds + 5000;
            while (Clock.ElapsedMilliseconds < until)
            {
                System.Threading.Thread.Sleep(200);
                nint stillOpen = FindIn(root, "Entire Screen", TabItem);
                if (stillOpen == 0)
                {
                    stillOpen = FindIn(root, "Go Live", Button);
                    if (stillOpen == 0) break;                                // picker gone: streaming
                }
                Release(stillOpen);
                if (!pressedGo)
                {
                    nint go = FindIn(root, "Go Live", Button);
                    if (go != 0) { pressedGo = Invoke(go); Release(go); Log.Write("Discord: pressed go live"); }
                }
            }
            if (Clock.ElapsedMilliseconds >= until) { Log.Write("Discord: the picker is still open. It offers: " + PickerChoices(root)); return; }
            System.Threading.Thread.Sleep(400);
            Log.Write("Discord: sharing " + target + ". Stream buttons now: " +
                      string.Join(", ", ButtonNames(root).Where(n => n.Contains("stream", StringComparison.OrdinalIgnoreCase) || n.Contains("shar", StringComparison.OrdinalIgnoreCase))));
        }
        finally { Release(root); }
        PutBack(before);
    }

    // ------------------------------------------------------------------ joining a voice channel

    readonly record struct WinState(nint Prev, bool Hidden, bool Minimized, bool Front);

    /// <summary>Where Discord and the window you're in stand before PadDeck brings Discord up.</summary>
    static WinState Remember()
    {
        var prev = User32.GetForegroundWindow();
        var main = MainWindow();
        return new WinState(prev, main == 0 || IsWindowVisible(main) == 0, main != 0 && IsIconic(main) != 0, main != 0 && prev == main);
    }

    /// <summary>Send Discord back to the tray / taskbar as it was, and go back to the window you were in.</summary>
    static void PutBack(WinState s)
    {
        var main = MainWindow();
        if (main != 0)
        {
            if (s.Hidden) ShowWindow(main, 0 /* SW_HIDE */);
            else if (s.Minimized) ShowWindow(main, 6 /* SW_MINIMIZE */);
        }
        if (!s.Front && s.Prev != 0 && s.Prev != main)
            AppWindows.Thread.Invoke(() => { AppWindows.Activate(s.Prev); return 0; }).Wait(2000);
    }

    /// <summary>
    /// Join a voice channel: by server and channel name (clicks the server, then the channel), or by a channel link
    /// (Discord → right-click the channel → Copy Link). Then puts Discord and your window back.
    /// </summary>
    static void JoinVoice(string? server, string? channel, string? link)
    {
        server = server?.Trim(); channel = channel?.Trim();
        var linkUri = ChannelLink(link);
        if (linkUri == null && (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(channel)))
        {
            Log.Write("Discord join: set a server and a voice channel on this pad");
            return;
        }
        if (Uia() == 0) return;
        var before = Remember();
        Front();
        nint root = Root(MainWindow());
        if (root == 0) return;
        bool joined = false;
        try
        {
            if (linkUri != null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(linkUri) { UseShellExecute = true })?.Dispose();
                nint join = WaitFor(() => FindIn(root, "Join Voice", Button), 4000);
                if (join == 0) { Log.Write("Discord join: no Join Voice button after opening " + linkUri); return; }
                joined = Invoke(join);
                Release(join);
            }
            else
            {
                if (!OpenServer(root, server!)) { Log.Write($"Discord join: no server called \"{server}\". You have: {string.Join(", ", ServerNames(root))}"); return; }
                nint ch = WaitFor(() => VoiceChannel(root, channel!), 3000);
                if (ch == 0) { Log.Write($"Discord join: no voice channel \"{channel}\" in {server}. It has: {string.Join(", ", VoiceChannels(root))}"); return; }
                joined = Invoke(ch);
                Release(ch);
                // Newer Discord may open the channel's page instead of joining: press its Join Voice button.
                System.Threading.Thread.Sleep(600);
                nint join = FindIn(root, "Join Voice", Button);
                if (join != 0) { Invoke(join); Release(join); }
            }
        }
        finally { Release(root); }
        if (!joined) return;
        Log.Write($"Discord: joined {(linkUri ?? server + " / " + channel)}");
        System.Threading.Thread.Sleep(500);
        Scan();
        PutBack(before);
    }

    /// <summary>https://discord.com/channels/G/C (or discord://...) → discord://-/channels/G/C, so it opens in the app.</summary>
    static string? ChannelLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(link, @"channels/(\d+)/(\d+)");
        return m.Success ? $"discord://-/channels/{m.Groups[1].Value}/{m.Groups[2].Value}" : null;
    }

    /// <summary>Server icons are unnamed: the name is text inside them ("VALO", or "Unread messages, VALO").</summary>
    static bool OpenServer(nint root, string server)
    {
        foreach (var t in FindAllIn(root, server, TextType, 3 /* substring */))
        {
            try
            {
                var name = (Name(t) ?? "").Trim();
                if (!name.Equals(server, StringComparison.OrdinalIgnoreCase) && !name.EndsWith(", " + server, StringComparison.OrdinalIgnoreCase)) continue;
                // Walk up to the clickable icon.
                nint el = t;
                AddRef(el);
                for (int up = 0; up < 5 && el != 0; up++)
                {
                    var parent = Parent(el);
                    Release(el);
                    el = parent;
                    if (el == 0) break;
                    var p = Pattern(el, 10000, IID_InvokePattern);
                    if (p != 0)
                    {
                        Release(p);
                        bool ok = Invoke(el);
                        Release(el);
                        if (ok) return true;
                        el = 0;
                    }
                }
                if (el != 0) Release(el);
            }
            finally { Release(t); }
        }
        return false;
    }

    static List<string> ServerNames(nint root)
    {
        var names = new List<string>();
        nint tree = FindType(root, 50023 /* Tree */);
        if (tree == 0) return names;
        try
        {
            foreach (var t in FindAllIn(tree, "", TextType, 3))
            {
                var n = Name(t);
                Release(t);
                if (string.IsNullOrWhiteSpace(n)) continue;
                n = n.StartsWith("Unread messages, ") ? n[17..] : n;
                if (!names.Contains(n)) names.Add(n);
            }
        }
        finally { Release(tree); }
        return names;
    }

    /// <summary>Voice channel buttons are named "Lobby (voice channel)", sometimes with more after a comma.</summary>
    static nint VoiceChannel(nint root, string channel)
    {
        string want = channel + " (voice channel)";
        nint hit = 0;
        foreach (var el in FindAllIn(root, want, Button, 3))
        {
            var n = (Name(el) ?? "").Trim();
            if (hit == 0 && (n.Equals(want, StringComparison.OrdinalIgnoreCase) || n.StartsWith(want + ",", StringComparison.OrdinalIgnoreCase))) hit = el;
            else Release(el);
        }
        return hit;
    }

    static List<string> VoiceChannels(nint root)
    {
        var names = new List<string>();
        foreach (var el in FindAllIn(root, "(voice channel)", Button, 3))
        {
            var n = Name(el) ?? "";
            Release(el);
            int i = n.IndexOf(" (voice channel)", StringComparison.Ordinal);
            if (i > 0) names.Add(n[..i]);
        }
        return names;
    }

    /// <summary>
    /// Any app: press the first button in this window with one of these names (e.g. a music app's heart).
    /// Run on this class's thread.
    /// </summary>
    public static bool ClickNamed(nint hwnd, string[] names)
    {
        nint root = Root(hwnd);
        if (root == 0) return false;
        try
        {
            foreach (var n in names)
            {
                nint el = FindKey(root, n);
                if (el == 0) continue;
                // Invoke, or toggle for toggle-style hearts.
                bool ok = Invoke(el);
                if (!ok)
                {
                    var p = Pattern(el, 10015, IID_TogglePattern);
                    if (p != 0) { ok = ((delegate* unmanaged[Stdcall]<nint, int>)Vt(p)[3])(p) == 0; Release(p); }
                }
                Release(el);
                if (ok) { Log.Write("Pressed " + n); return true; }
            }
            return false;
        }
        finally { Release(root); }
    }

    /// <summary>
    /// Any app: the name and on/off state (-1 if it has none) of the first button found by these keys.
    /// A key is a button name, or "#Id" for its automation id. Run on this class's thread.
    /// </summary>
    public static (string? name, int toggle) ReadButton(nint hwnd, string[] keys)
    {
        nint root = Root(hwnd);
        if (root == 0) return (null, -1);
        try
        {
            foreach (var k in keys)
            {
                nint el = FindKey(root, k);
                if (el == 0) continue;
                try
                {
                    int state = -1;
                    var p = Pattern(el, 10015, IID_TogglePattern);
                    if (p != 0)
                    {
                        int s = 0;
                        if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(p)[4])(p, &s) == 0) state = s;
                        Release(p);
                    }
                    return (Name(el), state);
                }
                finally { Release(el); }
            }
            return (null, -1);
        }
        finally { Release(root); }
    }

    /// <summary>Any app: move a slider (found by key, as <see cref="ReadButton"/>) by <paramref name="delta"/>. Run on this class's thread.</summary>
    public static bool NudgeSlider(nint hwnd, string[] keys, double delta)
    {
        nint root = Root(hwnd);
        if (root == 0) return false;
        try
        {
            foreach (var k in keys)
            {
                nint el = FindKey(root, k);
                if (el == 0) continue;
                var p = Pattern(el, 10003 /* RangeValue */, IID_RangeValuePattern);
                Release(el);
                if (p == 0) continue;
                try
                {
                    double cur = 0, max = 0, min = 0;
                    ((delegate* unmanaged[Stdcall]<nint, double*, int>)Vt(p)[4])(p, &cur);
                    ((delegate* unmanaged[Stdcall]<nint, double*, int>)Vt(p)[6])(p, &max);
                    ((delegate* unmanaged[Stdcall]<nint, double*, int>)Vt(p)[7])(p, &min);
                    double to = Math.Clamp(cur + delta, min, Math.Max(min, max - 1));
                    return ((delegate* unmanaged[Stdcall]<nint, double, int>)Vt(p)[3])(p, to) == 0;          // SetValue
                }
                finally { Release(p); }
            }
            return false;
        }
        finally { Release(root); }
    }

    static nint FindKey(nint root, string key)
    {
        if (!key.StartsWith('#')) return FindIn(root, key, Button);
        nint c = PropCond(30011 /* AutomationId */, key[1..], 0), found = 0;
        if (c == 0) return 0;
        try { ((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Vt(root)[5])(root, 4, c, &found); }
        finally { Release(c); }
        return found;
    }

    /// <summary>For the editor: your servers, and the voice channels of the server Discord is showing.</summary>
    public static (List<string> servers, List<string> channels) Lists()
    {
        if (Uia() == 0) return (new(), new());
        nint root = Root(MainWindow());
        if (root == 0) return (new(), new());
        try { return (ServerNames(root), VoiceChannels(root)); }
        finally { Release(root); }
    }

    /// <summary>Names of the choices in the open picker, for the log when the wanted one isn't there.</summary>
    static string PickerChoices(nint root)
    {
        nint dlg = FindType(root, WindowType);
        if (dlg == 0) return "(picker not open)";
        try { return string.Join(", ", ButtonNames(dlg)); }
        finally { Release(dlg); }
    }

    /// <summary>
    /// Voice chat: open it (bringing Discord up and going to your call), or close it if it's already open in front of you.
    /// </summary>
    static void ToggleChat()
    {
        var main = MainWindow();
        bool front = main != 0 && User32.GetForegroundWindow() == main && IsIconic(main) == 0;
        if (!front)
        {
            Front();
            if (ChatOpen()) return;                                         // it was open behind other windows: now you can see it
        }
        else if (ChatOpen())
        {
            CloseChat();
            return;
        }
        Scan();
        if (Click("show chat")) return;
        Keys("ctrl+shift+alt+v");                                           // Discord: jump back to your call
        System.Threading.Thread.Sleep(500);
        Scan();
        Click("show chat");
    }

    static bool ChatOpen()
    {
        nint root = Root(MainWindow());
        if (root == 0) return false;
        try
        {
            nint g = FindIn(root, "Text chat for voice channel", GroupType, 3 /* substring */);
            Release(g);
            return g != 0;
        }
        finally { Release(root); }
    }

    /// <summary>The chat panel's own Close button, never Discord's window Close.</summary>
    static void CloseChat()
    {
        nint root = Root(MainWindow());
        if (root == 0) return;
        var list = FindAllIn(root, "Close", Button);
        try
        {
            foreach (var el in list)
                if (!(ClassName(el) ?? "").Contains("winButton", StringComparison.Ordinal) && Invoke(el))
                {
                    Log.Write("Discord: closed voice chat");
                    return;
                }
        }
        finally
        {
            foreach (var el in list) Release(el);
            Release(root);
        }
    }

    /// <summary>Press the first of Discord's buttons that has one of these names.</summary>
    static bool Click(params string[] names)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            foreach (var n in names)
                foreach (var b in _btns)
                    if (b.Name == n && Invoke(b.El)) { Log.Write("Discord: pressed " + n); return true; }
            if (attempt == 0) Scan();
        }
        Log.Write("Discord: no button " + string.Join(" / ", names));
        return false;
    }

    /// <summary>
    /// Discord only fills in its accessibility tree once something asks for it, and not while its window has never been
    /// on screen since it started. If nothing turns up, show the window for a moment without switching to it, then hide it again.
    /// </summary>
    static void WakeUp()
    {
        System.Threading.Thread.Sleep(400);
        Scan();
        if (_seen > 0) return;
        var main = Windows().FirstOrDefault(h => IsWindowVisible(h) == 0);
        if (main == 0) return;
        ShowWindow(main, 4 /* SW_SHOWNOACTIVATE */);
        System.Threading.Thread.Sleep(900);
        Scan();
        ShowWindow(main, 0 /* SW_HIDE */);
    }

    // ------------------------------------------------------------------ reading Discord

    static void Scan()
    {
        _lastScan = Clock.ElapsedMilliseconds;
        Clear();
        _seen = 0;
        if (Uia() != 0)
            foreach (var h in Windows()) Collect(h);
        if (!ReadStates()) Set(false, false, false, false, false);
    }

    static bool ReadStates()
    {
        bool muted = false, deaf = false, cam = false, share = false, call = false;
        foreach (var b in _btns)
        {
            var name = Name(b.El);
            if (name == null) return false;                            // the button has gone: look again
            b.Name = name.Trim().ToLowerInvariant();
            switch (b.Name)
            {
                case "mute": muted |= Toggled(b.El); break;
                case "unmute": muted = true; break;
                case "deafen": deaf |= Toggled(b.El); break;
                case "undeafen": deaf = true; break;
                case "turn off camera": cam = true; break;
                case "stop streaming" or "stop sharing": share = true; break;
                case "disconnect" or "leave call": call = true; break;
            }
        }
        Set(muted, deaf, cam, share, call);
        return true;
    }

    static void Set(bool muted, bool deaf, bool cam, bool share, bool call)
    {
        bool changed = muted != Muted || deaf != Deafened || cam != Camera || share != Sharing || call != InCall;
        Muted = muted; Deafened = deaf; Camera = cam; Sharing = share; InCall = call;
        if (changed) Changed?.Invoke();
    }

    static void Collect(nint hwnd)
    {
        nint root = 0, arr = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(_uia)[6])(_uia, hwnd, &root) != 0 || root == 0) return;   // ElementFromHandle
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Vt(root)[6])(root, 4 /* Descendants */, _cond, &arr) != 0 || arr == 0) return;
            int len = 0;
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(arr)[3])(arr, &len);
            _seen += len;
            for (int i = 0; i < len; i++)
            {
                nint el = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(arr)[4])(arr, i, &el) != 0 || el == 0) continue;
                var name = Name(el)?.Trim().ToLowerInvariant();
                if (name != null && Wanted.Contains(name)) _btns.Add(new Btn { Name = name, El = el });
                else Release(el);
            }
        }
        finally
        {
            if (arr != 0) Release(arr);
            Release(root);
        }
    }

    static string? Name(nint el)
    {
        nint bstr = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(el)[23])(el, &bstr) != 0) return null;   // get_CurrentName
        if (bstr == 0) return "";
        try { return Marshal.PtrToStringBSTR(bstr); }
        finally { SysFreeString(bstr); }
    }

    static bool Toggled(nint el)
    {
        var p = Pattern(el, 10015 /* Toggle */, IID_TogglePattern);
        if (p == 0) return false;
        try
        {
            int state = 0;
            return ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(p)[4])(p, &state) == 0 && state == 1;
        }
        finally { Release(p); }
    }

    static bool Invoke(nint el)
    {
        var p = Pattern(el, 10000 /* Invoke */, IID_InvokePattern);
        if (p != 0)
        {
            try
            {
                int hr = ((delegate* unmanaged[Stdcall]<nint, int>)Vt(p)[3])(p);
                if (hr == 0) return true;
                Log.Write($"Discord: invoke failed 0x{hr:X8}");
                return false;                                  // don't also toggle: it may have gone through anyway
            }
            finally { Release(p); }
        }
        p = Pattern(el, 10015 /* Toggle */, IID_TogglePattern);
        if (p == 0) return false;
        try { return ((delegate* unmanaged[Stdcall]<nint, int>)Vt(p)[3])(p) == 0; }
        finally { Release(p); }
    }

    static nint Pattern(nint el, int id, Guid iid)
    {
        nint p = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, Guid*, nint*, int>)Vt(el)[14])(el, id, &iid, &p);  // GetCurrentPatternAs
        return hr == 0 ? p : 0;
    }

    static void Clear()
    {
        foreach (var b in _btns) Release(b.El);
        _btns.Clear();
    }

    /// <summary>Discord's top-level windows (Chromium frames with a title), shown or hidden in the tray.</summary>
    static List<nint> Windows()
    {
        var list = new List<nint>();
        var h = GCHandle.Alloc(list);
        try { EnumWindows(&EnumCb, GCHandle.ToIntPtr(h)); }
        finally { h.Free(); }
        return list;
    }

    [ThreadStatic] static Dictionary<uint, bool>? _isDiscord;

    [UnmanagedCallersOnly]
    static int EnumCb(nint hwnd, nint l)
    {
        if (GetWindowTextLength(hwnd) == 0) return 1;
        char* cls = stackalloc char[32];
        int n = GetClassName(hwnd, cls, 32);
        if (!new ReadOnlySpan<char>(cls, n).SequenceEqual("Chrome_WidgetWin_1")) return 1;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        _isDiscord ??= new();
        if (!_isDiscord.TryGetValue(pid, out var yes))
        {
            var exe = Gdi.ProcessPath(pid);
            yes = exe != null && Path.GetFileName(exe).Equals("Discord.exe", StringComparison.OrdinalIgnoreCase);
            if (_isDiscord.Count > 64) _isDiscord.Clear();
            _isDiscord[pid] = yes;
        }
        if (yes) ((List<nint>)GCHandle.FromIntPtr(l).Target!).Add(hwnd);
        return 1;
    }

    // ------------------------------------------------------------------ finding things

    const int Button = 50000, TextType = 50020, GroupType = 50026, TabItem = 50019, WindowType = 50032;

    static nint _walker;

    static nint Parent(nint el)
    {
        if (_walker == 0)
        {
            nint w = 0;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(_uia)[14])(_uia, &w) != 0) return 0;   // get_ControlViewWalker
            _walker = w;
        }
        nint parent = 0;
        return ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(_walker)[3])(_walker, el, &parent) == 0 ? parent : 0;  // GetParentElement
    }

    static void AddRef(nint p) { if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vt(p)[1])(p); }

    static nint MainWindow()
    {
        foreach (var h in Windows()) return h;
        return 0;
    }

    static nint Root(nint hwnd)
    {
        nint root = 0;
        if (hwnd == 0 || Uia() == 0) return 0;
        return ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Vt(_uia)[6])(_uia, hwnd, &root) == 0 ? root : 0;
    }

    /// <summary>First element under <paramref name="root"/> with this name (flags: 1 ignore case, 2 substring) and control type.</summary>
    static nint FindIn(nint root, string name, int type, int flags = 1)
    {
        nint c = AndCond(NameCond(name, flags), TypeCond(type)), found = 0;
        if (c == 0) return 0;
        try { ((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Vt(root)[5])(root, 4 /* Descendants */, c, &found); }
        finally { Release(c); }
        return found;
    }

    static nint FindType(nint root, int type)
    {
        nint c = TypeCond(type), found = 0;
        if (c == 0) return 0;
        try { ((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Vt(root)[5])(root, 4, c, &found); }
        finally { Release(c); }
        return found;
    }

    /// <summary>Every element of this type, with this name (flags as <see cref="FindIn"/>), or any name when it's empty.</summary>
    static List<nint> FindAllIn(nint root, string name, int type, int flags = 1)
    {
        var list = new List<nint>();
        nint c = name.Length == 0 ? TypeCond(type) : AndCond(NameCond(name, flags), TypeCond(type)), arr = 0;
        if (c == 0) return list;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Vt(root)[6])(root, 4, c, &arr) != 0 || arr == 0) return list;
            int len = 0;
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(arr)[3])(arr, &len);
            for (int i = 0; i < len; i++)
            {
                nint el = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(arr)[4])(arr, i, &el) == 0 && el != 0) list.Add(el);
            }
        }
        finally { Release(c); Release(arr); }
        return list;
    }

    static List<string> ButtonNames(nint root)
    {
        var names = new List<string>();
        nint c = TypeCond(Button), arr = 0;
        if (c == 0) return names;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Vt(root)[6])(root, 4, c, &arr) != 0 || arr == 0) return names;
            int len = 0;
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)Vt(arr)[3])(arr, &len);
            for (int i = 0; i < len; i++)
            {
                nint el = 0;
                if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Vt(arr)[4])(arr, i, &el) != 0 || el == 0) continue;
                if (Name(el) is { Length: > 0 } n) names.Add(n);
                Release(el);
            }
        }
        finally { Release(c); Release(arr); }
        return names;
    }

    static nint WaitFor(Func<nint> find, int ms)
    {
        long until = Clock.ElapsedMilliseconds + ms;
        while (true)
        {
            var r = find();
            if (r != 0 || Clock.ElapsedMilliseconds > until) return r;
            System.Threading.Thread.Sleep(120);
        }
    }

    static void Select(nint el)
    {
        var p = Pattern(el, 10010 /* SelectionItem */, IID_SelectionItemPattern);
        if (p == 0) { Invoke(el); return; }
        try { ((delegate* unmanaged[Stdcall]<nint, int>)Vt(p)[3])(p); }
        finally { Release(p); }
    }

    static string? ClassName(nint el)
    {
        nint bstr = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vt(el)[30])(el, &bstr) != 0 || bstr == 0) return null;   // get_CurrentClassName
        try { return Marshal.PtrToStringBSTR(bstr); }
        finally { SysFreeString(bstr); }
    }

    static nint NameCond(string name, int flags) => PropCond(30005 /* Name */, name, flags);

    static nint PropCond(int prop, string value, int flags)
    {
        if (Uia() == 0) return 0;
        var v = new Variant { vt = 8 /* VT_BSTR */, p = SysAllocString(value) };
        nint c = 0;
        try { ((delegate* unmanaged[Stdcall]<nint, int, Variant, int, nint*, int>)Vt(_uia)[24])(_uia, prop, v, flags, &c); }
        finally { SysFreeString(v.p); }
        return c;
    }

    static nint TypeCond(int type)
    {
        var v = new Variant { vt = 3 /* VT_I4 */, i4 = type };
        nint c = 0;
        ((delegate* unmanaged[Stdcall]<nint, int, Variant, nint*, int>)Vt(_uia)[23])(_uia, 30003 /* ControlType */, v, &c);
        return c;
    }

    /// <summary>Both conditions; releases the two parts.</summary>
    static nint AndCond(nint a, nint b)
    {
        nint c = 0;
        try
        {
            if (a == 0 || b == 0) return 0;
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Vt(_uia)[25])(_uia, a, b, &c);
            return c;
        }
        finally { Release(a); Release(b); }
    }

    // ------------------------------------------------------------------ COM

    static nint Uia()
    {
        if (_uia != 0) return _uia;
        if (Ole32.CoCreateInstance(CLSID_CUIAutomation, 0, 1 /* INPROC */, IID_IUIAutomation, out var u) != 0 || u == 0) return 0;
        var v = new Variant { vt = 3 /* VT_I4 */, i4 = 50000 /* Button */ };
        nint cond = 0;
        // CreatePropertyCondition(UIA_ControlTypePropertyId, Button)
        if (((delegate* unmanaged[Stdcall]<nint, int, Variant, nint*, int>)Vt(u)[23])(u, 30003, v, &cond) != 0 || cond == 0)
        {
            Release(u);
            return 0;
        }
        _cond = cond;
        return _uia = u;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public int i4; [FieldOffset(8)] public nint p; }

    static nint* Vt(nint p) => *(nint**)p;
    static void Release(nint p) { if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Vt(p)[2])(p); }

    static readonly Guid CLSID_CUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    static readonly Guid IID_IUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    static readonly Guid IID_InvokePattern = new("FB377FBE-8EA6-46D5-9C73-6499642D3059");
    static readonly Guid IID_TogglePattern = new("94CF8058-9B8D-4AB9-8BFD-4CD0A33C8C70");
    static readonly Guid IID_SelectionItemPattern = new("A8EFA66A-0FDA-421A-9194-38021F3578EA");
    static readonly Guid IID_RangeValuePattern = new("59213F4F-7346-49E5-B120-80555987A148");

    [LibraryImport("user32.dll")] private static partial int EnumWindows(delegate* unmanaged<nint, nint, int> cb, nint l);
    [LibraryImport("user32.dll")] private static partial int IsWindowVisible(nint h);
    [LibraryImport("user32.dll")] private static partial int ShowWindow(nint h, int cmd);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static partial int GetWindowTextLength(nint h);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")] private static partial int GetClassName(nint h, char* buf, int max);
    [LibraryImport("oleaut32.dll")] private static partial void SysFreeString(nint bstr);
    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint SysAllocString(string s);
    [LibraryImport("user32.dll")] private static partial int IsIconic(nint h);
}
