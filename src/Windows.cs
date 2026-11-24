using System.Runtime.InteropServices;

namespace PadDeck;

/// <summary>
/// Finds the top-level windows that belong to an app (desktop or Microsoft Store) and
/// restores / brings to front / minimizes / closes them. Runs on its own STA thread.
/// </summary>
internal static unsafe partial class AppWindows
{
    public enum State { None, Front, Minimized, Behind }

    public sealed record Target(string? Aumid, string? ExePath, string? ExeName);

    public static readonly Worker Thread = new("windows", ThreadPriority.AboveNormal, () =>
    {
        Ole32.CoInitializeEx(0, 2 /* STA: shell property stores */);
        User32.MSG m;
        PeekMessage(&m, 0, 0, 0, 0);          // give the thread a message queue (needed by AttachThreadInput)
    });

    static readonly Dictionary<string, Target> TargetCache = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ target

    /// <summary>A Start-menu app, an .exe or a shortcut: something with windows (not a file, folder or website).</summary>
    public static bool IsAppTarget(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim().Trim('"');
        return p.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) ||
               p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Work out how to recognise an app's windows from what the button launches.</summary>
    public static Target? Resolve(string? path, string? match)
    {
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(match)) return null;
        string key = (path ?? "") + "|" + (match ?? "");
        if (TargetCache.TryGetValue(key, out var hit)) return hit;

