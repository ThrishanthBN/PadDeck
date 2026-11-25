using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PadDeck;

/// <summary>
/// Tiny HTTP server for the editor, bound to 127.0.0.1 only. No http.sys / admin rights needed.
/// Every API call must carry an X-PadDeck header and a local Host, so web pages in your browser
/// can't drive PadDeck (that blocks cross-site and DNS-rebinding requests).
/// </summary>
internal sealed class WebServer : IDisposable
{
    readonly Engine _engine;
    TcpListener? _listener;
    public int Port { get; private set; }
    readonly List<NetworkStream> _sse = new();
    readonly Channel<string> _out = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    public Func<bool>? GetAutostart;
    public Action<bool>? SetAutostart;
    public Func<string, string?>? PickFile;

    public WebServer(Engine engine)
    {
        _engine = engine;
        _engine.Broadcast += msg => _out.Writer.TryWrite(msg);
    }

    public void Start(int preferred)
    {
        for (int p = preferred; p < preferred + 20; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                _listener = l;
                Port = p;
                break;
            }
            catch (SocketException) { }
        }
        if (_listener == null) throw new InvalidOperationException("No free local port for the editor");
        _ = Task.Run(AcceptLoop);
        _ = Task.Run(PumpEvents);
    }

    public string Url => $"http://127.0.0.1:{Port}/";

    public bool EditorOpen { get { lock (_sse) return _sse.Count > 0; } }

    async Task AcceptLoop()
    {
        while (_listener != null)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(); } catch { break; }
            _ = Task.Run(() => Handle(c));
        }
    }

    async Task PumpEvents()
    {
        _ = Task.Run(async () =>
        {
            // Heartbeat: lets us notice closed editor windows (and so start the idle light show).
            while (_listener != null) { await Task.Delay(10_000); _out.Writer.TryWrite(": ping"); }
        });
        await foreach (var msg in _out.Reader.ReadAllAsync())
        {
            var bytes = Encoding.UTF8.GetBytes((msg.StartsWith(':') ? msg : "data: " + msg) + "\n\n");
            NetworkStream[] clients;
            lock (_sse) clients = _sse.ToArray();
            foreach (var s in clients)
            {
                try
                {
                    using var cts = new CancellationTokenSource(1000);
                    await s.WriteAsync(bytes, cts.Token);
                }
                catch { lock (_sse) _sse.Remove(s); try { s.Dispose(); } catch { } }
            }
        }
    }

    sealed record Req(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)
    {
        public string BodyText => Encoding.UTF8.GetString(Body);
    }

    static async Task<Req?> Read(NetworkStream s)
    {
        var buf = new byte[8192];
        var ms = new MemoryStream();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await s.ReadAsync(buf);
            if (n <= 0) return null;
            ms.Write(buf, 0, n);
            if (ms.Length > 32 * 1024) return null;
            var arr = ms.GetBuffer();
            for (int i = 3; i < ms.Length; i++)
                if (arr[i - 3] == '\r' && arr[i - 2] == '\n' && arr[i - 1] == '\r' && arr[i] == '\n') { headerEnd = i + 1; break; }
        }
        var all = ms.ToArray();
        var head = Encoding.ASCII.GetString(all, 0, headerEnd).Split("\r\n");
        var first = head[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        int len = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var l) ? l : 0;
        if (len < 0 || len > 8 * 1024 * 1024) return null;
        var body = new byte[len];
        int have = Math.Min(len, all.Length - headerEnd);
        Array.Copy(all, headerEnd, body, 0, have);
        while (have < len)
        {
            int n = await s.ReadAsync(body.AsMemory(have));
            if (n <= 0) return null;
            have += n;
        }
        var path = first[1];
        int q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        return new Req(first[0].ToUpperInvariant(), path, headers, body);
    }

    static async Task Send(NetworkStream s, int code, string type, byte[] body)
    {
        var reason = code switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
    }

    static Task Json(NetworkStream s, string json, int code = 200) => Send(s, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
    static Task Error(NetworkStream s, int code, string msg) => Json(s, "{\"error\":" + PadDeck.Json.Str(msg) + "}", code);

    bool LocalHost(Req r)
    {
        if (!r.Headers.TryGetValue("Host", out var host)) return false;
        if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}") return false;
        if (r.Headers.TryGetValue("Origin", out var origin) &&
            origin != $"http://127.0.0.1:{Port}" && origin != $"http://localhost:{Port}") return false;
        return true;
    }

    async Task Handle(TcpClient client)
    {
        bool keep = false;
        var s = client.GetStream();
        try
        {
            var r = await Read(s);
            if (r == null) return;
            if (!LocalHost(r)) { await Error(s, 403, "Forbidden"); return; }

            if (r.Method == "GET" && !r.Path.StartsWith("/api/"))
            {
                var name = r.Path == "/" ? "index.html" : r.Path.TrimStart('/');
                var res = Resource(name);
                if (res == null) { await Error(s, 404, "Not found"); return; }
                var type = name.EndsWith(".js") ? "text/javascript; charset=utf-8"
                         : name.EndsWith(".css") ? "text/css; charset=utf-8"
                         : name.EndsWith(".svg") ? "image/svg+xml" : "text/html; charset=utf-8";
                await Send(s, 200, type, res);
                return;
            }

            if (r.Path == "/api/events")
            {
                var head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: keep-alive\r\n\r\n";
                await s.WriteAsync(Encoding.ASCII.GetBytes(head));
                lock (_sse) _sse.Add(s);
                keep = true;
                // Status goes through the normal queue so nothing that changes meanwhile can be missed.
                _out.Writer.TryWrite("{\"type\":\"status\",\"status\":" + _engine.StatusJson() + "}");
                _engine.ForceLedBroadcast();
                // The editor never sends anything on this connection, so a finished read means it closed.
                _ = Task.Run(async () =>
                {
                    var buf = new byte[64];
                    try { while (await s.ReadAsync(buf) > 0) { } } catch { }
                    lock (_sse) _sse.Remove(s);
                    try { s.Dispose(); client.Dispose(); } catch { }
                });
                return;
            }

            // Every other API call needs the custom header (forces a CORS preflight that we never approve).
            if (!r.Headers.TryGetValue("X-PadDeck", out var hv) || hv != "1") { await Error(s, 403, "Missing X-PadDeck header"); return; }

            await Api(s, r);
        }
        catch (Exception ex)
        {
            Log.Write("http: " + ex.Message);
            try { await Error(s, 500, ex.Message); } catch { }
        }
        finally
        {
            if (!keep) { try { s.Dispose(); client.Dispose(); } catch { } }
        }
    }

    async Task Api(NetworkStream s, Req r)
    {
        JsonNode? body = null;
        if (r.Method == "POST" && r.Path != "/api/config" && r.Body.Length > 0)
        {
            try { body = JsonNode.Parse(r.Body); } catch { await Error(s, 400, "Invalid JSON"); return; }
        }

        switch (r.Method, r.Path)
        {
            case ("GET", "/api/state"):
                await Json(s, "{\"config\":" + ConfigStore.Serialize(_engine.Config) +
                              ",\"status\":" + _engine.StatusJson() +
                              ",\"autostart\":" + ((GetAutostart?.Invoke() ?? false) ? "true" : "false") +
                              ",\"configPath\":" + PadDeck.Json.Str(ConfigStore.FilePath) +
                              ",\"revision\":" + _engine.Revision + "}");
                return;

            case ("POST", "/api/config"):
                Config cfg;
                try { cfg = ConfigStore.Parse(r.BodyText); }
                catch (Exception ex) { await Error(s, 400, "Config not saved: " + ex.Message); return; }
                // An editor window sends the revision it loaded; if the layout changed since, its copy is out of date.
                // A window from an older PadDeck doesn't send one: make it reload rather than trust its copy.
                if (!r.Headers.TryGetValue("X-Config-Rev", out var rv) || !int.TryParse(rv, out var expected))
                { await Error(s, 409, "This editor window is out of date: press F5 to reload it"); return; }
                int rev = _engine.Apply(cfg, expected);
                if (rev < 0) { await Error(s, 409, "The layout changed in another window"); return; }
                await Json(s, "{\"ok\":true,\"revision\":" + rev + "}");
                return;

            case ("POST", "/api/press"):
                int id = (int?)body?["id"] ?? 0;
                if (id < 11 || id > 98) { await Error(s, 400, "Bad button id"); return; }
                _engine.TestPress(id, Math.Clamp((int?)body?["ms"] ?? 120, 20, 3000));
                await Json(s, "{\"ok\":true}");
                return;

            case ("POST", "/api/page"):
                _engine.SetPage((int?)body?["page"] ?? 0);
                await Json(s, "{\"ok\":true}");
                return;

            case ("POST", "/api/pause"):
                _engine.SetPaused((bool?)body?["paused"] ?? false);
                await Json(s, "{\"ok\":true}");
                return;

            case ("POST", "/api/rescan"):
                _ = Task.Run(_engine.Pad.Rescan);
                await Json(s, "{\"ok\":true}");
                return;

            case ("POST", "/api/autostart"):
                SetAutostart?.Invoke((bool?)body?["enabled"] ?? false);
                await Json(s, "{\"ok\":true,\"autostart\":" + ((GetAutostart?.Invoke() ?? false) ? "true" : "false") + "}");
                return;

            case ("POST", "/api/pick"):
                var kind = (string?)body?["kind"] ?? "any";
                var picked = PickFile == null ? null : await Task.Run(() => PickFile(kind));
                await Json(s, "{\"path\":" + PadDeck.Json.Str(picked) + "}");
                return;

            case ("POST", "/api/lightshow"):
                _engine.ShowCommand((string?)body?["op"] ?? "next");
                await Json(s, "{\"ok\":true}");
                return;

            case ("GET", "/api/audio/devices"):
                var (outs, ins) = await _engine.Audio.Thread.Invoke(() => (_engine.Audio.Devices(false), _engine.Audio.Devices(true)));
                string Dev(Audio.Device d) => $"{{\"id\":{PadDeck.Json.Str(d.Id)},\"name\":{PadDeck.Json.Str(d.Name)},\"default\":{(d.IsDefault ? "true" : "false")}}}";
                await Json(s, "{\"outputs\":[" + string.Join(",", outs.Select(Dev)) + "],\"inputs\":[" + string.Join(",", ins.Select(Dev)) + "]}");
                return;

            case ("POST", "/api/windows/check"):
                // Can PadDeck see this app's windows right now? (shown next to the "already open" option)
                var wpath = (string?)body?["path"];
                var wmatch = (string?)body?["match"];
                var (wst, wcount, wname) = await AppWindows.Thread.Invoke(() =>
                {
                    var t = AppWindows.Resolve(wpath, wmatch);
                    if (t == null) return ("unknown", 0, (string?)null);
                    var st = AppWindows.Find(t, out var w);
                    return (st.ToString().ToLowerInvariant(), w.Count, t.ExeName);
                });
                await Json(s, $"{{\"state\":\"{wst}\",\"count\":{wcount},\"process\":{PadDeck.Json.Str(wname)}}}");
                return;

            case ("GET", "/api/windows"):
                var desc = await AppWindows.Thread.Invoke(AppWindows.Describe);
                await Json(s, PadDeck.Json.Str(desc));
                return;

            case ("GET", "/api/apps"):
                var startApps = await StartApps.List();
                await Json(s, "[" + string.Join(",", startApps.Select(a =>
                    $"{{\"name\":{PadDeck.Json.Str(a.name)},\"id\":{PadDeck.Json.Str(a.id)}}}")) + "]");
                return;

            case ("GET", "/api/audio/apps"):
                var running = await _engine.Audio.Thread.Invoke(_engine.Audio.Apps);
                await Json(s, "[" + string.Join(",", running.OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase).Select(a =>
                    $"{{\"name\":{PadDeck.Json.Str(a.Name)},\"label\":{PadDeck.Json.Str(a.Label)},\"color\":\"#{a.Color:x6}\",\"playing\":{(a.Playing ? "true" : "false")}}}")) + "]");
                return;

            case ("POST", "/api/apps/color"):
                // A new app pad takes its icon's colour.
                var cpath = (string?)body?["path"];
                if (string.IsNullOrWhiteSpace(cpath)) { await Error(s, 400, "No path"); return; }
                cpath = Environment.ExpandEnvironmentVariables(cpath.Trim().Trim('"'));
                var col = await AppWindows.Thread.Invoke(() =>
                {
                    try { return Gdi.AppIconColor(cpath); } catch { return null; }
                });
                await Json(s, col is { } cv ? $"{{\"color\":\"#{cv:x6}\"}}" : "{\"color\":null}");
                return;

            case ("GET", "/api/discord/lists"):
                var dl = await DiscordCtl.Thread.Invoke(DiscordCtl.Lists);
                await Json(s, "{\"servers\":[" + string.Join(",", dl.servers.Select(PadDeck.Json.Str)) +
                              "],\"channels\":[" + string.Join(",", dl.channels.Select(PadDeck.Json.Str)) + "]}");
                return;

            case ("GET", "/api/audio/sessions"):
                var names = await _engine.Audio.Thread.Invoke(_engine.Audio.Sessions);
                await Json(s, "[" + string.Join(",", names.Select(PadDeck.Json.Str)) + "]");
                return;

            case ("GET", "/api/obs/scenes"):
            case ("GET", "/api/obs/inputs"):
                var list = await _engine.Obs.Names(r.Path.EndsWith("scenes") ? "scenes" : "inputs");
                await Json(s, "[" + string.Join(",", list.Select(PadDeck.Json.Str)) + "]");
                return;
        }
        await Error(s, 404, "Unknown endpoint");
    }

    static readonly Dictionary<string, byte[]> Cache = new();

    static byte[]? Resource(string name)
    {
        if (name.Contains("..")) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var b)) return b;
            using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui/" + name);
            if (st == null) return null;
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            return Cache[name] = ms.ToArray();
        }
    }

    public void Dispose()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _out.Writer.TryComplete();
    }
}
