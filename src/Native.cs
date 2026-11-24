using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PadDeck;

// ---------------------------------------------------------------------------
// WinMM MIDI. Callback-driven input, so a pad press reaches us in microseconds.
// ---------------------------------------------------------------------------
internal static unsafe partial class WinMM
{
    public const uint CALLBACK_FUNCTION = 0x30000;
    public const uint MIM_DATA = 0x3C3;
    public const uint MHDR_DONE = 0x1;
    public const uint MIDIERR_STILLPLAYING = 65;

    [StructLayout(LayoutKind.Sequential)]
    public struct MIDIINCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        public fixed char szPname[32];
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIDIOUTCAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        public fixed char szPname[32];
        public ushort wTechnology, wVoices, wNotes, wChannelMask;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIDIHDR
    {
        public byte* lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public nint dwUser;
        public uint dwFlags;
        public nint lpNext;
        public nint reserved;
        public uint dwOffset;
        public fixed long dwReserved[8];
    }

    [LibraryImport("winmm.dll")] public static partial uint midiInGetNumDevs();
    [LibraryImport("winmm.dll", EntryPoint = "midiInGetDevCapsW")] public static partial uint midiInGetDevCaps(nuint id, MIDIINCAPS* caps, uint cb);
    [LibraryImport("winmm.dll")] public static partial uint midiInOpen(out nint h, uint id, nint cb, nint inst, uint flags);
    [LibraryImport("winmm.dll")] public static partial uint midiInStart(nint h);
    [LibraryImport("winmm.dll")] public static partial uint midiInStop(nint h);
    [LibraryImport("winmm.dll")] public static partial uint midiInReset(nint h);
    [LibraryImport("winmm.dll")] public static partial uint midiInClose(nint h);

    [LibraryImport("winmm.dll")] public static partial uint midiOutGetNumDevs();
    [LibraryImport("winmm.dll", EntryPoint = "midiOutGetDevCapsW")] public static partial uint midiOutGetDevCaps(nuint id, MIDIOUTCAPS* caps, uint cb);
    [LibraryImport("winmm.dll")] public static partial uint midiOutOpen(out nint h, uint id, nint cb, nint inst, uint flags);
    [LibraryImport("winmm.dll")] public static partial uint midiOutClose(nint h);
    [LibraryImport("winmm.dll")] public static partial uint midiOutReset(nint h);
    [LibraryImport("winmm.dll")] public static partial uint midiOutShortMsg(nint h, uint msg);
    [LibraryImport("winmm.dll")] public static partial uint midiOutPrepareHeader(nint h, MIDIHDR* hdr, uint cb);
    [LibraryImport("winmm.dll")] public static partial uint midiOutUnprepareHeader(nint h, MIDIHDR* hdr, uint cb);
    [LibraryImport("winmm.dll")] public static partial uint midiOutLongMsg(nint h, MIDIHDR* hdr, uint cb);

    [LibraryImport("winmm.dll", EntryPoint = "mciSendStringW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint mciSendString(string command, char* ret, uint retLen, nint callback);

    public static List<string> InputNames()
    {
        var list = new List<string>();
        uint n = midiInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            MIDIINCAPS c;
            list.Add(midiInGetDevCaps(i, &c, (uint)sizeof(MIDIINCAPS)) == 0 ? new string(c.szPname) : "");
        }
        return list;
    }

    public static List<string> OutputNames()
    {
        var list = new List<string>();
        uint n = midiOutGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            MIDIOUTCAPS c;
            list.Add(midiOutGetDevCaps(i, &c, (uint)sizeof(MIDIOUTCAPS)) == 0 ? new string(c.szPname) : "");
        }
        return list;
    }
}

// ---------------------------------------------------------------------------
// Keyboard injection.
// ---------------------------------------------------------------------------
internal static unsafe partial class User32
{
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    [LibraryImport("user32.dll")] public static partial uint SendInput(uint n, INPUT* inputs, int cb);
    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")] public static partial uint MapVirtualKey(uint code, uint type);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    // Window + tray plumbing
    public const uint WM_DESTROY = 0x0002, WM_COMMAND = 0x0111, WM_USER = 0x0400, WM_APP = 0x8000;
    public const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203, WM_CONTEXTMENU = 0x007B;
    public const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8, MF_GRAYED = 0x1;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; public uint lPrivate; }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")] public static partial ushort RegisterClassEx(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] public static partial nint DefWindowProc(nint hwnd, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] public static partial int GetMessage(MSG* msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll")] public static partial int TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] public static partial nint DispatchMessage(MSG* msg);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessage(nint hwnd, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")] public static partial nint LoadIcon(nint inst, nint name);
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)] public static partial int MessageBox(nint hwnd, string text, string caption, uint type);
}

internal static unsafe partial class Shell32
{
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIcon(uint msg, NOTIFYICONDATAW* data);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ExtractIconEx(string file, int index, nint* large, nint* small, uint n);

