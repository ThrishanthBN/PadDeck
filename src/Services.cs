using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PadDeck;

/// <summary>One background thread that runs queued work in order.</summary>
internal sealed class Worker : IDisposable
{
    readonly BlockingCollection<Action> _q = new();
    readonly Thread _t;

    public Worker(string name, ThreadPriority priority = ThreadPriority.Normal, Action? init = null)
    {
        _t = new Thread(() =>
        {
            init?.Invoke();
            foreach (var a in _q.GetConsumingEnumerable())
            {
                try { a(); } catch (Exception ex) { Log.Write($"{name}: {ex.Message}"); }
            }
        }) { IsBackground = true, Name = name, Priority = priority };
        _t.Start();
    }

    public int Pending => _q.Count;
    public void Post(Action a) { if (!_q.IsAddingCompleted) _q.Add(a); }

    public Task<T> Invoke<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }

    public void Dispose() => _q.CompleteAdding();
}

// ---------------------------------------------------------------------------
// Keyboard
// ---------------------------------------------------------------------------
internal static unsafe class Keyboard
{
    public readonly record struct Key(ushort Vk, bool Ext);

    static readonly Dictionary<string, Key> Map = BuildMap();

    static Dictionary<string, Key> BuildMap()
    {
        var m = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        void K(ushort vk, bool ext, params string[] names) { foreach (var n in names) m[n] = new Key(vk, ext); }
        K(0xA2, false, "ctrl", "control", "lctrl");
        K(0xA3, true, "rctrl");
        K(0xA0, false, "shift", "lshift");
        K(0xA1, false, "rshift");
        K(0xA4, false, "alt", "lalt", "menu");
        K(0xA5, true, "ralt", "altgr");
        K(0x5B, true, "win", "windows", "meta", "cmd", "super", "lwin");
        for (char c = 'A'; c <= 'Z'; c++) K(c, false, c.ToString());
        for (char c = '0'; c <= '9'; c++) K(c, false, c.ToString());
        for (int i = 1; i <= 24; i++) K((ushort)(0x6F + i), false, "F" + i);
        K(0x0D, false, "enter", "return"); K(0x1B, false, "esc", "escape"); K(0x09, false, "tab");
        K(0x20, false, "space"); K(0x08, false, "backspace", "back");
        K(0x2E, true, "delete", "del"); K(0x2D, true, "insert", "ins");
        K(0x24, true, "home"); K(0x23, true, "end"); K(0x21, true, "pageup", "pgup"); K(0x22, true, "pagedown", "pgdn");
        K(0x26, true, "up", "arrowup"); K(0x28, true, "down", "arrowdown"); K(0x25, true, "left", "arrowleft"); K(0x27, true, "right", "arrowright");
        K(0x2C, true, "printscreen", "prtsc", "print"); K(0x13, false, "pause", "break"); K(0x14, false, "capslock");
        K(0x90, true, "numlock"); K(0x91, false, "scrolllock"); K(0x5D, true, "apps", "contextmenu");
        for (int i = 0; i <= 9; i++) K((ushort)(0x60 + i), false, "num" + i, "numpad" + i);
        K(0x6A, false, "nummultiply", "numpadmultiply"); K(0x6B, false, "numadd", "numpadadd", "numplus");
        K(0x6D, false, "numsubtract", "numpadsubtract", "numminus"); K(0x6E, false, "numdecimal", "numpaddecimal");
        K(0x6F, true, "numdivide", "numpaddivide");
        K(0xBD, false, "-", "minus"); K(0xBB, false, "=", "equal", "plus"); K(0xDB, false, "[", "bracketleft");
        K(0xDD, false, "]", "bracketright"); K(0xDC, false, "\\", "backslash"); K(0xBA, false, ";", "semicolon");
        K(0xDE, false, "'", "quote"); K(0xBC, false, ",", "comma"); K(0xBE, false, ".", "period");
        K(0xBF, false, "/", "slash"); K(0xC0, false, "`", "backquote", "grave");
        K(0xB3, true, "mediaplaypause", "playpause"); K(0xB0, true, "medianext", "nexttrack");
        K(0xB1, true, "mediaprev", "mediaprevious", "prevtrack"); K(0xB2, true, "mediastop");
        K(0xAF, true, "volumeup"); K(0xAE, true, "volumedown"); K(0xAD, true, "volumemute");
        return m;
    }

