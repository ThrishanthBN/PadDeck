# PadDeck

Turns a **Novation Launchpad Mini MK3** (also works with a Launchpad X) into a stream deck. All 80 buttons work, including the top row and the right-hand column.

## Download

Get `PadDeck.exe` from the [latest release](https://github.com/ThrishanthBN/PadDeck/releases/latest) and run it. Windows 10 or 11, 64-bit. It's one file with the .NET runtime built in, so there's nothing to install. Windows SmartScreen may warn the first time because the exe isn't signed: click **More info → Run anyway**.

## Run it

Installed copy: `%LOCALAPPDATA%\Programs\PadDeck\PadDeck.exe`, with a **PadDeck** entry in the Start menu. Fresh builds land in `dist\PadDeck.exe`; copy that over the installed one to update (quit PadDeck first). Nothing else to install: the .NET runtime is built into the exe.

- PadDeck sits in the system tray. Left-click the tray icon to open the editor, right-click for the menu (pause, reconnect, start with Windows, quit).
- Running the exe a second time just opens the editor.
- Close any other app that holds the Launchpad first (MIDI Mixer, Ableton, Novation Components). PadDeck tells you if the device is busy.
- Quitting PadDeck puts the Launchpad back in its normal mode.

## What a button can do

| Action | Notes |
|---|---|
| Press keys | Any combo. **Record** captures it from your keyboard. F13 to F24 are ideal for OBS, Discord and games because no physical key clashes with them. |
| Type text | Types into whatever window is focused (full Unicode). |
| Open app, file or website | **Choose an app** lists everything in your Start menu by name, including Microsoft Store apps, with search. Or pick any file, folder or URL. By default an app pad brings the app to the front if it's behind other windows, restores it if minimized, minimizes it if it's already in front, and closes it if you hold the pad. Both can be changed per pad. |
| Discord call | Mute, deafen, camera, share screen, hang up, decline a call, open the voice channel's chat. Presses Discord's own buttons, so nothing needs setting up in Discord. |
| Run a command | Runs hidden through `cmd.exe`. |
| Media control | Play/pause, next, previous, stop, system volume. |
| Volume | Master, microphone, the focused app, or any app by process name (`spotify`, `discord`, `chrome`): mute, unmute, up, down, set. |
| OBS Studio | Switch scene, record, stream, replay buffer + save clip, virtual camera, mute sources, studio-mode transition. |
| Switch page | Go to a page, next or previous. |
| Play a sound | Soundboard: mp3/wav/wma, overlapping, per-button volume, "stop if playing" mode. |
| Wait | Delay between steps, so one button can run a sequence. |

**Behaviours:** *Press* (run once), *Toggle* (two sets of actions, stays lit while on), *Hold* (keys stay down while your finger is on the pad: push-to-talk).

**Live lights:** a button can light up from real state: mic muted, speakers muted, an app muted, OBS recording / streaming / replay / virtual cam, the live OBS scene, an OBS source muted. Toggle buttons and page buttons light automatically.

**Faders:** any grid column *or row* can become an 8-step volume fader for master, mic, the focused app or a specific app. The lit pads show the current level and turn red while muted.

**Auto mixer:** turn it on for any page (Page settings) and PadDeck fills it with every app that has sound open, each coloured from its own icon. It's apps only (speakers and mic can be added in Page settings if you want them). Apps appear and disappear by themselves and keep their slot while they run. In column layout, the top-row button above each app mutes it. The stock "Mixer" page is an auto mixer.

**Mute an app from anywhere:** the "Mute an app" preset lists the apps the mixer can see; pick one and the button takes the app's icon colour and turns red while it's muted. In the mixer, selecting an app's fader offers "Make a mute button" to paste on any pad, on any page.

**Audio devices:** a button can cycle your output or input device, or jump to a specific one (headphones vs speakers, say), and can light up while that device is the default.

**Music (top-left quarter of Main):** the pads follow whichever app is playing (Spotify, Apple Music, a browser...), so there are no per-app shortcuts to set up and they work while the app is in the background.

| Row | Pads |
|---|---|
| Top | Music app (in its icon colour: brings it up, or minimizes it), previous, play / pause (lit while playing), next |
| 2 | Shuffle (lit while on), repeat (all → one → off; lit while on, pulses on "one"), back 10 s, forward 10 s |
| 3 | Music app quieter, louder, mute (red while muted), like / favourite |
| 4 | Music volume 25 / 50 / 75 / 100 %, lit up to the current level |

They use Windows' own media controls (the ones behind the volume pop-up). Where an app doesn't share something with Windows (Apple Music doesn't share shuffle, repeat or the song position), PadDeck presses or moves the app's own button or slider instead. Volume and mute are the app's own volume, as in the mixer. Like presses the app's heart / Favourite button. The "Music" action can go on any pad.

**App colours:** a new app pad takes its icon's colour (Store apps included). A few apps ship a plain white icon on Windows (Apple Music): those get the colour they're known by.

**Light show:** starts on its own when idle if you turn that on in Settings, and any pad stops it (the first press only wakes the pads, so you can't fire something by accident). Effects: rainbow wave, plasma, digital rain, sparkle, ripples, fire, breathe, radar, or ocean (deep blue, azure, cyan and teal-mint drifting and folding into each other like light under water, with soft white glints and twinkling stars). Settings → Light show can play one effect, and a pad can start a specific effect ("Play Ocean"). The "Light show", "Light show brighter" and "Light show dimmer" presets put the controls on a pad if you want them.

**Discord page:** the top row is for calls.

| Pad | What it does | Light |
|---|---|---|
| Mic | Mute / unmute | Red while muted |
| Camera | Camera on / off | Bright while on |
| Share screen | Tap: shares "Screen 1" straight away (or whatever you set: "Screen 2", or an app's name), then puts you back where you were. Tap again to stop. Hold: opens Discord's picker so you can choose. | Bright while sharing |
| Deafen | Deafen / undeafen | Red while deafened |
| Voice chat | Opens your call's text chat (bringing Discord up). Press again to close it. | |
| Overlay | Discord's overlay key (Shift+`) | |
| Decline call | Declines an incoming call | |
| Hang up | Leaves the call | Lit only while you're in a call |

There's also a Discord pad (bring up / minimize / hold to close to the tray), a Discord volume fader and a "mute Discord's sound" pad.

**Join a voice channel:** the "Join VC" pad (and the "Join a voice channel" preset, for as many as you want) joins a voice channel on any of your servers. Set the server and channel by name in the editor (it suggests your servers, and the voice channels of the server Discord is showing), or paste the channel's link (right-click the channel in Discord → Copy Link). PadDeck brings Discord up, clicks the server and the channel, then puts Discord and the window you were in back. A channel inside a collapsed category can't be found: expand the category in Discord.

These pads press Discord's own buttons through Windows UI Automation (the accessibility interface screen readers use), because Discord has no keyboard shortcut for the camera, screen share or hanging up. Nothing needs setting up in Discord. They work while Discord is in the background or closed to the tray, and the lights show Discord's real state even if you click in Discord itself. They look for Discord's English button names. If Discord hasn't been on screen since it started, its window may flash once on the first press. If you use a different overlay key, change it on the Overlay pad.

**Move a button:** drag it to another pad. Dropping on a taken pad swaps the two; hold Ctrl to copy instead. Ctrl+Z undoes.

**Reset or delete:** right-click any pad in the editor (or use the buttons under it) to reset it to a blank button or delete its mapping completely. Ctrl+Z undoes.

**Pages:** unlimited pages. Each button can belong to one page or to every page. By default the right column switches pages.

## Why it's fast

- MIDI input comes in through a native Windows callback and is handed straight to a dedicated high-priority thread that fires the action. There's no polling and no event loop in between, so the delay is down to USB and Windows itself.
- Actions that might take time (opening apps, commands, delays) run off the input path, so they never hold up the next press.
- LEDs are updated with a single RGB SysEx message containing only the lights that changed.
- No Electron. The background app is a single ~14 MB exe. Measured in Task Manager: 4 to 5 MB in the background, steady over time. While the editor is open it grows to about 20 MB, and that memory goes back to Windows as soon as the editor closes (also after a light show, and every 10 minutes). Around 1% of one CPU core. The editor is a web page served locally.

## OBS setup

In OBS: **Tools → WebSocket Server Settings → Enable WebSocket server**. Click **Show Connect Info**, copy the password, then in PadDeck open **Settings → OBS Studio**, turn on *Connect to OBS* and paste it.

## Files

- Config: `%APPDATA%\PadDeck\config.json` (saved automatically, human-readable)
- Log: `%APPDATA%\PadDeck\paddeck.log`

The editor only listens on `127.0.0.1` and rejects requests from other websites, so nothing else on your network or in your browser can trigger your buttons.

## Build from source

Needs the .NET 10 SDK.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output: `dist\PadDeck.exe`.

## License

MIT. See [LICENSE](LICENSE).
