using System.Text.Json;
using System.Text.Json.Serialization;

namespace PadDeck;

// Button ids are the Launchpad programmer-mode numbers:
//   top row 91..98, grid rows 81..88 (top) down to 11..18 (bottom),
//   right column 89, 79, ... 19, logo 99 (light only).

public sealed class Config
{
    public int Version { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    public List<Page> Pages { get; set; } = new();
    /// <summary>Bindings shared by every page (a page's own binding wins).</summary>
    public Dictionary<string, ButtonDef> Global { get; set; } = new();
}

public sealed class Settings
{
    public int Brightness { get; set; } = 100;          // 0..100, hardware brightness
    public bool PressFlash { get; set; } = true;        // light up a pad while held
    public string LogoColor { get; set; } = "#3b82f6";
    public bool ObsEnabled { get; set; } = false;
    public string ObsUrl { get; set; } = "ws://127.0.0.1:4455";
    public string? ObsPassword { get; set; }
    public int TapMs { get; set; } = 20;                // how long a hotkey tap is held
    public bool IdleAuto { get; set; } = false;         // start the light show by itself when idle (opt-in)
    public int IdleSeconds { get; set; } = 120;         // ...after this long untouched
    public int ShowBrightness { get; set; } = 100;      // light show brightness, 10..100
    public string IdleEffect { get; set; } = "cycle";   // cycle | one of Engine.Effects
    public bool WakePressOnly { get; set; } = true;     // first press during a light show only wakes the pads
}

public sealed class Page
{
    public string Name { get; set; } = "Page";
    public Dictionary<string, ButtonDef> Buttons { get; set; } = new();
    public List<FaderDef> Faders { get; set; } = new();
    /// <summary>Fill the faders automatically with whatever apps are playing audio.</summary>
    public bool AutoMixer { get; set; }
    public bool AutoHorizontal { get; set; }
    public bool AutoSystem { get; set; } = false;       // also include master + mic in the auto mixer (apps only by default)
}

/// <summary>A grid column (vertical) or row (horizontal) turned into an 8-step volume fader.</summary>
public sealed class FaderDef
{
    public int Column { get; set; } = 1;                // vertical: 1..8 left to right
    public int Row { get; set; } = 1;                   // horizontal: 1..8 bottom to top
    public bool Horizontal { get; set; }
    public string Target { get; set; } = "master";      // master | mic | focused | <process name>
    public string Color { get; set; } = "#22c55e";
    public string? Label { get; set; }                  // display only (auto mixer)
}

public sealed class ButtonDef
{
    public string? Label { get; set; }
    public string Color { get; set; } = "#000000";
    public string? ActiveColor { get; set; }
    public string Effect { get; set; } = "static";      // static | flash | pulse
    public string Mode { get; set; } = "press";         // press | toggle | hold
    public string? Feedback { get; set; }               // see Engine.FeedbackState
    public string? FeedbackArg { get; set; }
    public List<ActionDef> Actions { get; set; } = new();
    public List<ActionDef> OffActions { get; set; } = new();
}

public sealed class ActionDef
{
    public string Type { get; set; } = "hotkey";
    // hotkey
    public string? Keys { get; set; }
    // type
    public string? Text { get; set; }
    // open / command / sound
    public string? Path { get; set; }
    public string? Args { get; set; }
    // open (apps only): what to do when the app is already running. null/"toggle" = bring to front / restore,
    // or minimize if it's in front (the default), "focus" = bring to front only, "launch" = open it again
    public string? Window { get; set; }
    // open: holding the pad closes the app (toggle/focus only). null = on
    public bool? HoldClose { get; set; }
    // discord: Op = mute | deafen | camera | screen | disconnect | decline | chat | join.
    // screen: Target = what to share. join: Target = server, Text = voice channel, or Path = channel link.
    // open: process name to recognise the app's windows by, when it can't be worked out
    public string? Match { get; set; }
    // media / volume / obs / page op
    public string? Op { get; set; }
    // volume target, obs scene/input name
    public string? Target { get; set; }
    public double? Value { get; set; }
    // delay
    public int? Ms { get; set; }
    // page index (0-based)
    public int? Page { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Config))]
internal partial class JsonCtx : JsonSerializerContext { }