    public static List<Key>? Parse(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        var keys = new List<Key>();
        foreach (var raw in combo.Split('+'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (Map.TryGetValue(t, out var k)) keys.Add(k);
            else if (t.StartsWith("vk", StringComparison.OrdinalIgnoreCase) &&
                     ushort.TryParse(t.AsSpan(2).TrimStart("0x"), System.Globalization.NumberStyles.HexNumber, null, out var vk))
                keys.Add(new Key(vk, false));
            else return null;
        }
        return keys.Count > 0 ? keys : null;
    }

    static User32.INPUT Make(Key k, bool up)
    {
        var i = new User32.INPUT { type = User32.INPUT_KEYBOARD };
        i.u.ki.wVk = k.Vk;
        i.u.ki.wScan = (ushort)User32.MapVirtualKey(k.Vk, 0);
        i.u.ki.dwFlags = (k.Ext ? User32.KEYEVENTF_EXTENDEDKEY : 0) | (up ? User32.KEYEVENTF_KEYUP : 0);
        return i;
    }

    static void Send(Span<User32.INPUT> inputs)
    {
        fixed (User32.INPUT* p = inputs) User32.SendInput((uint)inputs.Length, p, sizeof(User32.INPUT));
    }

    public static void Down(IReadOnlyList<Key> keys)
    {
        Span<User32.INPUT> buf = stackalloc User32.INPUT[keys.Count];
        for (int i = 0; i < keys.Count; i++) buf[i] = Make(keys[i], false);
        Send(buf);
    }

    public static void Up(IReadOnlyList<Key> keys)
    {
        Span<User32.INPUT> buf = stackalloc User32.INPUT[keys.Count];
        for (int i = 0; i < keys.Count; i++) buf[i] = Make(keys[keys.Count - 1 - i], true);
        Send(buf);
    }

    /// <summary>Press and release. Held briefly so apps that poll key state (OBS, games) still see it.</summary>
    public static void Tap(IReadOnlyList<Key> keys, int holdMs)
    {
        Down(keys);
        Thread.Sleep(holdMs);
        Up(keys);
    }

    public static void TypeText(string text)
    {
        Span<User32.INPUT> buf = stackalloc User32.INPUT[2];
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n') { Tap(new[] { new Key(0x0D, false) }, 2); continue; }
            for (int j = 0; j < 2; j++)
            {
                buf[j] = new User32.INPUT { type = User32.INPUT_KEYBOARD };
                buf[j].u.ki.wScan = ch;
                buf[j].u.ki.dwFlags = User32.KEYEVENTF_UNICODE | (j == 1 ? User32.KEYEVENTF_KEYUP : 0);
            }
            Send(buf);
        }
    }
}

// ---------------------------------------------------------------------------
// Audio: master/mic endpoint volume and per-app session volume.
// All COM work happens on one MTA thread.
// ---------------------------------------------------------------------------
internal sealed class Audio : IDisposable
{
    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    static readonly Guid Ctx = Guid.Empty;
    const int eRender = 0, eCapture = 1, eConsole = 0, DEVICE_STATE_ACTIVE = 1;

    public readonly Worker Thread = new("audio", ThreadPriority.AboveNormal,
        () => Ole32.CoInitializeEx(0, Ole32.COINIT_MULTITHREADED));

    IMMDeviceEnumerator? _en;
    readonly Dictionary<uint, string> _pidNames = new();

    IMMDeviceEnumerator En
    {
        get
        {
            if (_en != null) return _en;
            int hr = Ole32.CoCreateInstance(CLSID_MMDeviceEnumerator, 0, Ole32.CLSCTX_ALL, IID_IMMDeviceEnumerator, out var p);
            if (hr != 0) throw new InvalidOperationException($"Audio unavailable (0x{hr:X8})");
            return _en = Com.Wrap<IMMDeviceEnumerator>(p);
        }
    }

    IAudioEndpointVolume? Endpoint(int flow)
    {
        if (En.GetDefaultAudioEndpoint(flow, eConsole, out var dev) != 0 || dev == null) return null;
        try
        {
            if (dev.Activate(IID_IAudioEndpointVolume, Ole32.CLSCTX_ALL, 0, out var p) != 0) return null;
            return Com.Wrap<IAudioEndpointVolume>(p);
        }
        finally { Com.Release(dev); }
    }

