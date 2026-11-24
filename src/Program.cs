using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PadDeck;

internal static unsafe partial class Program
{
    const int DefaultPort = 47811;
    static readonly string PortFile = Path.Combine(ConfigStore.Dir, "port.txt");

    static Engine? _engine;
    static WebServer? _web;
    static nint _hwnd, _icon;
    static uint _taskbarCreated;
    const uint WM_TRAY = User32.WM_APP + 1, WM_REFRESH = User32.WM_APP + 2;
    const int CMD_OPEN = 1, CMD_PAUSE = 2, CMD_RECONNECT = 3, CMD_AUTOSTART = 4, CMD_QUIT = 5, CMD_CONFIG = 6;

    [STAThread]
    static int Main(string[] args)
    {
        bool background = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(true, @"Local\PadDeck.SingleInstance", out bool first);
        if (!first)
        {
            // Already running: just open the editor.
            try { OpenEditor($"http://127.0.0.1:{File.ReadAllText(PortFile).Trim()}/"); } catch { }
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("FATAL: " + e.ExceptionObject);
        bool firstRun = !File.Exists(ConfigStore.FilePath);

        try
        {
            var cfg = ConfigStore.Load();
            var pad = new Launchpad();
            var audio = new Audio();
            var obs = new Obs();
            _engine = new Engine(cfg, pad, audio, obs);
            _web = new WebServer(_engine)
            {
                GetAutostart = GetAutostart,
                SetAutostart = SetAutostart,
                PickFile = PickFile,
            };
            _web.Start(DefaultPort);
            _engine.EditorOpen = () => _web.EditorOpen;
            File.WriteAllText(PortFile, _web.Port.ToString());
            pad.ConnectionChanged += () => { if (_hwnd != 0) User32.PostMessage(_hwnd, WM_REFRESH, 0, 0); };

            bool noEditor = args.Any(a => a.Equals("--no-editor", StringComparison.OrdinalIgnoreCase));
            if (!noEditor && (firstRun || !background)) OpenEditor(_web.Url);

            // Settle: free startup garbage so the steady-state footprint is small.
            Mem.TrimSoon(3000);

            RunTray();

            _web.Dispose();
            _engine.Dispose();
            obs.Dispose();
            pad.Dispose();
            audio.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("startup: " + ex);
            User32.MessageBox(0, "PadDeck could not start:\n\n" + ex.Message, "PadDeck", 0x10);
            return 1;
        }
    }

    // ---------------------------------------------------------------- editor

    static void OpenEditor(string url)
    {
        // Edge "app" window feels like a real app and closes to nothing; fall back to the default browser.
        try { Process.Start(new ProcessStartInfo("msedge", $"--app={url} --window-size=1240,860") { UseShellExecute = true })?.Dispose(); return; }
        catch { }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    // ---------------------------------------------------------------- autostart

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool GetAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("PadDeck") is string;
    }

    static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("PadDeck", $"\"{Environment.ProcessPath}\" --background");
        else k.DeleteValue("PadDeck", false);
    }

    // ---------------------------------------------------------------- file picker

    [StructLayout(LayoutKind.Sequential)]
    struct OPENFILENAMEW
    {
        public int lStructSize;
        public nint hwndOwner, hInstance;
        public char* lpstrFilter, lpstrCustomFilter;
        public int nMaxCustFilter, nFilterIndex;
        public char* lpstrFile;
        public int nMaxFile;
        public char* lpstrFileTitle;
        public int nMaxFileTitle;
        public char* lpstrInitialDir, lpstrTitle;
        public int Flags;
        public short nFileOffset, nFileExtension;
        public char* lpstrDefExt;
        public nint lCustData, lpfnHook;
        public char* lpTemplateName;
        public nint pvReserved;
        public int dwReserved, FlagsEx;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")]
    private static partial int GetOpenFileName(OPENFILENAMEW* ofn);

    static string? PickFile(string kind)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            string filter = kind switch
            {
                "sound" => "Sounds (*.mp3;*.wav;*.wma;*.m4a)\0*.mp3;*.wav;*.wma;*.m4a\0All files\0*.*\0\0",
                "app" => "Programs and shortcuts\0*.exe;*.lnk;*.bat;*.cmd;*.url\0All files\0*.*\0\0",
                _ => "All files\0*.*\0\0",
            };
            // A topmost owner so the dialog shows above the editor window.
            var owner = User32.CreateWindowEx(0x8 | 0x80, "STATIC", "", 0x80000000, 0, 0, 0, 0, 0, 0, 0, 0);
            char* file = stackalloc char[1024];
            file[0] = '\0';
            fixed (char* f = filter)
            fixed (char* title = "Choose a file for PadDeck")
            {
                var ofn = new OPENFILENAMEW
                {
                    lStructSize = sizeof(OPENFILENAMEW),
                    hwndOwner = owner,
                    lpstrFilter = f,
                    lpstrFile = file,
                    nMaxFile = 1024,
                    lpstrTitle = title,
                    Flags = 0x1000 | 0x800 | 0x8 | 0x80000 | 0x100000, // FILEMUSTEXIST|PATHMUSTEXIST|NOCHANGEDIR|EXPLORER|NODEREFERENCELINKS
                };
                User32.SetForegroundWindow(owner);
                if (GetOpenFileName(&ofn) != 0) result = new string(file);
            }
            DestroyWindow(owner);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);