    public static void CopyTo(char* dst, int cap, string s)
    {
        int n = Math.Min(s.Length, cap - 1);
        for (int i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = '\0';
    }
}

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] public static partial nint GetModuleHandle(nint name);
}

// ---------------------------------------------------------------------------
// Windows Core Audio (per-app + endpoint volume) via source-generated COM.
// Trim-safe, no reflection.
// ---------------------------------------------------------------------------
internal static partial class Ole32
{
    public const uint CLSCTX_ALL = 23;
    public const uint COINIT_MULTITHREADED = 0;
    [LibraryImport("ole32.dll")] public static partial int CoInitializeEx(nint reserved, uint coinit);
    [LibraryImport("ole32.dll")] public static partial int CoCreateInstance(in Guid clsid, nint outer, uint ctx, in Guid iid, out nint ppv);
    [LibraryImport("ole32.dll")] public static partial void CoTaskMemFree(nint p);
    [LibraryImport("ole32.dll")] public static partial int PropVariantClear(ref PROPVARIANT pv);
}

internal static unsafe partial class Gdi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO { public int fIcon, xHotspot, yHotspot; public nint hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public nint bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetIconInfo(nint icon, out ICONINFO info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(nint icon);
    [LibraryImport("user32.dll")] public static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")] public static partial int GetObject(nint obj, int size, void* buf);
    [LibraryImport("gdi32.dll")] public static partial int GetDIBits(nint dc, nint bmp, uint start, uint lines, void* bits, BITMAPINFOHEADER* info, uint usage);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint obj);

    [LibraryImport("kernel32.dll")] public static partial nint OpenProcess(uint access, int inherit, uint pid);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* buf, ref int size);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseHandle(nint h);

    public static string? ProcessPath(uint pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, 0, pid);
        if (h == 0) return null;
        try
        {
            char* buf = stackalloc char[1024];
            int size = 1024;
            return QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// The most characteristic colour in an exe's icon: vivid hues weighted by saturation and brightness,
    /// then pushed to full brightness so it reads well on an LED. Grey icons come back as soft white.
    /// </summary>
    public static uint IconColor(string exePath)
    {
        nint large = 0;
        if (Shell32.ExtractIconEx(exePath, 0, &large, null, 1) == 0 || large == 0) return 0xE2E8F0;
        try
        {
            if (!GetIconInfo(large, out var ii)) return 0xE2E8F0;
            try
            {
                if (ii.hbmColor == 0) return 0xE2E8F0;
                BITMAP bm;
                GetObject(ii.hbmColor, sizeof(BITMAP), &bm);
                int w = bm.bmWidth, hgt = bm.bmHeight;
                if (w <= 0 || hgt <= 0 || w > 256 || hgt > 256) return 0xE2E8F0;
                var px = new uint[w * hgt];
                var bih = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -hgt, biPlanes = 1, biBitCount = 32 };
                var dc = GetDC(0);
                fixed (uint* p = px) GetDIBits(dc, ii.hbmColor, 0, (uint)hgt, p, &bih, 0);
                ReleaseDC(0, dc);
                return Dominant(px);
            }
            finally
            {
                if (ii.hbmColor != 0) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != 0) DeleteObject(ii.hbmMask);
            }
        }
        finally { DestroyIcon(large); }
    }

    /// <summary>
    /// (ShellIconColor) Icon colour for anything the shell can show: a Start-menu app ("shell:AppsFolder\..."), Microsoft Store apps
    /// included, or an .exe / shortcut / file path. Call on an STA thread. Null if there's no icon.
    /// </summary>
    public static uint? AppIconColor(string parsingName)
    {
        // Some Store apps (Apple Music) have a white glyph on a coloured tile: if the bare icon is colourless, try it with its tile.
        var c = ShellIconColor(parsingName, 0x4 | 0x1);
        if (c is null or 0xE2E8F0 && parsingName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            c = ShellIconColor(parsingName, 0x80 | 0x4 | 0x1) ?? c;
        // A few apps ship a plain white icon on Windows: use the colour people know them by.
        if (c is null or 0xE2E8F0)
            foreach (var (key, brand) in BrandColors)
                if (parsingName.Contains(key, StringComparison.OrdinalIgnoreCase)) return brand;
        return c;
    }

    static readonly (string key, uint color)[] BrandColors =
    {
        ("AppleMusic", 0xFA2D48), ("AppleTV", 0xE2E8F0), ("iCloud", 0x3693F3), ("Notion", 0xE2E8F0),
    };

    public static uint? ShellIconColor(string parsingName, int flags)
    {
        var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");          // IShellItemImageFactory
        if (SHCreateItemFromParsingName(parsingName, 0, &iid, out var factory) != 0 || factory == 0) return null;
        nint bmp = 0;
        try
        {
            // GetImage(SIZE, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, HBITMAP*)
            var size = 64L | (64L << 32);
            if (((delegate* unmanaged[Stdcall]<nint, long, int, nint*, int>)(*(nint**)factory)[3])(factory, size, flags, &bmp) != 0 || bmp == 0) return null;
            BITMAP bm;
            GetObject(bmp, sizeof(BITMAP), &bm);
            int w = bm.bmWidth, hgt = Math.Abs(bm.bmHeight);
            if (w <= 0 || hgt <= 0 || w > 512 || hgt > 512) return null;
            var px = new uint[w * hgt];
            var bih = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -hgt, biPlanes = 1, biBitCount = 32 };
            var dc = GetDC(0);
            fixed (uint* p = px) GetDIBits(dc, bmp, 0, (uint)hgt, p, &bih, 0);
            ReleaseDC(0, dc);
            return Dominant(px);
        }
        finally
        {
            if (bmp != 0) DeleteObject(bmp);
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)factory)[2])(factory);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindCtx, Guid* riid, out nint item);

    static uint Dominant(uint[] px)
    {
        var weight = new double[24];
        var sumR = new double[24]; var sumG = new double[24]; var sumB = new double[24];
        bool anyAlpha = px.Any(p => (p >> 24) != 0);
        foreach (var p in px)
        {
            if (anyAlpha && (p >> 24) < 128) continue;
            double r = ((p >> 16) & 0xFF) / 255.0, g = ((p >> 8) & 0xFF) / 255.0, b = (p & 0xFF) / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double s = max <= 0 ? 0 : (max - min) / max;
            if (s < 0.25 || max < 0.2) continue;
            double hue = max == min ? 0 : max == r ? ((g - b) / (max - min) + 6) % 6 : max == g ? (b - r) / (max - min) + 2 : (r - g) / (max - min) + 4;
            int bin = (int)(hue / 6 * 24) % 24;
            double wgt = s * max;
            weight[bin] += wgt; sumR[bin] += r * wgt; sumG[bin] += g * wgt; sumB[bin] += b * wgt;
        }
        int best = Array.IndexOf(weight, weight.Max());
        if (weight[best] < 3) return 0xE2E8F0;
        double R = sumR[best] / weight[best], G = sumG[best] / weight[best], B = sumB[best] / weight[best];
        double m = Math.Max(R, Math.Max(G, B));
        R /= m; G /= m; B /= m;                                   // full brightness, same hue
        return (uint)(R * 255) << 16 | (uint)(G * 255) << 8 | (uint)(B * 255);
    }
}

[GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[GeneratedComInterface, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, uint clsCtx, nint activationParams, out nint iface);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore props);
    [PreserveSig] int GetId(out nint id);
    [PreserveSig] int GetState(out uint state);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY { public Guid fmtid; public uint pid; }

[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT { public ushort vt, r1, r2, r3; public nint p, p2; }

[GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
    [PreserveSig] int GetValue(in PROPERTYKEY key, out PROPVARIANT value);
}

/// <summary>Undocumented but stable since Windows 7; it's what every "switch audio device" tool uses.</summary>
[GeneratedComInterface, Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
internal partial interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int ResetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint(nint deviceId, int role);
}

[GeneratedComInterface, Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal partial interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(nint notify);
    [PreserveSig] int UnregisterControlChangeNotify(nint notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float db, in Guid ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, in Guid ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, in Guid ctx);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, in Guid ctx);
    [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
    [PreserveSig] int SetMute(int mute, in Guid ctx);
    [PreserveSig] int GetMute(out int mute);
}

[GeneratedComInterface, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(nint sessionGuid, uint flags, out nint control);
    [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, uint flags, out nint volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[GeneratedComInterface, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}

[GeneratedComInterface, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal partial interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out nint name);
    [PreserveSig] int SetDisplayName(nint name, in Guid ctx);
    [PreserveSig] int GetIconPath(out nint path);
    [PreserveSig] int SetIconPath(nint path, in Guid ctx);
    [PreserveSig] int GetGroupingParam(out Guid group);
    [PreserveSig] int SetGroupingParam(in Guid group, in Guid ctx);
    [PreserveSig] int RegisterAudioSessionNotification(nint notify);
    [PreserveSig] int UnregisterAudioSessionNotification(nint notify);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier(out nint id);
    [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference(int optOut);
}

[GeneratedComInterface, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
internal partial interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, in Guid ctx);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute(int mute, in Guid ctx);
    [PreserveSig] int GetMute(out int mute);
}

internal static class Com
{
    public static readonly StrategyBasedComWrappers Wrappers = new();

    public static T Wrap<T>(nint p) where T : class
    {
        var o = (T)Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance);
        Marshal.Release(p);
        return o;
    }

    public static void Release(object? o)
    {
        if (o is ComObject c) c.FinalRelease();
    }

    public static string? TakeString(nint p)
    {
        if (p == 0) return null;
        var s = Marshal.PtrToStringUni(p);
        Ole32.CoTaskMemFree(p);
        return s;
    }
}