    string ProcessName(uint pid)
    {
        if (pid == 0) return "system";
        if (_pidNames.TryGetValue(pid, out var n)) return n;
        if (_pidNames.Count > 512) _pidNames.Clear();
        try { using var p = Process.GetProcessById((int)pid); n = p.ProcessName.ToLowerInvariant(); }
        catch { n = ""; }
        _pidNames[pid] = n;
        return n;
    }

    static string Norm(string target)
    {
        var t = target.Trim().ToLowerInvariant();
        return t.EndsWith(".exe") ? t[..^4] : t;
    }

    string? FocusedProcess()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == 0) return null;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        var n = ProcessName(pid);
        return n.Length > 0 ? n : null;
    }

    // The per-poll session walk calls the audio interfaces directly and releases each object straight away.
    // Going through .NET's COM wrappers here created objects several times a second and leaked about 1.3 MB a minute.

    /// <summary>ISimpleAudioVolume (an app's volume and mute in the mixer), called through its raw interface.</summary>
    internal readonly unsafe struct SimpleVol
    {
        readonly nint _p;
        public SimpleVol(nint p) => _p = p;
        nint* V => *(nint**)_p;
        public int SetMasterVolume(float level, in Guid ctx) { var c = ctx; return ((delegate* unmanaged[Stdcall]<nint, float, Guid*, int>)V[3])(_p, level, &c); }
        public int GetMasterVolume(out float level) { float l = 0; int hr = ((delegate* unmanaged[Stdcall]<nint, float*, int>)V[4])(_p, &l); level = l; return hr; }
        public int SetMute(int mute, in Guid ctx) { var c = ctx; return ((delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)V[5])(_p, mute, &c); }
        public int GetMute(out int mute) { int m = 0; int hr = ((delegate* unmanaged[Stdcall]<nint, int*, int>)V[6])(_p, &m); mute = m; return hr; }
    }

    static readonly Guid IID_SimpleVol = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    static unsafe nint QI(nint p, Guid iid)
    {
        nint o = 0;
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)p)[0])(p, &iid, &o) == 0 ? o : 0;
    }

    static unsafe void Rel(nint p) { if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)p)[2])(p); }

    /// <summary>IAudioSessionControl::GetState: 0 inactive, 1 active (playing), 2 expired.</summary>
    static unsafe int SessionState(nint ctl)
    {
        int s = 0;
        return ((delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)ctl)[3])(ctl, &s) == 0 ? s : 2;
    }

    /// <summary>Visit every audio session (on every active output device) whose process matches.</summary>
    void ForEachSession(Func<string, bool> match, Action<SimpleVol> visit) =>
        Walk((ctl, pid, name) =>
        {
            if (!match(name)) return;
            var v = QI(ctl, IID_SimpleVol);
            if (v == 0) return;
            try { visit(new SimpleVol(v)); }
            finally { Rel(v); }
        });

    // Session managers are cached per output device: activating them on every poll churns native memory.
    readonly Dictionary<string, nint> _mgrs = new();
    List<nint> _mgrList = new();
    long _mgrsAt;
    bool _mgrsLoaded;

    List<nint> Managers()
    {
        long now = Environment.TickCount64;
        if (_mgrsLoaded && now - _mgrsAt < 5000) return _mgrList;
        _mgrsLoaded = true;
        _mgrsAt = now;
        var seen = new HashSet<string>();
        if (En.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var col) == 0 && col != null)
        {
            try
            {
                col.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    if (col.Item(i, out var dev) != 0 || dev == null) continue;
                    try
                    {
                        var id = DeviceId(dev);
                        if (id == null) continue;
                        seen.Add(id);
                        if (!_mgrs.ContainsKey(id) && dev.Activate(IID_IAudioSessionManager2, Ole32.CLSCTX_ALL, 0, out var pm) == 0 && pm != 0)
                            _mgrs[id] = pm;                                   // keeps the reference Activate gave us
                    }
                    finally { Com.Release(dev); }
                }
            }
            finally { Com.Release(col); }
        }
        foreach (var gone in _mgrs.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Rel(_mgrs[gone]);
            _mgrs.Remove(gone);
        }
        _mgrList = _mgrs.Values.ToList();
        return _mgrList;
    }

    /// <summary>Every audio session: IAudioSessionManager2 → session enumerator → IAudioSessionControl2, all raw.</summary>
    unsafe void Walk(Action<nint, uint, string> visit)
    {
        foreach (var mgr in Managers())
        {
            nint en = 0;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)mgr)[5])(mgr, &en) != 0 || en == 0) continue;   // GetSessionEnumerator
            try
            {
                int n = 0;
                ((delegate* unmanaged[Stdcall]<nint, int*, int>)(*(nint**)en)[3])(en, &n);                                  // GetCount
                for (int i = 0; i < n; i++)
                {
                    nint ctl = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)(*(nint**)en)[4])(en, i, &ctl) != 0 || ctl == 0) continue;   // GetSession
                    try
                    {
                        var ctl2 = QI(ctl, IID_SessionControl2);
                        if (ctl2 == 0) continue;
                        try
                        {
                            uint pid = 0;
                            ((delegate* unmanaged[Stdcall]<nint, uint*, int>)(*(nint**)ctl2)[14])(ctl2, &pid);              // GetProcessId
                            visit(ctl2, pid, ProcessName(pid));
                        }
                        finally { Rel(ctl2); }
                    }
                    finally { Rel(ctl); }
                }
            }
            finally { Rel(en); }
        }
    }

    static readonly Guid IID_SessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");

    /// <summary>Level and mute for several apps in one pass over the sessions (keys: the targets as given).</summary>
    public void AppStates(IReadOnlyCollection<string> targets, Dictionary<string, (float level, bool mute)> into)
    {
        into.Clear();
        if (targets.Count == 0) return;
        Walk((ctl, pid, name) =>
        {
            foreach (var t in targets)
            {
                if (into.ContainsKey(t) || Norm(t) != name) continue;
                var p = QI(ctl, IID_SimpleVol);
                if (p == 0) continue;
                try
                {
                    var v = new SimpleVol(p);
                    if (v.GetMasterVolume(out var l) == 0 && v.GetMute(out var m) == 0) into[t] = (l, m != 0);
                }
                finally { Rel(p); }
            }
        });
    }

    // ---- queries (call on Thread) ----

    public float? GetLevel(string target)
    {
        var t = Norm(target);
        if (t is "master" or "mic")
        {
            var ep = Endpoint(t == "mic" ? eCapture : eRender);
            if (ep == null) return null;
            try { ep.GetMasterVolumeLevelScalar(out var v); return v; } finally { Com.Release(ep); }
        }
        if (t == "focused") t = FocusedProcess() ?? "";
        if (t.Length == 0) return null;
        float? level = null;
        ForEachSession(n => n == t, v => { if (level == null && v.GetMasterVolume(out var l) == 0) level = l; });
        return level;
    }

    public bool? GetMute(string target)
    {
        var t = Norm(target);
        if (t is "master" or "mic")
        {
            var ep = Endpoint(t == "mic" ? eCapture : eRender);
            if (ep == null) return null;
            try { ep.GetMute(out var m); return m != 0; } finally { Com.Release(ep); }
        }
        if (t == "focused") t = FocusedProcess() ?? "";
        if (t.Length == 0) return null;
        bool? muted = null;
        ForEachSession(n => n == t, v => { if (muted == null && v.GetMute(out var m) == 0) muted = m != 0; });
        return muted;
    }

    /// <summary>op: up | down | set | mute | unmute | togglemute. value: percent.</summary>
    public void Apply(string target, string op, double? value)
    {
        var t = Norm(target);
        op = op.ToLowerInvariant();
        float step = (float)((value ?? 5) / 100.0);

        if (t is "master" or "mic")
        {
            var ep = Endpoint(t == "mic" ? eCapture : eRender);
            if (ep == null) return;
            try
            {
                ep.GetMasterVolumeLevelScalar(out var cur);
                ep.GetMute(out var m);
                switch (op)
                {
                    case "up": ep.SetMasterVolumeLevelScalar(Math.Clamp(cur + step, 0, 1), Ctx); if (m != 0) ep.SetMute(0, Ctx); break;
                    case "down": ep.SetMasterVolumeLevelScalar(Math.Clamp(cur - step, 0, 1), Ctx); break;
                    case "set": ep.SetMasterVolumeLevelScalar(Math.Clamp((float)((value ?? 50) / 100.0), 0, 1), Ctx); break;
                    case "mute": ep.SetMute(1, Ctx); break;
                    case "unmute": ep.SetMute(0, Ctx); break;
                    case "togglemute": ep.SetMute(m != 0 ? 0 : 1, Ctx); break;
                }
            }
            finally { Com.Release(ep); }
            return;
        }

        if (t == "focused") t = FocusedProcess() ?? "";
        if (t.Length == 0) return;
        bool? toggleTo = null;
        ForEachSession(n => n == t, v =>
        {
            v.GetMasterVolume(out var cur);
            v.GetMute(out var m);
            switch (op)
            {
                case "up": v.SetMasterVolume(Math.Clamp(cur + step, 0, 1), Ctx); break;
                case "down": v.SetMasterVolume(Math.Clamp(cur - step, 0, 1), Ctx); break;
                case "set": v.SetMasterVolume(Math.Clamp((float)((value ?? 50) / 100.0), 0, 1), Ctx); break;
                case "mute": v.SetMute(1, Ctx); break;
                case "unmute": v.SetMute(0, Ctx); break;
                case "togglemute":
                    toggleTo ??= m == 0;          // all sessions of the app follow the first one
                    v.SetMute(toggleTo.Value ? 1 : 0, Ctx); break;
            }
        });
    }

    /// <summary>Set a level from a fader pad. Unmutes when raising above zero.</summary>
    public void SetLevel(string target, float level)
    {
        Apply(target, "set", level * 100);
        if (level > 0.001f && GetMute(target) == true) Apply(target, "unmute", null);
    }

    public List<string> Sessions()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        ForEachSession(n => { if (n.Length > 0 && n != "system") set.Add(n); return false; }, _ => { });
        return set.ToList();
    }

    // ---- running apps (auto mixer) ----

    public sealed record AppAudio(string Name, string Label, uint Color, bool Playing);
    readonly Dictionary<string, (string label, uint color)> _appInfo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every app with an audio session right now, with a friendly name and its icon colour.</summary>
    public List<AppAudio> Apps()
    {
        var found = new Dictionary<string, (uint pid, bool playing)>(StringComparer.OrdinalIgnoreCase);
        Walk((ctl, pid, name) =>
        {
            if (name.Length == 0 || name == "system" || name == "paddeck") return;
            int st = SessionState(ctl);
            if (st == 2) return;                                     // expired
            bool playing = st == 1;
            if (found.TryGetValue(name, out var cur)) found[name] = (cur.pid, cur.playing || playing);
            else found[name] = (pid, playing);
        });
        var list = new List<AppAudio>();
        foreach (var (name, (pid, playing)) in found)
        {
            if (!_appInfo.TryGetValue(name, out var info))
            {
                string label = char.ToUpperInvariant(name[0]) + name[1..];
                uint color = 0xE2E8F0;
                var path = Gdi.ProcessPath(pid);
                if (path != null)
                {
                    try
                    {
                        var desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                        if (!string.IsNullOrEmpty(desc) && desc.Length <= 24) label = desc;
                    }
                    catch { }
                    try { color = Gdi.IconColor(path); } catch { }
                }
                _appInfo[name] = info = (label, color);
            }
            list.Add(new AppAudio(name, info.label, info.color, playing));
        }
        return list;
    }

    // ---- audio devices ----

    public sealed record Device(string Id, string Name, bool IsDefault);
    static readonly PROPERTYKEY PKEY_FriendlyName = new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

    static string? DeviceId(IMMDevice dev) => dev.GetId(out var p) == 0 ? Com.TakeString(p) : null;

    static string DeviceName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var store) != 0 || store == null) return "Unknown device";
        try
        {
            if (store.GetValue(PKEY_FriendlyName, out var pv) != 0) return "Unknown device";
            try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) ?? "Unknown device" : "Unknown device"; }
            finally { Ole32.PropVariantClear(ref pv); }
        }
        finally { Com.Release(store); }
    }

    public string? DefaultId(bool input)
    {
        if (En.GetDefaultAudioEndpoint(input ? eCapture : eRender, eConsole, out var dev) != 0 || dev == null) return null;
        try { return DeviceId(dev); } finally { Com.Release(dev); }
    }

    public List<Device> Devices(bool input)
    {
        var def = DefaultId(input);
        var list = new List<Device>();
        if (En.EnumAudioEndpoints(input ? eCapture : eRender, DEVICE_STATE_ACTIVE, out var col) != 0 || col == null) return list;
        try
        {
            col.GetCount(out var n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) != 0 || dev == null) continue;
                try
                {
                    var id = DeviceId(dev);
                    if (id != null) list.Add(new Device(id, DeviceName(dev), id == def));
                }
                finally { Com.Release(dev); }
            }
        }
        finally { Com.Release(col); }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    static readonly Guid CLSID_PolicyConfig = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    static readonly Guid IID_PolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");

    static unsafe void SetDefault(string id)
    {
        if (Ole32.CoCreateInstance(CLSID_PolicyConfig, 0, Ole32.CLSCTX_ALL, IID_PolicyConfig, out var p) != 0) return;
        var pc = Com.Wrap<IPolicyConfig>(p);
        try
        {
            fixed (char* s = id)
                for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint((nint)s, role);   // console, multimedia, communications
        }
        finally { Com.Release(pc); }
    }

    /// <summary>op: next | prev | set. For set, match by id first, then by name (ids survive reboots, names are a fallback).</summary>
    public void SwitchDevice(bool input, string op, string? id, string? name)
    {
        var devs = Devices(input);
        if (devs.Count == 0) return;
        Device? target = null;
        if (op == "set")
            target = devs.FirstOrDefault(d => d.Id == id) ?? devs.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        else
        {
            int cur = devs.FindIndex(d => d.IsDefault);
            int next = ((cur < 0 ? 0 : cur) + (op == "prev" ? -1 : 1) + devs.Count) % devs.Count;
            target = devs[next];
        }
        if (target != null && !target.IsDefault) SetDefault(target.Id);
    }

    public void Dispose() => Thread.Dispose();
}

