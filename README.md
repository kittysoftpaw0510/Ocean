# Ocean: LAN video sharing

Ocean turns every Windows PC on a local network into a node that can share videos and watch what
other nodes share. No server, no accounts, no cloud: nodes find each other automatically (like IP Messenger),
and every byte between them travels over mutually authenticated, end-to-end encrypted TLS.

- **Share** a folder or a handful of files as a named group ("Family 2024", "Travel"). Sub-folders are kept:
  others browse the share as folders, and downloads recreate the same structure on disk.
- **Organize** a share into folders inside Ocean: create, rename and remove folders, and move videos between them
  (drag and drop, or *Move to…*). This is Ocean's view only; files on your disk are never moved or renamed.
- **Protect** a group, or a single video inside it: public, password, or *ask me each time*.
- **Private circles**: set a shared secret and only PCs with the same secret can see each other.
- **Watch** by streaming (built-in player, or VLC / your default player), or **download** one video,
  a group, or everything, resumable and with folder structure preserved.
- **Thumbnails**, duration and resolution for every video; grid / tiles / list views; sort by newest,
  name, size, duration or folder; search.
- **Offline nodes stay visible**: when a PC goes offline it stays listed (dimmed, "last seen …") with its last known
  shares for 24 h by default; downloads you start wait and continue when it is back.
- **Bridged networks**: when two PCs have no network path to each other but both reach a third one, Ocean passes
  videos through that PC (shown as "via …" in the list). At most one PC in between. When several PCs can bridge
  the same pair, requests spread over the least busy ones and move to another when one fails. A bridge paces what
  it passes on like its own uploads, keeps a copy so the owner sends each part once, and can opt out in Settings.
- **Lighter copies for watching**: when someone has watched one of your heavy videos for half a minute, your PC
  makes a 720p copy at about 1.5 Mbps (with the bundled ffmpeg) and serves that from then on; viewers see
  "Preparing…" meanwhile and can switch, or are switched when playback keeps stalling. The original is never
  touched and downloads always get it. Copies are kept for a number of days after the last viewing, within a disk
  limit; both are set in Settings, where the feature can also be turned off.
- **Light on the PC**: Ocean's service, including making lighter copies, never uses more than 25 % of the processor
  (Windows enforces it; adjustable in Settings). The player in the app is not limited.
- **Player**: speed control (0.25× to 2×) and A-B repeat with markers on the seek bar.
- **New videos are highlighted** (since your last visit to that node) and you get a notification
  when a node adds something.
- Background service + a small tray app. Light on bandwidth: catalogs are fetched only when they
  change, thumbnails are tiny and cached, uploads can be rate-limited.

## Requirements

- Windows 10 (1809+) or Windows 11, x64.
- .NET 8 Desktop Runtime (`winget install Microsoft.DotNet.DesktopRuntime.8` and
  `winget install Microsoft.DotNet.AspNetCore.8`), or build with `-SelfContained` to bundle it.

## Install (end users)

Run **`Ocean-Setup-1.0.7.exe`** and click through. Nothing else is needed: the .NET runtime is bundled.
The setup:

- installs Ocean to `C:\Program Files\Ocean` with a Start menu shortcut (desktop shortcut optional);
- registers and starts the **Ocean LAN Video Sharing** Windows service (starts with Windows, restarts on failure,
  shares even when nobody is signed in; signed-in users may start it, only admins may stop it);
- allows Ocean through Windows Firewall, including on networks Windows marks as Public (Windows labels many home
  and office networks Public; the service keeps that rule in line with **Settings > Network > Allow connections on
  Public networks**);
- optionally opens Ocean in the tray at sign-in;
- upgrades in place (run a newer setup over an older one; settings and shares are kept).

Uninstall from *Settings > Apps* (or the Start menu). It removes the service, firewall rules and shortcuts and
asks whether to also delete settings and caches (`%ProgramData%\Ocean`). Videos and downloads are never touched.

Silent install for many PCs: `Ocean-Setup-1.0.7.exe /VERYSILENT /SUPPRESSMSGBOXES /TASKS="service,firewall,autostart"`.

## Build

```powershell
# .NET 8 SDK required (winget install Microsoft.DotNet.SDK.8)
.\scripts\make-setup.ps1            # -> dist\Ocean-Setup-<version>.exe (one-file installer, runtime bundled)
.\scripts\build.ps1                 # -> dist\Ocean  (~2 MB portable folder, needs the .NET 8 runtimes)
.\scripts\make-icon.ps1             # re-renders assets\ (app icon + installer artwork)
```

