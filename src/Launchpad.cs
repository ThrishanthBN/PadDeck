using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PadDeck;

/// <summary>
/// Talks to a Launchpad Mini MK3 (or Launchpad X) in Programmer mode.
/// Input arrives on a WinMM callback and is handed to a dedicated high-priority thread,
/// so presses are dispatched within microseconds. LED updates go out as one RGB SysEx
/// containing only the LEDs that changed.
/// </summary>
internal sealed unsafe class Launchpad : IDisposable
{
    public static readonly byte[] AllIds = BuildIds();

    public event Action<int, bool>? Button;     // id, pressed
    public event Action? ConnectionChanged;

    public bool Connected { get; private set; }
    public string DeviceName { get; private set; } = "";
    public string Status { get; private set; } = "Looking for Launchpad…";
    public List<string> PortNames { get; private set; } = new();

    readonly object _io = new();
    readonly List<nint> _ins = new();
    readonly List<nint> _outs = new();
    byte _deviceId = 0x0D;                       // 0x0D Mini MK3, 0x0C Launchpad X
    readonly Thread _inputThread, _scanThread;
    volatile bool _running = true;

    // Callback -> input thread hand-off
    static readonly ConcurrentQueue<long> Queue = new();
    static readonly AutoResetEvent Signal = new(false);
    static volatile int _lockedPort = -1;

    // SysEx buffer (pinned, reused)
    readonly byte* _buf;
    readonly WinMM.MIDIHDR* _hdr;
    readonly ManualResetEvent _outDone = new(false);
    const int BufSize = 1024;

    // LED state
    readonly uint[] _sent = new uint[100];       // last colour sent per id (0xRRGGBB in 7-bit), uint.MaxValue = unknown

    public Launchpad()
    {
        _buf = (byte*)NativeMemory.AllocZeroed(BufSize);
        _hdr = (WinMM.MIDIHDR*)NativeMemory.AllocZeroed((nuint)sizeof(WinMM.MIDIHDR));
        Array.Fill(_sent, uint.MaxValue);
        _inputThread = new Thread(InputLoop) { IsBackground = true, Name = "pad-input", Priority = ThreadPriority.Highest };
        _scanThread = new Thread(ScanLoop) { IsBackground = true, Name = "pad-scan", Priority = ThreadPriority.BelowNormal };
        _inputThread.Start();
        _scanThread.Start();
    }

    static byte[] BuildIds()
    {
        var ids = new List<byte>();
        for (int row = 1; row <= 9; row++)
            for (int col = 1; col <= 9; col++)
                ids.Add((byte)(row * 10 + col));
        return ids.ToArray();
    }

    // ---------------- connection ----------------

    static int Score(string name, bool input)
    {
        var n = name.ToLowerInvariant();
        bool mini = n.Contains("lpminimk3") || n.Contains("launchpad mini");
        bool x = n.Contains("lpx") || n.Contains("launchpad x");
        if (!mini && !x) return 0;
        // The second ("MIDI") interface is the one Programmer mode uses.
        if (n.Contains(input ? "midiin2" : "midiout2")) return 3;
        if (n.Contains("daw")) return 1;
        return 2;
    }

    static List<uint> Pick(List<string> names, bool input)
    {
        var scored = names.Select((n, i) => (i: (uint)i, s: Score(n, input))).Where(t => t.s > 0).ToList();
        if (scored.Count == 0) return new();
        int best = scored.Max(t => t.s);
        // Prefer the MIDI interface; if naming is ambiguous, use every non-DAW port.
        return scored.Where(t => best == 3 ? t.s == 3 : t.s >= Math.Min(best, 2)).Select(t => t.i).ToList();
    }

    void ScanLoop()
    {
        while (_running)
        {
            try
            {
                if (!Connected) TryConnect();
                else if (!StillPresent()) { Disconnect("Launchpad unplugged. Waiting for it…"); }
            }
            catch (Exception ex) { Log.Write("scan: " + ex); }
            for (int i = 0; i < 15 && _running; i++) Thread.Sleep(100);
        }
    }