// ---------------------------------------------------------------------------
// OBS Studio (obs-websocket v5, built into OBS 28+)
// ---------------------------------------------------------------------------
internal sealed class Obs : IDisposable
{
    public event Action? Changed;

    public volatile bool Connected;
    public string Status { get; private set; } = "Off";
    public volatile bool Recording, Streaming, ReplayActive, VirtualCam;
    public volatile string? Scene;
    readonly ConcurrentDictionary<string, bool> _muted = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    HashSet<string> _watchInputs = new(StringComparer.OrdinalIgnoreCase);

    ClientWebSocket? _ws;
    CancellationTokenSource? _cts;
    (bool enabled, string url, string? pw) _cfg;
    int _reqId;

    public bool IsMuted(string? input) => input != null && _muted.TryGetValue(input, out var m) && m;

    public void Configure(bool enabled, string url, string? password)
    {
        var next = (enabled, url, password);
        if (next == _cfg && (_cts != null || !enabled)) return;
        _cfg = next;
        _cts?.Cancel();
        _cts = null;
        Connected = false;
        if (!enabled) { SetStatus("Off"); return; }
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunLoop(cts.Token));
    }

    public void WatchInputs(IEnumerable<string> inputs)
    {
        _watchInputs = new HashSet<string>(inputs.Where(i => !string.IsNullOrWhiteSpace(i)), StringComparer.OrdinalIgnoreCase);
        if (Connected) _ = RefreshMutes();
    }

    void SetStatus(string s) { Status = s; Changed?.Invoke(); }

    async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                _ws = ws;
                SetStatus("Connecting…");
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(4000);
                    await ws.ConnectAsync(new Uri(_cfg.url), connectCts.Token);
                }
                await ReceiveLoop(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (Status != "Wrong password") SetStatus("Not reachable. Is OBS open, with its WebSocket server on?");
                Log.Write("obs: " + ex.Message);
            }
            Connected = false;
            foreach (var p in _pending) p.Value.TrySetResult(null);
            _pending.Clear();
            Changed?.Invoke();
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    if (ws.CloseStatus == (WebSocketCloseStatus)4009) SetStatus("Wrong password");
                    return;
                }
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);

            JsonNode? msg;
            try { msg = JsonNode.Parse(ms.ToArray()); } catch { continue; }
            if (msg == null) continue;
            int op = (int?)msg["op"] ?? -1;
            var d = msg["d"];
            switch (op)
            {
                case 0: await Identify(d); break;
                case 2:
                    Connected = true;
                    SetStatus("Connected");
                    _ = RefreshAll();
                    break;
                case 5: OnEvent((string?)d?["eventType"], d?["eventData"]); break;
                case 7:
                    var id = (string?)d?["requestId"];
                    if (id != null && _pending.TryRemove(id, out var tcs))
                        tcs.TrySetResult((bool?)d?["requestStatus"]?["result"] == true ? d?["responseData"] ?? new JsonObject() : null);
                    break;
            }
        }
    }

    async Task Identify(JsonNode? hello)
    {
        var ident = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 4 | 8 | 64 }; // scenes, inputs, outputs
        var auth = hello?["authentication"];
        if (auth != null)
        {
            var pw = _cfg.pw ?? "";
            var salt = (string?)auth["salt"] ?? "";
            var challenge = (string?)auth["challenge"] ?? "";
            var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pw + salt)));
            ident["authentication"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
        }
        await SendRaw(new JsonObject { ["op"] = 1, ["d"] = ident });
    }

    void OnEvent(string? type, JsonNode? data)
    {
        bool active = (bool?)data?["outputActive"] ?? false;
        switch (type)
        {
            case "RecordStateChanged": Recording = active; break;
            case "StreamStateChanged": Streaming = active; break;
            case "ReplayBufferStateChanged": ReplayActive = active; break;
            case "VirtualcamStateChanged": VirtualCam = active; break;
            case "CurrentProgramSceneChanged": Scene = (string?)data?["sceneName"]; break;
            case "InputMuteStateChanged":
                var name = (string?)data?["inputName"];
                if (name != null) _muted[name] = (bool?)data?["inputMuted"] ?? false;
                break;
            default: return;
        }
        Changed?.Invoke();
    }

    async Task RefreshAll()
    {
        Recording = (bool?)(await Request("GetRecordStatus"))?["outputActive"] ?? false;
        Streaming = (bool?)(await Request("GetStreamStatus"))?["outputActive"] ?? false;
        ReplayActive = (bool?)(await Request("GetReplayBufferStatus"))?["outputActive"] ?? false;
        VirtualCam = (bool?)(await Request("GetVirtualCamStatus"))?["outputActive"] ?? false;
        var sc = await Request("GetCurrentProgramScene");
        Scene = (string?)sc?["sceneName"] ?? (string?)sc?["currentProgramSceneName"];
        await RefreshMutes();
        Changed?.Invoke();
    }

    async Task RefreshMutes()
    {
        foreach (var input in _watchInputs.ToArray())
        {
            var r = await Request("GetInputMute", new JsonObject { ["inputName"] = input });
            if (r != null) _muted[input] = (bool?)r["inputMuted"] ?? false;
        }
        Changed?.Invoke();
    }

    async Task SendRaw(JsonObject msg)
    {
        var ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception ex) { Log.Write("obs send: " + ex.Message); }
        finally { _sendLock.Release(); }
    }

    public async Task<JsonNode?> Request(string type, JsonObject? data = null, int timeoutMs = 3000)
    {
        if (!Connected) return null;
        var id = Interlocked.Increment(ref _reqId).ToString();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var d = new JsonObject { ["requestType"] = type, ["requestId"] = id };
        if (data != null) d["requestData"] = data;
        await SendRaw(new JsonObject { ["op"] = 6, ["d"] = d });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        _pending.TryRemove(id, out _);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    /// <summary>Run an OBS action. Fire-and-forget so the pad never waits on the network.</summary>
    public void Do(string? op, string? target)
    {
        if (!Connected) return;
        JsonObject? Input(bool? muted = null)
        {
            var o = new JsonObject { ["inputName"] = target ?? "" };
            if (muted != null) o["inputMuted"] = muted.Value;
            return o;
        }
        (string type, JsonObject? data) req = (op ?? "").ToLowerInvariant() switch
        {
            "scene" => ("SetCurrentProgramScene", new JsonObject { ["sceneName"] = target ?? "" }),
            "preview_scene" => ("SetCurrentPreviewScene", new JsonObject { ["sceneName"] = target ?? "" }),
            "transition" => ("TriggerStudioModeTransition", null),
            "record_toggle" => ("ToggleRecord", null),
            "record_start" => ("StartRecord", null),
            "record_stop" => ("StopRecord", null),
            "record_pause" => ("ToggleRecordPause", null),
            "stream_toggle" => ("ToggleStream", null),
            "stream_start" => ("StartStream", null),
            "stream_stop" => ("StopStream", null),
            "replay_toggle" => ("ToggleReplayBuffer", null),
            "replay_save" => ("SaveReplayBuffer", null),
            "virtualcam_toggle" => ("ToggleVirtualCam", null),
            "mute_toggle" => ("ToggleInputMute", Input()),
            "mute" => ("SetInputMute", Input(true)),
            "unmute" => ("SetInputMute", Input(false)),
            _ => ("", null)
        };
        if (req.type.Length > 0) _ = Request(req.type, req.data);
    }

    public async Task<List<string>> Names(string kind)
    {
        if (kind == "scenes")
        {
            var r = await Request("GetSceneList");
            var list = (r?["scenes"] as JsonArray)?.Select(s => (string?)s?["sceneName"] ?? "").Where(s => s.Length > 0).ToList() ?? new();
            list.Reverse();          // OBS lists bottom-to-top
            return list;
        }
        var inputs = await Request("GetInputList");
        return (inputs?["inputs"] as JsonArray)?.Select(s => (string?)s?["inputName"] ?? "").Where(s => s.Length > 0).ToList() ?? new();
    }

    public void Dispose() { _cts?.Cancel(); }
}

