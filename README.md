# TSDraw: draw on TeamSpeak 6 streams

Slack-huddle-style drawing for TeamSpeak 6 screen shares. While you watch someone's
stream, click the pen and draw on it. The streamer sees your drawing **on their own
screen**, on top of whatever they're sharing, and everyone else watching sees it too.
Drawings fade out a few seconds after you lift the pen.

```
 viewer's TeamSpeak                                        streamer's PC
┌──────────────────────┐   peer-to-peer WebRTC link    ┌─────────────────────────┐
│ stream video         │ ────────────────────────────► │ TeamSpeak ─► TSDraw.exe │
│  + TSDraw pen layer  │ (handshake via TeamSpeak)     │   overlay on the shared │
└──────────────────────┘                               │   screen, hidden from   │
                                                       │   the stream capture    │
                                                       └─────────────────────────┘
```

Everyone who wants to draw or be drawn on runs **TSDraw.exe**: a ~100 KB tray app with
no install and no admin rights. **No server is needed.**

## How it works

- **No plugin API, so runtime injection:** TeamSpeak 6 extensions can only be CSS
  themes, and its UI files are hash-checked. TSDraw starts TeamSpeak with Chromium's
  `--remote-debugging-port` and injects the drawing layer at runtime. No TeamSpeak
  files are changed, and TeamSpeak updates don't remove it.
- **A link next to the stream:** TeamSpeak's own stream connection is sealed inside its
  native code, and it carries only audio and video. So each viewer's TSDraw opens its
  own WebRTC data channel straight to the streamer. It uses TeamSpeak's own STUN
  servers (`turn.teamspeak.com`), so it gets through home routers the same way the
  stream does. The streamer passes strokes on to its other viewers, so everyone sees
  everyone.
- **Handshake through TeamSpeak:** the two sides swap a one-time connection offer
  through each client's hidden TeamSpeak `metaData` field, which exists for plugin
  data and isn't shown anywhere in TeamSpeak. The offer is encrypted with a key derived
  from the stream's session ID, which only people who can see the stream know. It's
  removed again as soon as the link is up. Drawings themselves never touch the
  TeamSpeak server.

## Setup

1. Download **[TSDraw.exe](https://github.com/spoussa/tsdraw/releases/latest/download/TSDraw.exe)**,
   put it in a folder of its own (e.g. `Documents\TSDraw`), and run it. If Windows says
   "Windows protected your PC", click *More info → Run anyway*. A blue pen icon
   appears in the tray.
2. If TeamSpeak isn't running, TSDraw starts it with drawing enabled. If it's already
   running, a notification offers to restart it with drawing enabled; you'll drop out
   of voice for a few seconds.
3. Right-click the tray icon → **Start TSDraw with Windows**. Turn off TeamSpeak's own
   autostart (TeamSpeak Settings → Behavior), so TSDraw is the one that launches it.
4. Optionally set your display name (tray → *Display name…*). It's shown next to your strokes.

## Using it

**As a viewer:** hover over a stream you're watching and click the pen at its top right.
Draw with the left mouse button; pick a colour from the dots; the bin clears your
drawings; **Esc** or the pen button stops drawing. The small dot on the pen shows the link:

| Dot | Meaning |
| --- | --- |
| green | linked: the streamer sees what you draw |
| amber | connecting, or the streamer isn't running TSDraw (it keeps retrying every minute or so) |
| red | can't reach the streamer (see *When peer-to-peer can't connect*), or TSDraw.exe isn't running |

**As the streamer:** nothing to do. Drawings appear over the monitor or window you're
sharing. They are hidden from the capture, so your viewers see each drawing once, drawn
crisply on their side, not a second blurry copy inside the video.
Press **Ctrl+Alt+D** to draw on your own screen (viewers see it). Press Esc or
Ctrl+Alt+D again to stop. Right-click while drawing clears your drawings.

## Updates

TSDraw updates itself from this repo's [releases](https://github.com/spoussa/tsdraw/releases).
It checks shortly after it starts and every 6 hours after that. When a new version is
out, it swaps its own exe and restarts, then shows "TSDraw updated". It never restarts
while you're streaming; it waits until you stop. Tray → *Check for updates* checks
right away. Set `"autoUpdate": false` to turn this off.

## Publishing a new version

Edit the code, then run:

```bash
release.cmd patch -Notes "What changed"
```