`make-setup.ps1` uses Inno Setup 6: an installed copy if present, otherwise it downloads the compiler
from the `Tools.InnoSetup` NuGet package into `.tools\`.

## Run (portable folder)

Start `dist\Ocean\Ocean.exe`. The app starts the background service automatically (as a hidden process)
when no service is running, puts an icon in the tray, and keeps sharing when you close the window.

To share even when nobody is signed in, install the Windows service (as administrator):

```powershell
cd dist\Ocean
.\install-service.ps1     # creates "OceanService", opens firewall ports 38471/tcp and 38472/udp
.\uninstall-service.ps1
```

The first time the service listens, Windows Firewall may ask to allow `Ocean.Service` on private networks:
allow it, or other PCs will not be able to connect. (`install-service.ps1` adds the rules itself.)
When Ocean runs as the Windows service it also allows itself on Public networks, unless you turn that off in Settings.

## Using Ocean

| Where | What |
|---|---|
| **Home** | *Continue watching* shows the last video you played and where you stopped: *Resume* picks up from there (or *Play again* if you watched it to the end). Kept per Windows user. |
| **Network** (left) | Nodes currently online. A green badge counts videos added since you last visited that node. |
| Node page | Share chips at the top filter the videos. Double-click to play, right-click for *Download*, *Open in external player*, *Copy stream link*. Multi-select and *Download n selected*. *Download all* for the chip you're on. |
| **My shares** | *Share a folder* / *Files* / *New folder* (an empty folder you fill later; rename or remove any time), or drop folders anywhere in the window. Pick who can watch: everyone, password, or ask-me. Right-click a video to give it its own rule. *Who has access* lists issued grants (revoke any time). |
| **Downloads** | Progress, speed, ETA; cancel, retry, open, show in folder. Partial files resume after restarts. |
| **Settings** | Your name, shared secret, upload speed cap, how long videos count as "new", download folder, video cache size (what you watched from other PCs is kept so replays cost no network), networking mode (Auto picks a classic, IPmsg-like mode when a VPN or proxy add-on would break the fast one), start with Windows. |
| Tray icon | Open, pause sharing, exit (keeps sharing) or exit and stop sharing. Balloon tips for access requests and new videos. |

Keyboard: `Ctrl+F` search · `Enter` play · `Ctrl+A` select all · in the player `Space` play/pause, `Shift+>` / `Shift+<` speed, `[` / `]` set A / B (A-B repeat), `\` clear repeat,
`←/→` ±10 s, `↑/↓` volume, `F` fullscreen, `M` mute, `Esc` close.

### Access modes

| Mode | Behaviour |
|---|---|
| Public | Everyone on the network (in your secret circle, if set) can list, watch and download. |
| Password | The group's videos are hidden until the viewer enters the password. The password is checked by the owner's PC; a successful check issues a per-viewer token that stays valid until the owner changes the password. |
| Ask me | The viewer clicks *Ask for access*; the owner gets a dialog (and tray balloon) with *Allow once* (8 hours), *Always allow*, *Deny*. |

A video can override its group's rule, e.g. one private clip inside a public folder.

### Shared secret

Leave it empty to see everyone. Set the same secret on a few PCs and they form a private circle: beacons
carry only a hash tag of the secret, and every request carries a proof bound to both node identities,
so nodes with a different (or no) secret cannot even list your shares.

## How it works

See [docs/DESIGN.md](docs/DESIGN.md) for the architecture, protocol and security model. In short:

- `Ocean.Service` (background): UDP discovery beacons (signed), HTTPS peer API with mutual TLS and
  key pinning, catalog/indexer with file watchers, thumbnail/metadata via the Windows Shell, download
  manager, and a loopback API + event stream for the UI.
- `Ocean` (WPF tray app): browse, play (through a local loopback proxy so any player can be used),
  manage shares and access, downloads, settings.
- `Ocean.Core`: shared protocol types, identity/crypto, safe file naming, media helpers.

Data lives in `%ProgramData%\Ocean` (config, node key, catalog index, thumbnails, logs).

### One instance per device

Only one Ocean app and one Ocean service run per PC, across all signed-in users. Opening Ocean again brings
the running window forward; if it is open in another user's session you get a short explanation instead.
A second copy of the service exits immediately (logged in `%ProgramData%\Ocean\logs`).

### Running several nodes on one PC (developers only)

Isolated test nodes need an explicit opt-in plus their own data folder and ports:

```powershell
$env:OCEAN_ALLOW_MULTI = "1"; $env:OCEAN_DATA_DIR = "C:\temp\nodeB"
$env:OCEAN_LOCAL_PORT = 38480; $env:OCEAN_PEER_PORT = 38481
.\dist\Ocean\Ocean.Service.exe --verbose
```

`OCEAN_TEST_DEAF_TO` (comma-separated node id prefixes) makes a node ignore those nodes' beacons, as if they were on
another network: handy for testing the "via" bridge on one PC.

### ffmpeg

The installer ships `ffmpeg.exe` (a gyan.dev release build, GPL; its licence is in `LICENSE-ffmpeg.txt` next to it)
beside Ocean's service. It is used only to make the lighter copies described above. `scripts\get-ffmpeg.ps1`
fetches it once into `.tools\ffmpeg` for the build; a portable build without it simply makes no copies.

## Supported formats

Any container the Windows Shell can inspect is listed and thumbnailed; streaming and downloading
work for every file regardless of codec. The built-in player uses the codecs installed in Windows
(MP4/H.264, WMV, MPEG-1, AVI/MPEG-4 part 2 out of the box, HEVC/AV1 with the Store extensions). For
MKV, AVI with exotic codecs, etc. use *Open in external player*. Ocean finds VLC automatically.

## Notes & limits

- The service, when installed as a Windows service, runs as `LocalSystem`: it can read local drives and UNC paths but not mapped network drives.
- Discovery uses UDP broadcast on port 38472. A PC on another network is reached only through a PC that hears both
  (at most one PC in between); that PC must be running Ocean and carries the traffic twice (in and out), unless it
  switched "Let other PCs reach each other through this PC" off.
- File names are transported as UTF-8 and never placed in URLs; downloads sanitize names (reserved names, invalid characters, >200-char segments, long paths).