        string? aumid = null, exe = null, exeName = null;
        var p = Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));
        const string apps = @"shell:AppsFolder\";
        if (p.StartsWith(apps, StringComparison.OrdinalIgnoreCase))
        {
            var id = p[apps.Length..];
            aumid = id;
            if (id.Equals("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase)) exeName = "explorer";
            if (!id.Contains('!'))
            {
                // Desktop app: ask the shell what its Start-menu shortcut points at.
                var (target, args) = LinkTarget(p);
                exe = target;
                if (exe == null && id.Contains(":\\")) exe = id;
                exe ??= KnownFolderPath(id);
                exeName ??= ProcessStartName(exe, args);
            }
        }
        else if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = p;
        else if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var (target, args) = LinkTarget(p);
            exe = target;
            exeName = ProcessStartName(exe, args);
        }

        if (exe != null && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = null;
        if (!string.IsNullOrWhiteSpace(match)) exeName = Path.GetFileNameWithoutExtension(match.Trim());
        exeName ??= exe != null ? Path.GetFileNameWithoutExtension(exe) : null;

        var t = aumid == null && exe == null && exeName == null ? null : new Target(aumid, exe, exeName);
        if (t != null) TargetCache[key] = t;
        return t;
    }

    /// <summary>Squirrel apps (Discord, Slack...) start through Update.exe --processStart Real.exe.</summary>
    static string? ProcessStartName(string? exe, string? args)
    {
        if (exe == null || args == null || !exe.EndsWith("update.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var i = args.IndexOf("--processStart", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = args[(i + 14)..].Trim().Trim('"');
        var end = rest.IndexOfAny(new[] { '"', ' ' });
        if (end > 0) rest = rest[..end];
        return Path.GetFileNameWithoutExtension(rest);
    }

    static (string? target, string? args) LinkTarget(string parsingName)
    {
        var iid = IID_PropertyStore;
        if (SHGetPropertyStoreFromParsingName(parsingName, 0, 0, iid, out var ps) != 0 || ps == 0) return (null, null);
        var store = Com.Wrap<IPropertyStore>(ps);
        try
        {
            return (ReadString(store, PKEY_Link_TargetParsingPath), ReadString(store, PKEY_Link_Arguments));
        }
        finally { Com.Release(store); }
    }

    static string? KnownFolderPath(string id)
    {
        // "{1AC14E77-...}\cmd.exe" style ids: a known folder plus a relative path.
        if (!id.StartsWith('{')) return null;
        int close = id.IndexOf('}');
        if (close < 0 || !Guid.TryParse(id[..(close + 1)], out var folder)) return null;
        if (SHGetKnownFolderPath(folder, 0, 0, out var p) != 0) return null;
        var root = Com.TakeString(p);
        return root == null ? null : Path.Combine(root, id[(close + 1)..].TrimStart('\\'));
    }

    // ------------------------------------------------------------------ windows

    public readonly record struct Win(nint Hwnd, bool Minimized);

    /// <summary>The app's windows in z-order (topmost first), and where they stand right now.</summary>
    public static State Find(Target t, out List<Win> wins)
    {
        wins = new List<Win>();
        var pidInfo = new Dictionary<uint, (string? exe, string? aumid)>();
        foreach (var h in TopLevel())
            if (Matches(h, t, pidInfo)) wins.Add(new Win(h, IsIconic(h) != 0));
        if (wins.Count == 0) return State.None;

        var fg = User32.GetForegroundWindow();
        var root = fg == 0 ? 0 : GetAncestor(fg, 3 /* GA_ROOTOWNER */);
        foreach (var w in wins) if (!w.Minimized && (w.Hwnd == fg || w.Hwnd == root)) return State.Front;
        foreach (var w in wins) if (!w.Minimized) return State.Behind;
        return State.Minimized;
    }

    static List<nint> TopLevel()
    {
        var list = new List<nint>();
        var h = GCHandle.Alloc(list);
        try { EnumWindows(&EnumCb, GCHandle.ToIntPtr(h)); }
        finally { h.Free(); }
        return list;
    }

    [UnmanagedCallersOnly]
    static int EnumCb(nint hwnd, nint l)
    {
        var list = (List<nint>)GCHandle.FromIntPtr(l).Target!;
        if (IsWindowVisible(hwnd) == 0) return 1;
        if (GetWindow(hwnd, 4 /* GW_OWNER */) != 0) return 1;
        long ex = GetWindowLongPtr(hwnd, -20 /* GWL_EXSTYLE */);
        if ((ex & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return 1;
        if (GetWindowTextLength(hwnd) == 0) return 1;
        char* cls = stackalloc char[32];
        int n = GetClassName(hwnd, cls, 32);
        var name = new ReadOnlySpan<char>(cls, n);
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return 1;   // desktop + taskbar
        int cloaked = 0;
        if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, &cloaked, 4) == 0 && cloaked != 0) return 1;
        list.Add(hwnd);
        return 1;
    }

    static bool Matches(nint h, Target t, Dictionary<uint, (string? exe, string? aumid)> cache)
    {
        if (t.Aumid != null && string.Equals(WindowAumid(h), t.Aumid, StringComparison.OrdinalIgnoreCase)) return true;

        User32.GetWindowThreadProcessId(h, out var pid);
        var (exe, aumid) = Info(pid, cache);
        if (exe != null && exe.EndsWith(@"\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Store app frame: the real app lives in a child window from another process.
            var child = HostedPid(h, pid);
            if (child != 0) (exe, aumid) = Info(child, cache);
        }
        if (t.Aumid != null && string.Equals(aumid, t.Aumid, StringComparison.OrdinalIgnoreCase)) return true;
        if (exe == null) return false;
        if (t.ExePath != null && string.Equals(exe, t.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
        return t.ExeName != null && string.Equals(Path.GetFileNameWithoutExtension(exe), t.ExeName, StringComparison.OrdinalIgnoreCase);
    }

    static (string? exe, string? aumid) Info(uint pid, Dictionary<uint, (string? exe, string? aumid)> cache)
    {
        if (cache.TryGetValue(pid, out var v)) return v;
        string? exe = Gdi.ProcessPath(pid), aumid = null;
        var hp = Gdi.OpenProcess(0x1000, 0, pid);
        if (hp != 0)
        {
            try
            {
                char* buf = stackalloc char[256];
                uint len = 256;
                if (GetApplicationUserModelId(hp, ref len, buf) == 0) aumid = new string(buf, 0, (int)Math.Max(0, len - 1));
            }
            finally { Gdi.CloseHandle(hp); }
        }
        return cache[pid] = (exe, aumid);
    }

    static uint HostedPid(nint frame, uint framePid)
    {
        var ctx = new uint[] { framePid, 0 };
        var h = GCHandle.Alloc(ctx);
        try { EnumChildWindows(frame, &ChildCb, GCHandle.ToIntPtr(h)); }
        finally { h.Free(); }
        return ctx[1];
    }

    [UnmanagedCallersOnly]
    static int ChildCb(nint hwnd, nint l)
    {
        var ctx = (uint[])GCHandle.FromIntPtr(l).Target!;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != ctx[0]) { ctx[1] = pid; return 0; }
        return 1;
    }

    static string? WindowAumid(nint h)
    {
        var iid = IID_PropertyStore;
        if (SHGetPropertyStoreForWindow(h, iid, out var ps) != 0 || ps == 0) return null;
        var store = Com.Wrap<IPropertyStore>(ps);
        try { return ReadString(store, PKEY_AppUserModel_ID); }
        finally { Com.Release(store); }
    }

    static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(key, out var pv) != 0) return null;
        try { return pv.vt == 31 /* VT_LPWSTR */ && pv.p != 0 ? Marshal.PtrToStringUni(pv.p) : null; }
        finally { Ole32.PropVariantClear(ref pv); }
    }

    // ------------------------------------------------------------------ actions

    public static void Activate(nint h)
    {
        if (IsIconic(h) != 0) ShowWindow(h, 9 /* SW_RESTORE */);
        var fg = User32.GetForegroundWindow();
        uint fgThread = fg == 0 ? 0 : User32.GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, 1) != 0;
        try
        {
            BringWindowToTop(h);
            User32.SetForegroundWindow(h);
        }
        finally { if (attached) AttachThreadInput(me, fgThread, 0); }
        if (User32.GetForegroundWindow() != h) SwitchToThisWindow(h, 1);
    }

    public static void Minimize(List<Win> wins)
    {
        foreach (var w in wins) if (!w.Minimized) ShowWindow(w.Hwnd, 6 /* SW_MINIMIZE */);
    }

    public static void Close(List<Win> wins)
    {
        foreach (var w in wins) User32.PostMessage(w.Hwnd, 0x0010 /* WM_CLOSE */, 0, 0);
    }

    /// <summary>
    /// Launch if closed, restore if minimized, bring to front if behind other windows,
    /// and (when allowed) minimize if it's already in front.
    /// </summary>
    public static State Smart(ActionDef a, bool minimizeIfFront, out List<Win> wins)
    {
        wins = new List<Win>();
        var t = Resolve(a.Path, a.Match);
        var st = t == null ? State.None : Find(t, out wins);
        switch (st)
        {
            case State.None: Engine.Launch(a.Path, a.Args); break;
            case State.Minimized: Activate(wins[0].Hwnd); break;
            case State.Behind: Activate(wins.First(w => !w.Minimized).Hwnd); break;
            case State.Front: if (minimizeIfFront) Minimize(wins); break;
        }
        return st;
    }

    /// <summary>For checking matches: every app window with its window app id, process app id and exe.</summary>
    public static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        var cache = new Dictionary<uint, (string? exe, string? aumid)>();
        foreach (var h in TopLevel())
        {
            User32.GetWindowThreadProcessId(h, out var pid);
            var (exe, aumid) = Info(pid, cache);
            if (exe != null && exe.EndsWith(@"\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var c = HostedPid(h, pid);
                if (c != 0) (exe, aumid) = Info(c, cache);
            }
            sb.Append(WindowAumid(h)).Append(" | ").Append(aumid).Append(" | ").Append(exe)
              .Append(IsIconic(h) != 0 ? " | minimized" : "").AppendLine();
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ native

    static readonly Guid IID_PropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    static readonly PROPERTYKEY PKEY_AppUserModel_ID = new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
    static readonly PROPERTYKEY PKEY_Link_TargetParsingPath = new() { fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), pid = 2 };
    static readonly PROPERTYKEY PKEY_Link_Arguments = new() { fmtid = new Guid("436F2667-14E2-4FEB-B30A-146C53B5B674"), pid = 100 };

    [LibraryImport("user32.dll")] private static partial int EnumWindows(delegate* unmanaged<nint, nint, int> cb, nint l);
    [LibraryImport("user32.dll")] private static partial int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> cb, nint l);
    [LibraryImport("user32.dll")] private static partial int IsWindowVisible(nint h);
    [LibraryImport("user32.dll")] private static partial int IsIconic(nint h);
    [LibraryImport("user32.dll")] private static partial nint GetWindow(nint h, uint cmd);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial long GetWindowLongPtr(nint h, int index);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static partial int GetWindowTextLength(nint h);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")] private static partial int GetClassName(nint h, char* buf, int max);
    [LibraryImport("user32.dll")] private static partial nint GetAncestor(nint h, uint flags);
    [LibraryImport("user32.dll")] private static partial int ShowWindow(nint h, int cmd);
    [LibraryImport("user32.dll")] private static partial int BringWindowToTop(nint h);
    [LibraryImport("user32.dll")] private static partial int AttachThreadInput(uint from, uint to, int attach);
    [LibraryImport("user32.dll")] private static partial void SwitchToThisWindow(nint h, int altTab);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] private static partial int PeekMessage(User32.MSG* m, nint h, uint min, uint max, uint remove);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll")] private static partial int GetApplicationUserModelId(nint process, ref uint len, char* buf);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(nint h, uint attr, void* value, uint size);
    [LibraryImport("shell32.dll")] private static partial int SHGetPropertyStoreForWindow(nint h, in Guid riid, out nint ppv);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHGetPropertyStoreFromParsingName(string path, nint bindCtx, int flags, in Guid riid, out nint ppv);
    [LibraryImport("shell32.dll")] private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