That bumps `VERSION` (`patch`, `minor`, `major`, or `none`), builds, commits, tags,
pushes, and creates a GitHub release with `TSDraw.exe`. Everyone's TSDraw picks it up
automatically. `build.cmd` alone builds `dist\TSDraw.exe` for local testing. Don't run
your everyday copy from `dist\`: a running exe can't be overwritten by the build.

## When peer-to-peer can't connect

Direct links fail when both sides are behind strict NATs, for example two mobile or
CGNAT connections. TeamSpeak's own streams can still work there because TeamSpeak has
its own relay servers, but TSDraw can't use them. If you see the red dot with
"couldn't connect (network)", run the optional relay on a machine everyone can reach,
e.g. the one hosting your TeamSpeak server (Python 3.7+, no packages):

```bash
python3 tsdraw-relay.py --port 9989
```

Or, on Windows: `TSDraw.exe --relay 9989`. Open TCP 9989 on that machine. Then
set `"relay": "your.server.address"` in **everyone's** settings; `"auto"` means the
host of the TeamSpeak server you're connected to. With a relay set, TSDraw sends all
drawings through it instead of peer-to-peer. The relay only ever sees encrypted
bytes; `--password` plus `relayPassword` keeps strangers off it.

## Settings

`%APPDATA%\TSDraw\config.json` (tray → *Open settings file*); restart TSDraw after editing.

| Key | Default | |
| --- | --- | --- |
| `name` | Windows user name | shown next to your strokes |
| `relay` | `""` | `""` = peer-to-peer; `"auto"` / `"host"` / `"host:port"` = use a relay |
| `relayPort` | `9989` | port used with `auto` or a bare host |
| `relayPassword` | `""` | must match the relay's `--password` |
| `cdpPort` | `39222` | local DevTools port TeamSpeak is started with |
| `launchTeamSpeak` | `true` | start TeamSpeak when TSDraw starts |
| `teamspeakPath` | `""` | only if TeamSpeak.exe isn't found automatically |
| `drawHotkey` | `"Ctrl+Alt+D"` | streamer's draw-on-my-screen toggle |
| `fadeSeconds` | `3` | how long strokes stay before fading |
| `excludeOverlayFromCapture` | `true` | keep drawings out of your stream (needs Windows 10 2004+) |
| `autoUpdate` | `true` | install new releases automatically |

Log: `%APPDATA%\TSDraw\tsdraw.log` (tray → *Open log*).

## Good to know

- **Security:** while TeamSpeak runs with the debug port, any program on your PC can
  control the TeamSpeak UI through `127.0.0.1:39222`. The port is never reachable from
  the network. Programs on your PC can already read your TeamSpeak data, so this adds
  little risk, but stop using TSDraw if that bothers you.
- **Your IP address:** like TeamSpeak's own peer-to-peer streams, a direct link shows
  your IP address to the person you're linked with. With a relay, it doesn't.
- **Exclusive-fullscreen games** draw over every other window, so the streamer won't
  see the overlay there. Use borderless/windowed fullscreen.
- **Sharing a single window:** the overlay follows that window. It also draws over
  anything you drag on top of it.
- **TeamSpeak updates** don't touch TSDraw, but a redesign of the stream player could
  break the pen layer until TSDraw is updated.
- The streamer's overlay uses colour-key transparency, because that's the only kind
  Windows lets you hide from screen capture. So strokes there have a dark outline and
  fade by thinning rather than by opacity.

## Building from source

`build.cmd` compiles `dist\TSDraw.exe` with the C# compiler that ships with Windows
(.NET Framework 4.x), so no SDK is needed. Sources:

| File | What |
| --- | --- |
| `src/mod.js` | drawing layer and peer-to-peer link, injected into TeamSpeak's UI (embedded in the exe) |
| `src/Cdp.cs` | finds TeamSpeak's UI pages over DevTools, injects, carries messages |
| `src/Overlay.cs` | streamer overlay, capture exclusion, screen/window tracking, stroke model |
| `src/App.cs` | tray app, TeamSpeak launcher, routing between pages, overlays and the optional relay |
| `src/Updater.cs` | self-update from GitHub releases |
| `VERSION`, `build.cmd`, `release.ps1` | version number, build, publish |
| `src/Relay.cs`, `relay/tsdraw-relay.py` | optional relay: client, wire format, and server |
| `test/` | pen-layer harness (`run-harness.mjs`), 3-client peer-to-peer simulation (`run-p2p.mjs`), fake relay peer (`fake-peer.mjs`) |

In TeamSpeak's DevTools console, `__tsdraw.links()` lists the peer-to-peer links and their states.