    // ---------------------------------------------------------------- tray

    static void RunTray()
    {
        var hinst = Kernel32.GetModuleHandle(0);
        fixed (char* cls = "PadDeckTray")
        {
            var wc = new User32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(User32.WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
                hInstance = hinst,
                lpszClassName = (nint)cls,
            };
            User32.RegisterClassEx(&wc);
        }
        _taskbarCreated = User32.RegisterWindowMessage("TaskbarCreated");
        _hwnd = User32.CreateWindowEx(0, "PadDeckTray", "PadDeck", 0, 0, 0, 0, 0, 0, 0, hinst, 0);

        nint small = 0;
        var exe = Environment.ProcessPath;
        if (exe != null) Shell32.ExtractIconEx(exe, 0, null, &small, 1);
        _icon = small != 0 ? small : User32.LoadIcon(0, 32512);
        AddIcon();

        User32.MSG msg;
        while (User32.GetMessage(&msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(&msg);
            User32.DispatchMessage(&msg);
        }
        RemoveIcon();
    }

    static Shell32.NOTIFYICONDATAW IconData()
    {
        var d = new Shell32.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Shell32.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
        };
        var tip = "PadDeck: " + (_engine?.Pad.Connected == true ? _engine.Pad.DeviceName + " connected" : "Launchpad not connected");
        if (_engine?.Paused == true) tip += " (paused)";
        Shell32.CopyTo(d.szTip, 128, tip);
        return d;
    }

    static void AddIcon()
    {
        var d = IconData();
        Shell32.Shell_NotifyIcon(Shell32.NIM_ADD, &d);
    }

    static void UpdateIcon()
    {
        var d = IconData();
        Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, &d);
    }

    static void RemoveIcon()
    {
        var d = IconData();
        Shell32.Shell_NotifyIcon(Shell32.NIM_DELETE, &d);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static nint WndProc(nint hwnd, uint msg, nuint w, nint l)
    {
        try
        {
            if (msg == WM_TRAY)
            {
                uint ev = (uint)(l & 0xFFFF);
                if (ev == User32.WM_LBUTTONUP || ev == User32.WM_LBUTTONDBLCLK) { if (_web != null) OpenEditor(_web.Url); }
                else if (ev == User32.WM_RBUTTONUP || ev == User32.WM_CONTEXTMENU) ShowMenu(hwnd);
                return 0;
            }
            if (msg == WM_REFRESH) { UpdateIcon(); return 0; }
            if (msg == _taskbarCreated && _taskbarCreated != 0) { AddIcon(); return 0; }
            if (msg == User32.WM_DESTROY) { User32.PostQuitMessage(0); return 0; }
        }
        catch (Exception ex) { Log.Write("tray: " + ex.Message); }
        return User32.DefWindowProc(hwnd, msg, w, l);
    }

    static void ShowMenu(nint hwnd)
    {
        var e = _engine!;
        var menu = User32.CreatePopupMenu();
        User32.AppendMenu(menu, User32.MF_STRING, CMD_OPEN, "Open editor");
        User32.AppendMenu(menu, User32.MF_SEPARATOR, 0, null);
        var status = e.Pad.Connected ? e.Pad.DeviceName + " connected" : "Launchpad not connected";
        User32.AppendMenu(menu, User32.MF_STRING | User32.MF_GRAYED, 0, status);
        User32.AppendMenu(menu, User32.MF_STRING | (e.Paused ? User32.MF_CHECKED : 0), CMD_PAUSE, "Pause actions");
        User32.AppendMenu(menu, User32.MF_STRING, CMD_RECONNECT, "Reconnect Launchpad");
        User32.AppendMenu(menu, User32.MF_STRING | (GetAutostart() ? User32.MF_CHECKED : 0), CMD_AUTOSTART, "Start with Windows");
        User32.AppendMenu(menu, User32.MF_STRING, CMD_CONFIG, "Show config folder");
        User32.AppendMenu(menu, User32.MF_SEPARATOR, 0, null);
        User32.AppendMenu(menu, User32.MF_STRING, CMD_QUIT, "Quit PadDeck");

        User32.GetCursorPos(out var pt);
        User32.SetForegroundWindow(hwnd);
        int cmd = User32.TrackPopupMenu(menu, User32.TPM_RIGHTBUTTON | User32.TPM_RETURNCMD, pt.x, pt.y, 0, hwnd, 0);
        User32.DestroyMenu(menu);

        switch (cmd)
        {
            case CMD_OPEN: if (_web != null) OpenEditor(_web.Url); break;
            case CMD_PAUSE: e.SetPaused(!e.Paused); UpdateIcon(); break;
            case CMD_RECONNECT: Task.Run(e.Pad.Rescan); break;
            case CMD_AUTOSTART: SetAutostart(!GetAutostart()); break;
            case CMD_CONFIG: Process.Start(new ProcessStartInfo(ConfigStore.Dir) { UseShellExecute = true })?.Dispose(); break;
            case CMD_QUIT: User32.PostQuitMessage(0); break;
        }
    }
}