internal static class ConfigStore
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PadDeck");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize(File.ReadAllText(FilePath), JsonCtx.Default.Config);
                if (cfg != null)
                {
                    int before = cfg.Version;
                    Normalize(cfg);
                    if (cfg.Version != before) Save(cfg);     // persist migrations
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Config load failed, keeping a backup and starting fresh: " + ex.Message);
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
        }
        var d = Defaults();
        Save(d);
        return d;
    }

    public static void Save(Config cfg)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonCtx.Default.Config));
        File.Move(tmp, FilePath, true);
    }

    public static Config Parse(string json) =>
        Normalize(JsonSerializer.Deserialize(json, JsonCtx.Default.Config) ?? throw new InvalidDataException("Empty config"));

    public static string Serialize(Config cfg) => JsonSerializer.Serialize(cfg, JsonCtx.Default.Config);

    static Config Normalize(Config c)
    {
        c.Settings ??= new();
        c.Pages ??= new();
        c.Global ??= new();
        if (c.Pages.Count == 0) c.Pages.Add(new Page { Name = "Main" });
        foreach (var p in c.Pages)
        {
            p.Buttons ??= new();
            p.Faders ??= new();
            foreach (var b in p.Buttons.Values) { b.Actions ??= new(); b.OffActions ??= new(); }
        }
        foreach (var b in c.Global.Values) { b.Actions ??= new(); b.OffActions ??= new(); }
        c.Settings.Brightness = Math.Clamp(c.Settings.Brightness, 0, 100);
        c.Settings.TapMs = Math.Clamp(c.Settings.TapMs, 1, 500);
        c.Settings.IdleSeconds = Math.Clamp(c.Settings.IdleSeconds, 0, 24 * 3600);
        c.Settings.IdleEffect ??= "cycle";
        if (c.Settings.IdleEffect == "honeycomb") c.Settings.IdleEffect = "ocean";     // briefly had that name
        c.Settings.ShowBrightness = Math.Clamp(c.Settings.ShowBrightness, 10, 100);
        Migrate(c);
        return c;
    }

    const int CurrentVersion = 8;

    /// <summary>Bring older configs up to date without touching anything the user customised.</summary>
    static void Migrate(Config c)
    {
        if (c.Version >= CurrentVersion) return;
        if (c.Version < 2)
        {
            // v2: light-show button in the bottom-right corner, and the stock "Mixer" page becomes an auto mixer.
            if (!c.Global.ContainsKey("19") && !c.Pages.Any(p => p.Buttons.ContainsKey("19")))
                c.Global["19"] = LightShowButton();
            var stockTargets = new[] { "master", "mic", "focused", "spotify", "discord", "chrome" };
            foreach (var p in c.Pages)
            {
                if (p.Name == "Mixer" && !p.AutoMixer &&
                    p.Faders.Select(f => f.Target).SequenceEqual(stockTargets))
                {
                    p.Faders.Clear();
                    for (int col = 1; col <= 6; col++) p.Buttons.Remove((90 + col).ToString());
                    p.AutoMixer = true;
                }
            }
        }
        if (c.Version < 3)
        {
            // v3: auto mixer is apps only; brightness pads above the light-show button.
            foreach (var p in c.Pages) if (p.AutoMixer) p.AutoSystem = false;
            bool Free(string id) => !c.Global.ContainsKey(id) && !c.Pages.Any(p => p.Buttons.ContainsKey(id));
            if (Free("39")) c.Global["39"] = ShowBrightnessButton(true);
            if (Free("29")) c.Global["29"] = ShowBrightnessButton(false);
        }
        if (c.Version < 4)
        {
            // v4: a Discord page, with a page button under the existing ones.
            if (!c.Pages.Any(p => p.Name.Equals("Discord", StringComparison.OrdinalIgnoreCase)))
            {
                c.Pages.Add(DiscordPage());
                int idx = c.Pages.Count - 1;
                // A leftover button for a deleted page may already point at this index: make it the Discord button.
                var stale = c.Global.Values.FirstOrDefault(b => b.Actions.Any(x => x.Type == "page" && (x.Op ?? "goto") == "goto" && x.Page == idx));
                if (stale != null)
                {
                    if (stale.Label == null || stale.Label.StartsWith("Page ")) stale.Label = "Discord";
                }
                else
                {
                    bool Free(string id) => !c.Global.ContainsKey(id) && !c.Pages.Any(p => p.Buttons.ContainsKey(id));
                    var slot = new[] { "59", "49" }.FirstOrDefault(Free);
                    if (slot != null) c.Global[slot] = PageButton("Discord", idx, "#5865f2");
                }
            }
        }
        if (c.Version < 5)
        {
            // v5: the Discord page presses Discord's own buttons instead of keybinds, and keeps only the call controls.
            // Pads the user changed or added are left alone.
            var page = c.Pages.FirstOrDefault(p => p.Name.Equals("Discord", StringComparison.OrdinalIgnoreCase));
            if (page != null)
            {
                var stock = new Dictionary<string, string>
                {
                    ["81"] = "Mute", ["82"] = "Deafen", ["83"] = "Push to talk", ["84"] = "Voice activity / PTT",
                    ["86"] = "Streamer mode", ["87"] = "Overlay", ["88"] = "Discord",
                    ["71"] = "Answer call", ["72"] = "Decline call", ["74"] = "Quick switcher", ["75"] = "Search",
                    ["77"] = "Mark read", ["78"] = "Mark server read",
                    ["61"] = "Channel up", ["62"] = "Channel down", ["63"] = "Unread up", ["64"] = "Unread down",
                    ["65"] = "Server up", ["66"] = "Server down", ["67"] = "Back", ["68"] = "Forward",
                };
                foreach (var (id, label) in stock)
                    if (page.Buttons.TryGetValue(id, out var b) && b.Label == label) page.Buttons.Remove(id);
                foreach (var (id, b) in DiscordPage().Buttons)
                    page.Buttons.TryAdd(id, b);
            }
        }
        if (c.Version < 6)
        {
            // v6: a Deafen pad, and Share screen shares "Screen 1" on a tap (hold for Discord's picker).
            var page = c.Pages.FirstOrDefault(p => p.Name.Equals("Discord", StringComparison.OrdinalIgnoreCase));
            if (page != null)
            {
                var fresh = DiscordPage().Buttons;
                page.Buttons.TryAdd("86", fresh["86"]);
                if (page.Buttons.TryGetValue("83", out var share) && share.Actions.FirstOrDefault(a => a.Type == "discord" && a.Op == "screen") is { Target: null } sa)
                    sa.Target = "Screen 1";
            }
        }
        if (c.Version < 7)
        {
            // v7: a "join a voice channel" pad on the Discord page.
            var page = c.Pages.FirstOrDefault(p => p.Name.Equals("Discord", StringComparison.OrdinalIgnoreCase));
            page?.Buttons.TryAdd("72", DiscordPage().Buttons["72"]);
        }
        if (c.Version < 8)
        {
            // v8: the light-show pads come off the grid (still available as presets and in Settings), and the
            // top-left quarter of Main becomes music controls that follow whichever app is playing.
            var lightShow = new Dictionary<string, string> { ["19"] = "Light show", ["29"] = "Show dimmer", ["39"] = "Show brighter" };
            foreach (var (id, label) in lightShow)
                if (c.Global.TryGetValue(id, out var b) && b.Label == label && b.Actions.All(a => a.Type == "lightshow")) c.Global.Remove(id);

            var main = c.Pages.FirstOrDefault(p => p.Name.Equals("Main", StringComparison.OrdinalIgnoreCase)) ?? c.Pages[0];
            // The old media-key pads are replaced; anything else the user put there stays.
            foreach (var (id, label) in new Dictionary<string, string> { ["82"] = "Prev", ["83"] = "Play", ["84"] = "Next" })
                if (main.Buttons.TryGetValue(id, out var b) && b.Label == label && b.Actions.All(a => a.Type == "media")) main.Buttons.Remove(id);
            foreach (var (id, b) in MusicPads()) main.Buttons.TryAdd(id, b);
        }
        c.Version = CurrentVersion;
    }

    static ButtonDef PageButton(string name, int index, string active) => new()
    {
        Label = name, Color = "#1e293b", ActiveColor = active,
        Actions = { A("page", "goto", page: index) }
    };

    /// <summary>
    /// Voice-call controls. These press Discord's own buttons (see DiscordCtl), so nothing has to be set up in
    /// Discord and they work while it's in the background or closed to the tray.
    /// </summary>
    public static Page DiscordPage()
    {
        var p = new Page { Name = "Discord" };
        ButtonDef D(string label, string color, string op, string? feedback = null, string? active = null) => new()
        {
            Label = label, Color = color, ActiveColor = active, Feedback = feedback,
            Actions = { new ActionDef { Type = "discord", Op = op } }
        };

        // Top row of the grid: the call
        p.Buttons["81"] = D("Mic", "#16a34a", "mute", "discord.mute", "#dc2626");                    // red while muted
        p.Buttons["82"] = D("Camera", "#1e3a8a", "camera", "discord.camera", "#3b82f6");             // bright while on
        p.Buttons["83"] = D("Share screen", "#3b0764", "screen", "discord.screen", "#a855f7");       // bright while sharing
        p.Buttons["83"].Actions[0].Target = "Screen 1";                                               // tap shares this; hold opens the picker
        p.Buttons["86"] = D("Deafen", "#16a34a", "deafen", "discord.deafen", "#dc2626");             // red while deafened
        p.Buttons["84"] = D("Voice chat", "#5865f2", "chat");
        p.Buttons["85"] = new ButtonDef
        {
            // Discord's overlay lock key (Settings → Game Overlay). Change the keys here if you use a different one.
            Label = "Overlay", Color = "#6d28d9", Actions = { A("hotkey", keys: "Shift+`") }
        };
        p.Buttons["87"] = D("Decline call", "#b45309", "decline");
        p.Buttons["88"] = D("Hang up", "#450a0a", "disconnect", "discord.call", "#ef4444");          // lit only while you're in a call

        // Join a voice channel: pick the server and channel in the editor. Copy the pad for more channels.
        p.Buttons["72"] = D("Join VC", "#4338ca", "join");

        // Bring Discord up, minimize it, or hold to close it to the tray
        p.Buttons["71"] = new ButtonDef
        {
            Label = "Discord", Color = "#5865f2",
            Actions = { new ActionDef { Type = "open", Path = DiscordCtl.AppPath, Text = "Discord", Window = "toggle" } }
        };

        // Discord's sound on your PC
        p.Buttons["21"] = new ButtonDef
        {
            Label = "Mute Discord audio", Color = "#3b4a9c", ActiveColor = "#dc2626", Feedback = "app.mute", FeedbackArg = "discord",
            Actions = { A("volume", "togglemute", "discord") }
        };
        p.Faders.Add(new FaderDef { Horizontal = true, Row = 1, Column = 1, Target = "discord", Color = "#5865f2", Label = "Discord volume" });
        return p;
    }

    /// <summary>
    /// Music controls for the top-left quarter of Main (columns 1-4, top four rows). They follow whichever app is
    /// playing (Spotify, Apple Music, a browser...): see Player.
    /// </summary>
    public static Dictionary<string, ButtonDef> MusicPads()
    {
        ButtonDef P(string label, string color, string op, double? value = null, string? feedback = null, string? active = null, string? arg = null) => new()
        {
            Label = label, Color = color, ActiveColor = active, Feedback = feedback, FeedbackArg = arg,
            Actions = { new ActionDef { Type = "player", Op = op, Value = value } }
        };
        var d = new Dictionary<string, ButtonDef>
        {
            // Top row: the app (its own colour), previous, play/pause, next
            ["81"] = P("Music app", "player", "app"),
            ["82"] = P("Prev", "#0ea5e9", "prev"),
            ["83"] = P("Play / pause", "#0369a1", "playpause", feedback: "player.playing", active: "#22c55e"),
            ["84"] = P("Next", "#0ea5e9", "next"),
            // Shuffle, repeat (all → one → off; pulses on one), back / forward 10 seconds
            ["71"] = P("Shuffle", "#164e63", "shuffle", feedback: "player.shuffle", active: "#22d3ee"),
            ["72"] = P("Repeat", "#164e63", "repeat", feedback: "player.repeat", active: "#22d3ee"),
            ["73"] = P("−10 s", "#334155", "seek", -10),
            ["74"] = P("+10 s", "#334155", "seek", 10),
            // The music app's own volume, mute, and like / favourite
            ["61"] = P("Music vol −", "#6d28d9", "voldown", 5),
            ["62"] = P("Music vol +", "#a855f7", "volup", 5),
            ["63"] = P("Mute music", "#3b0764", "mute", feedback: "player.mute", active: "#dc2626"),
            ["64"] = P("Like", "#9d174d", "like"),
        };
        // Bottom row of the block: volume 25 / 50 / 75 / 100 %, lit up to the current level like a meter
        for (int i = 1; i <= 4; i++)
            d[(50 + i).ToString()] = P($"Music {i * 25}%", "#2e1065", "setvol", i * 25, "player.level", "#a855f7", (i * 25).ToString());
        return d;
    }

    static ButtonDef LightShowButton() => new()
    {
        Label = "Light show", Color = "#7c3aed", Effect = "pulse",
        Actions = { new ActionDef { Type = "lightshow", Op = "next" } }
    };

    static ButtonDef ShowBrightnessButton(bool up) => new()
    {
        Label = up ? "Show brighter" : "Show dimmer", Color = up ? "#a78bfa" : "#3b2a6b",
        Actions = { new ActionDef { Type = "lightshow", Op = up ? "brighter" : "dimmer" } }
    };

    static ActionDef A(string type, string? op = null, string? target = null, string? keys = null, int? page = null, double? value = null) =>
        new() { Type = type, Op = op, Target = target, Keys = keys, Page = page, Value = value };

    static ButtonDef B(string label, string color, params ActionDef[] actions) =>
        new() { Label = label, Color = color, Actions = actions.ToList() };

    /// <summary>A useful starting layout: media + mic on page 1, OBS on 2, a mixer on 3,
    /// right column switches pages on every page.</summary>
    public static Config Defaults()
    {
        var main = new Page { Name = "Main" };
        foreach (var (id, b) in MusicPads()) main.Buttons[id] = b;
        main.Buttons["85"] = new ButtonDef
        {
            Label = "Mic", Color = "#16a34a", ActiveColor = "#dc2626", Feedback = "mic.mute",
            Actions = { A("volume", "togglemute", "mic") }
        };
        main.Buttons["86"] = B("Vol −", "#a855f7", A("volume", "down", "master", value: 5));
        main.Buttons["87"] = B("Vol +", "#a855f7", A("volume", "up", "master", value: 5));
        main.Buttons["88"] = new ButtonDef
        {
            Label = "Mute", Color = "#a855f7", ActiveColor = "#dc2626", Feedback = "master.mute",
            Actions = { A("volume", "togglemute", "master") }
        };
        main.Buttons["75"] = B("Snip", "#f59e0b", A("hotkey", keys: "Win+Shift+S"));
        main.Buttons["76"] = B("Desktop", "#f59e0b", A("hotkey", keys: "Win+D"));
        main.Buttons["77"] = B("Browser", "#f59e0b", new ActionDef { Type = "open", Path = "https://" });
        main.Buttons["78"] = B("Task Mgr", "#f59e0b", A("hotkey", keys: "Ctrl+Shift+Esc"));

        var obs = new Page { Name = "OBS" };
        obs.Buttons["81"] = new ButtonDef { Label = "Record", Color = "#7f1d1d", ActiveColor = "#ef4444", Effect = "static", Feedback = "obs.record", Actions = { A("obs", "record_toggle") } };
        obs.Buttons["82"] = new ButtonDef { Label = "Stream", Color = "#1e3a8a", ActiveColor = "#3b82f6", Feedback = "obs.stream", Actions = { A("obs", "stream_toggle") } };
        obs.Buttons["83"] = new ButtonDef { Label = "Replay", Color = "#3f3f46", ActiveColor = "#eab308", Feedback = "obs.replay", Actions = { A("obs", "replay_toggle") } };
        obs.Buttons["84"] = B("Save clip", "#eab308", A("obs", "replay_save"));
        obs.Buttons["85"] = new ButtonDef { Label = "Virtual cam", Color = "#3f3f46", ActiveColor = "#22c55e", Feedback = "obs.virtualcam", Actions = { A("obs", "virtualcam_toggle") } };
        for (int i = 0; i < 4; i++)
        {
            var scene = "Scene " + (i + 1);
            obs.Buttons[(71 + i).ToString()] = new ButtonDef
            {
                Label = scene, Color = "#334155", ActiveColor = "#f8fafc", Feedback = "obs.scene", FeedbackArg = scene,
                Actions = { A("obs", "scene", scene) }
            };
        }

        var mixer = new Page { Name = "Mixer", AutoMixer = true };

        // The light show is started from Settings (or a pad you add from the presets).
        var cfg = new Config { Version = CurrentVersion, Pages = { main, obs, mixer, DiscordPage() } };
        string[] pageColors = { "#e2e8f0", "#e2e8f0", "#e2e8f0", "#5865f2" };
        for (int i = 0; i < 4; i++)
        {
            cfg.Global[(89 - i * 10).ToString()] = new ButtonDef
            {
                Label = cfg.Pages[i].Name, Color = "#1e293b", ActiveColor = pageColors[i],
                Actions = { A("page", "goto", page: i) }
            };
        }
        return cfg;
    }
}

internal static class Log
{
    static readonly object Gate = new();
    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ConfigStore.Dir);
                var path = Path.Combine(ConfigStore.Dir, "paddeck.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