// ---------------------------------------------------------------------------
// Installed apps, exactly as the Start menu lists them (desktop + Microsoft Store).
// Fetched by a short-lived helper process only when the editor's app picker opens,
// so it costs PadDeck no memory.
// ---------------------------------------------------------------------------
internal static class StartApps
{
    static List<(string name, string id)>? _cache;
    static DateTime _cachedAt;
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<List<(string name, string id)>> List()
    {
        await Gate.WaitAsync();
        try
        {
            if (_cache != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(2)) return _cache;
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
                "ConvertTo-Json -Compress -InputObject @(Get-StartApps | Select-Object Name,AppID)\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var readTask = p.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(readTask, Task.Delay(15000)) != readTask) { try { p.Kill(); } catch { } return _cache ?? new(); }
            var json = await readTask;
            var list = new List<(string, string)>();
            if (JsonNode.Parse(json) is JsonArray arr)
            {
                foreach (var n in arr)
                {
                    var name = (string?)n?["Name"];
                    var id = (string?)n?["AppID"];
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;      // web links pinned to Start
                    list.Add((name.Trim(), id.Trim()));
                }
            }
            list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
            _cache = list.DistinctBy(x => x.Item2).ToList();
            _cachedAt = DateTime.UtcNow;
            return _cache;
        }
        catch (Exception ex)
        {
            Log.Write("start apps: " + ex.Message);
            return _cache ?? new();
        }
        finally { Gate.Release(); }
    }
}