    public void Rescan()
    {
        if (Connected) Disconnect("Reconnecting…");
        TryConnect();
    }

    bool StillPresent()
    {
        var ins = WinMM.InputNames();
        return ins.Any(n => Score(n, true) > 0);
    }

    void TryConnect()
    {
        var inNames = WinMM.InputNames();
        var outNames = WinMM.OutputNames();
        var inIdx = Pick(inNames, true);
        var outIdx = Pick(outNames, false);
        if (inIdx.Count == 0 || outIdx.Count == 0)
        {
            SetStatus(false, "No Launchpad found. Plug it in. PadDeck connects automatically.");
            return;
        }

        lock (_io)
        {
            CloseAll();
            var failed = new List<string>();
            var cb = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, void>)&MidiInProc;
            for (int k = 0; k < inIdx.Count; k++)
            {
                uint id = inIdx[k];
                if (WinMM.midiInOpen(out var h, id, cb, (nint)k, WinMM.CALLBACK_FUNCTION) == 0)
                { _ins.Add(h); WinMM.midiInStart(h); }
                else failed.Add(inNames[(int)id]);
            }
            foreach (var id in outIdx)
            {
                // CALLBACK_EVENT: Windows signals _outDone when a SysEx finishes, so we can sleep instead of spin.
                if (WinMM.midiOutOpen(out var h, id, _outDone.SafeWaitHandle.DangerousGetHandle(), 0, 0x50000) == 0) _outs.Add(h);
                else failed.Add(outNames[(int)id]);
            }
            if (_ins.Count == 0 || _outs.Count == 0)
            {
                CloseAll();
                SetStatus(false, "Launchpad is busy in another app (for example MIDI Mixer, Ableton or Novation Components). Close it and PadDeck will connect.");
                return;
            }

            var name = inNames[(int)inIdx[0]];
            _deviceId = name.ToLowerInvariant().Contains("lpx") || name.ToLowerInvariant().Contains("launchpad x") ? (byte)0x0C : (byte)0x0D;
            DeviceName = _deviceId == 0x0C ? "Launchpad X" : "Launchpad Mini MK3";
            PortNames = inIdx.Select(i => "in: " + inNames[(int)i]).Concat(outIdx.Select(i => "out: " + outNames[(int)i])).ToList();
            _lockedPort = _ins.Count == 1 ? 0 : -1;

            SysEx(0x09, 0x01);       // wake
            SysEx(0x0E, 0x01);       // programmer mode
            Array.Fill(_sent, uint.MaxValue);
        }
        SetStatus(true, $"{DeviceName} connected");
        Log.Write("Connected: " + string.Join(", ", PortNames));
    }

    void Disconnect(string status)
    {
        lock (_io) CloseAll();
        SetStatus(false, status);
    }

    void CloseAll()
    {
        foreach (var h in _ins) { WinMM.midiInStop(h); WinMM.midiInReset(h); WinMM.midiInClose(h); }
        foreach (var h in _outs) { WinMM.midiOutReset(h); WinMM.midiOutClose(h); }
        _ins.Clear();
        _outs.Clear();
    }

    void SetStatus(bool connected, string status)
    {
        bool changed = connected != Connected || status != Status;
        Connected = connected;
        Status = status;
        if (changed) ConnectionChanged?.Invoke();
    }

    // ---------------- input ----------------

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static void MidiInProc(nint h, uint msg, nint inst, nint p1, nint p2)
    {
        if (msg != WinMM.MIM_DATA) return;
        // Only queue + signal here; WinMM forbids most work inside the callback.
        Queue.Enqueue(((long)inst << 32) | (uint)p1);
        Signal.Set();
    }

    void InputLoop()
    {
        while (_running)
        {
            Signal.WaitOne();
            while (Queue.TryDequeue(out long item))
            {
                int port = (int)(item >> 32);
                uint m = (uint)item;
                int status = (int)(m & 0xF0), d1 = (int)((m >> 8) & 0x7F), d2 = (int)((m >> 16) & 0x7F);
                bool down;
                if (status == 0x90) down = d2 > 0;
                else if (status == 0x80) down = false;
                else if (status == 0xB0) down = d2 > 0;
                else continue;
                if (d1 < 11 || d1 > 99 || d1 % 10 == 0) continue;
                // If several ports are open, stick to whichever one reports presses first.
                if (_lockedPort == -1) _lockedPort = port;
                else if (_lockedPort != port) continue;
                try { Button?.Invoke(d1, down); }
                catch (Exception ex) { Log.Write("button handler: " + ex); }
            }
        }
    }

    // ---------------- output ----------------

    void SysEx(params byte[] body)
    {
        int n = 0;
        _buf[n++] = 0xF0; _buf[n++] = 0x00; _buf[n++] = 0x20; _buf[n++] = 0x29; _buf[n++] = 0x02; _buf[n++] = _deviceId;
        foreach (var b in body) _buf[n++] = b;
        _buf[n++] = 0xF7;
        SendBuffer(n);
    }

    void SendBuffer(int length)
    {
        foreach (var h in _outs)
        {
            *_hdr = default;
            _hdr->lpData = _buf;
            _hdr->dwBufferLength = (uint)length;
            _hdr->dwBytesRecorded = (uint)length;
            uint cb = (uint)sizeof(WinMM.MIDIHDR);
            if (WinMM.midiOutPrepareHeader(h, _hdr, cb) != 0) continue;
            _outDone.Reset();
            if (WinMM.midiOutLongMsg(h, _hdr, cb) == 0)
            {
                // Sleep until the driver says it's done (no busy waiting).
                for (int i = 0; i < 20 && (_hdr->dwFlags & WinMM.MHDR_DONE) == 0; i++) _outDone.WaitOne(10);
            }
            for (int i = 0; i < 50 && WinMM.midiOutUnprepareHeader(h, _hdr, cb) == WinMM.MIDIERR_STILLPLAYING; i++) Thread.Sleep(1);
        }
    }

    public void SetBrightness(int percent)
    {
        lock (_io) { if (Connected) SysEx(0x08, (byte)Math.Clamp(percent * 127 / 100, 0, 127)); }
    }

    /// <summary>Push a full frame (index = button id, value = 0xRRGGBB 8-bit). Only changed LEDs are sent.</summary>
    public void Show(uint[] frame)
    {
        lock (_io) ShowLocked(frame);
    }

    void ShowLocked(uint[] frame)
    {
        {
            if (_outs.Count == 0) return;
            int n = 0;
            _buf[n++] = 0xF0; _buf[n++] = 0x00; _buf[n++] = 0x20; _buf[n++] = 0x29; _buf[n++] = 0x02; _buf[n++] = _deviceId;
            _buf[n++] = 0x03;
            int count = 0;
            foreach (var id in AllIds)
            {
                uint c = frame[id];
                uint c7 = ((c >> 17) & 0x7F) << 16 | ((c >> 9) & 0x7F) << 8 | ((c >> 1) & 0x7F);
                if (_sent[id] == c7) continue;
                _sent[id] = c7;
                _buf[n++] = 0x03;          // RGB
                _buf[n++] = id;
                _buf[n++] = (byte)(c7 >> 16);
                _buf[n++] = (byte)((c7 >> 8) & 0x7F);
                _buf[n++] = (byte)(c7 & 0x7F);
                count++;
            }
            if (count == 0) return;
            _buf[n++] = 0xF7;
            SendBuffer(n);
        }
    }

    public void Dispose()
    {
        _running = false;
        Signal.Set();
        // Clear LEDs and hand the device back in its normal (Live) mode.
        lock (_io)
        {
            Array.Fill(_sent, uint.MaxValue);
            ShowLocked(new uint[100]);
            if (_outs.Count > 0) SysEx(0x0E, 0x00);
            CloseAll();
        }
    }
}