// ---------------------------------------------------------------------------
// Soundboard (MCI handles mp3/wav/wma; several sounds can overlap).
// ---------------------------------------------------------------------------
internal static unsafe class Sound
{
    static readonly Worker Thread = new("sound");
    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase);

    static string Mci(string cmd)
    {
        char* ret = stackalloc char[128];
        ret[0] = '\0';
        WinMM.mciSendString(cmd, ret, 128, 0);
        return new string(ret);
    }

    /// <summary>op: play (restart) | toggle (stop if playing) | stopall. volume 0..100.</summary>
    public static void Run(string? path, string? op, double? volume) => Thread.Post(() =>
    {
        if (op == "stopall") { foreach (var a in Aliases.Values) Mci($"stop {a}"); return; }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!Aliases.TryGetValue(path, out var alias))
        {
            alias = "pd" + Aliases.Count;
            Mci($"open \"{path}\" type mpegvideo alias {alias}");
            Aliases[path] = alias;
        }
        if (op == "toggle" && Mci($"status {alias} mode") == "playing") { Mci($"stop {alias}"); return; }
        int v = (int)Math.Clamp((volume ?? 100) * 10, 0, 1000);
        Mci($"setaudio {alias} volume to {v}");
        Mci($"play {alias} from 0");
    });
}

// ---------------------------------------------------------------------------
// Memory: give pages back to Windows after bursts of work (editor open, light show).
// ---------------------------------------------------------------------------
internal static partial class Mem
{
    [LibraryImport("kernel32.dll")] private static partial int SetProcessWorkingSetSize(nint process, nint min, nint max);

    static int _pending;

    /// <summary>Collect, decommit the GC heap, and trim the working set, a little after things go quiet.</summary>
    public static void TrimSoon(int delayMs = 2000)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Task.Delay(delayMs).ContinueWith(_ =>
        {
            Volatile.Write(ref _pending, 0);
            try
            {
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                SetProcessWorkingSetSize(-1, -1, -1);     // pages go to the standby list: free for other apps
            }
            catch (Exception ex) { Log.Write("trim: " + ex.Message); }
        });
    }
}
