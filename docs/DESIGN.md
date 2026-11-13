# Ocean: design

## Goals

- Zero configuration: nodes on the same LAN find each other and can share within seconds.
- No central component; every PC is equal (IP Messenger model).
- Encrypted and authenticated between nodes, with identities that cannot be spoofed.
- Lightweight: no bundled codecs, no database engine, small memory footprint, bandwidth spent only when needed.
- Friendly: thumbnails, "what's new", one-click sharing, approval dialogs.

## Processes

```
+---------------------------+            +---------------------------+
|  Ocean.exe (WPF, tray)    |  loopback  |  Ocean.Service.exe        |
|  browse / play / manage   |<---------->|  discovery, catalog,      |
|  MediaElement <- proxy    |  HTTP+SSE  |  peer API, downloads      |
+---------------------------+            +-----------+---------------+
                                                     |  HTTPS (mTLS, pinned)
                                                     v
                                          other Ocean nodes on the LAN
```

- The service is a .NET 8 generic host. It runs either as a Windows service (`install-service.ps1`)
  or as a hidden background process started by the app. Behaviour is identical.
- The UI talks only to the service over `127.0.0.1:38470` with a random token stored in
  `%ProgramData%\Ocean\local.token`. Events (peers, catalogs, downloads, access requests) arrive as
  Server-Sent Events so the UI never polls.
- Video playback goes through `GET /api/peers/{peer}/stream/{item}/{name}?k=token` on the loopback
  listener. The service forwards `Range` requests to the owner over TLS and pipes the body back. Because
  this is a plain local HTTP URL, any player works (WPF MediaElement, VLC, a browser).

## Identity and transport security

- On first start each node creates a P-256 key pair wrapped in a self-signed certificate (`node.pfx`).
- **Node id = SHA-256 of the public key.** It is shown as a short fingerprint in Settings.
- Peer API connections are TLS 1.2/1.3 with **mutual** certificates:
  - the client pins the server certificate to the node id announced in discovery;
  - the server identifies the caller by the SHA-256 of the client certificate's key.
  No CA, no trust store, no way to impersonate another PC without its private key. This is end-to-end
  encryption between the two PCs involved; nothing in between can read or alter the stream.
- Discovery beacons are signed with the same key and carry the public key, so a beacon cannot
  announce an id it does not own.

## Local network only (`Net.LocalNetworks.IsLan`, `Net.LanOnly`)

Ocean talks to PCs on this PC's local networks and nowhere else. An address is local when it is loopback, or a
private IPv4 address (10/8, 172.16/12, 192.168/16, 169.254/16) on the subnet of one of this PC's adapters that
is not a VPN. VPN adapters are recognised by type (PPP) or by name/description (Astrill, Wintun, WireGuard,
TAP, OpenVPN, Tailscale, ZeroTier, Hamachi, AnyConnect, GlobalProtect, Fortinet...); the other end of a VPN is
the internet, and many VPN services put their customers on one shared subnet. IPv6 is never local (discovery is
IPv4 only). The rule is applied:

- discovery: beacons are sent only to the broadcast addresses of local networks, and beacons from elsewhere are
  dropped before parsing (so never answered);
- the peer port: a connection from elsewhere is closed before the TLS handshake (Kestrel connection middleware);
- outgoing: every peer request, bridged and reverse ones included, connects through `LanOnly.ConnectAsync`, which
  refuses other addresses before a packet leaves; a peer's candidate addresses are filtered the same way;
- multicast: packets from elsewhere are dropped (they are also sent with TTL 1 on physical adapters only).

The firewall rules (setup, `install-service.ps1`, and the service for the Public rule and the setup's rule) carry
`remoteip=localsubnet` as a second line. Refusals are logged once per address every 10 minutes. The update
check to GitHub is the only internet connection and carries nothing about peers.

## Discovery (UDP 38472)

Presence works the IPmsg way: a node speaks when something happens and is silent otherwise.

- **Hello on events.** At start a node asks who is there (a `hello` with `q`, three times over 15 s: a broadcast
  can be lost); every node answers with a unicast `reply`, so the list is complete within a second. It also says
  hello when its network changes (a question too), when its catalog, shares, name or secret change, when it needs a
  node to connect back (`want`), and on the app's refresh (a question). A hello caused by a change also goes to each
  node it hears, directly, since broadcasts are fragile (Wi-Fi drops them, firewalls block them). At most one
  hello a second. `bye` on shutdown (twice, and to each node directly).
- **Heartbeat.** Otherwise one `hello` every 5 min (`hb` = 300). A node heard directly goes offline after three
  missed heartbeats and a margin (`Peer.Silence`, 15.5 min). Two things notice sooner: any authenticated request a
  node makes counts as a sign of life (`PeerRegistry.Touch`), and when a node can't be connected to
  (`PeerClient.DirectFailed`) it is asked "are you there?" (a unicast question) and taken offline if neither a
  beacon nor a request comes within 5 s. One that only has its firewall in the way answers, and the reverse
  connection takes over.
- **Answers.** To a question, to a newcomer, to a node we ask to connect back (at most once a second each), and to
  a node's hello once per our own heartbeat, so a node our broadcasts don't reach still hears from us before it
  gives us up. An older node gets no such answers (it has our keep-alive, below). A `reply` is never
  answered: until 1.3.7 a node asking another to connect back answered all its beacons, replies included, and two
  such nodes answered each other without end (about 1,200 packets a second, 20 Mbps, on a network of 24 PCs).
- **Older nodes.** Ocean up to 1.3.8 takes a node silent for 16 s as gone and sends no `hb`. Each such node heard
  directly gets a unicast `reply` every 4 s: older Ocean counts any beacon as a sign of life and never answers a
  reply. Everything else stays quiet. Until 1.4.3 one older node put every node on the old rhythm (a broadcast
  every 4 s, answers to every node every 8 s): with 5 older and 23 new PCs, about 900 discovery packets a minute
  per PC (0.1 Mbps); now about 75 sent (the keep-alives) and the older nodes' own broadcasts received. A node is on
  the 4 s rhythm only in its first 20 s, until the startup questions are answered; its peers time it out by what
  it announced (16 s for `hb` = 4, 15.5 min for 300).
- **Signatures.** `sig` covers the fields older nodes know; `xs` covers everything, `hb`, `nrev` and `q` included.
  Without a valid `xs` the newer fields are dropped and the beacon counts as an older node's.
- **Bridge lists** (`/p/neighbors`, below) are asked from a quiet node when it comes online and when the `nrev` in
  its beacons (a fingerprint of the nodes it hears) changes: at once when we reach someone through it, else at most
  every 5 min; each beacon with the same fingerprint confirms the routes through it (a route lasts three of the
  bridge's heartbeats). An older node is asked every 30 s and its routes last 75 s.
- **Reverse links** ask the other side to hold each poll up to 4 min (`X-Ocean-Hold`; an older node holds 25 s).

Idle, with every node quiet: a heartbeat and one answer per node every 5 min, about 70 small packets per PC per 5
minutes on a network of 24 PCs (0.002 Mbps); measured on two nodes with the heartbeat shortened to 10 s: 8 packets
each in 40 s, no TCP at all over an idle reverse link. Beacons go to the directed broadcast address of each local
network (see "Local network only"; never `255.255.255.255`, which Windows sends where the default route points,
i.e. into a connected VPN). The beacon carries the catalog version and share count, so browsing nodes refetch a
catalog only when it changed. Test switches: `OCEAN_TEST_HEARTBEAT` (seconds), `OCEAN_TEST_UNICAST_AS_BROADCAST`.

Beacon: `{v, t, id, pk, name, user, host, port, realm, cat, shares, ts, want, sig, hb, nrev, q, xs}`.

### Reaching a PC through another one ("via", one bridge at most)

When A and C have no network path to each other but both reach B, B bridges them:

- Each node asks the nodes it hears directly for `GET /p/neighbors` (when, see Discovery): the nodes they hear directly
  (never nodes they reach through someone else, so there is never more than one PC in between), plus how many
  transfers they are passing on right now. The answer carries a revision; with `X-Ocean-Neighbors-Rev` the node
  answers 304 and only its load when nothing changed. A node not heard directly is listed as reached through
  every node that offers it (`Peer.Bridges`; `PeerDto.via` names the one in use, `viaCount` how many there are).
  A direct beacon always wins: a node heard within its silence limit is never bridged.
- A sends C's request to a bridge as `/p/via/{C's id}/p/…` (catalog, stream, thumb, access, ping only). The least
  busy bridge is chosen (its reported load plus what A itself started lately); a video keeps its bridge while it
  plays (sticky per path for 2 min), so seeking stays on one connection. A bridge that fails (connect error, or
  an answer marked `X-Ocean-Via-Error`: off / busy / unreachable / loop / failed) is skipped for 15 s to 10 min
  and the request goes to the next bridge at once; when all fail, the last answer is returned (a "busy" one keeps
  a download waiting as it would on the owner). A request to a bridge waits for a reverse connection only when
  that link is already up, so a dead bridge fails fast.
- A signs each such request with its node key (`X-Ocean-Origin`, `-Key`, `-Ts`, `-Sig` over origin, target,
  method, path and time; ±1 day clock tolerance). The bridge checks the signature, its own proof of the secret is
  checked by C, and C serves the request as A's: A's grants, A's download slots, A's name on access requests.
  A relay can therefore carry a request but never invent one in another node's name.
- A bridge refuses a request that already passed a bridge (`X-Ocean-Hops`), never relays to a node it reaches
  only through another one, and refuses everything when "Let other PCs reach each other through this PC" is off
  (its neighbour list is then empty, so nobody routes through it).
- What a bridge passes on goes through its own `UploadScheduler`: viewers before downloads, the upload limit
  from Settings, and a download slot per relayed download (503 busy beyond that, so the origin tries another
  bridge). A public video of the target is served from the bridge's own copy or its stream cache
  (`PeerMedia.ServeForBridgeAsync`), so one viewer's bytes serve the next and a seek back costs the owner
  nothing; a locked video goes to the owner with the origin's signature so the owner decides.
- B sees what it carries (as every node with the secret is trusted to); the video passes B twice (in and out).

### Network add-ons (Winsock LSPs) and the classic networking mode

Some VPN, proxy, "network accelerator" and security programs install a Winsock Layered Service Provider
(e.g. Astrill's ASProxy). Some of them complete overlapped I/O a second time, which .NET (I/O completion
ports) cannot survive: the process dies with "'overlapped' has already been freed" within a second of the
first connection. Native programs like IPmsg use plain blocking sockets and are unaffected. Ocean therefore
has two networking modes (`NetworkCompat`):

- **Fast**: .NET's normal asynchronous sockets (Kestrel's socket transport, SocketsHttpHandler).
- **Classic**: only blocking socket calls on dedicated threads, never overlapped I/O. `ClassicSocketStream`
  (a receive thread and a send thread with bounded queues, so TCP back-pressure still works) serves both
  Kestrel (`ClassicTransportFactory`: blocking accept thread + `PipeReader/Writer.Create` over the stream)
  and `SocketsHttpHandler.ConnectCallback` (service and app). TLS and HTTP above it are unchanged.
  Discovery already used blocking UDP calls.
- **Auto** (default) picks Classic when any layered provider is in the Winsock catalog (`WinsockCatalog`)
  or when `classic-network.flag` exists in the data folder: the service writes it when it crashes with the
  signature above, so a PC heals itself on the next start even for add-ons that aren't detected.
  Settings › Networking (or `networkMode` in config.json) forces a mode; `OCEAN_NET_MODE=classic|fast`
  overrides everything (developers). "Restart the service now" exits with code 3; the installer sets
  `sc failureflag 1` (and the service sets it at startup) so the service manager restarts it.

### Loopback and echo cases

- **Own beacons.** Broadcasts are delivered back to the sender (and once per adapter). A node drops any beacon
  carrying its own node id; since ids are signed key fingerprints this cannot be confused with another PC.
- **Multi-homed peers.** A PC with Ethernet + Wi-Fi, a VPN/TUN adapter or Hyper-V switches is heard on
  several addresses, some of which may not be reachable from here. Each peer keeps every address heard in the
  last 60 s. Requests go to the last address that worked, then RFC 1918 LAN addresses, then the rest, with a
  4 s connect timeout per attempt; the first that connects (and passes key pinning) becomes preferred.
- **Re-shared videos.** If B downloads from A and shares its downloads folder, A would see its own videos as
  "new" on B. The browsing side marks items whose size + normalized name match one of its own shared videos as
  *Yours*; they never count as new and never trigger notifications.
- **Unlocking is not adding.** Videos that become visible because a share was unlocked do not raise
  "new videos" notifications.
- **One instance per device.** `Global\Ocean.App` / `Global\Ocean.Service` mutexes; the UI loopback API and the
  peer port are therefore never contested on a normal install.

## Shared secret ("realm")

- `realm` in the beacon = first 16 hex of `HMAC-SHA256(secret, "ocean:realm:v1")`. Nodes drop beacons
  whose tag differs from their own (empty secret ⇒ empty tag).
- Every peer request carries `X-Ocean-Realm = HMAC-SHA256(secret, "ocean:proof:v1:<clientId>:<serverId>")`.
  The server recomputes it. Binding both ids makes a captured proof useless against any other node.
- The secret itself is never transmitted.

## Catalog

- A **share** = name + access mode + one or more roots (folders or files) + optional per-item overrides.
- Items get a stable id = SHA-256(shareId | rootIndex | relative path) so ids survive rescans; URLs
  contain only ids, never file names.
- `AddedAt` is the first time the indexer saw the file (first scan: file creation time, so an existing
  library does not light up as "new"). The browsing side flags an item **new** when
  `AddedAt > max(lastVisit, now − NewDays)`.
- A `FileSystemWatcher` per root triggers a debounced rescan; a full rescan also runs every 15 min.
- Duration/resolution come from Shell properties (`System.Media.Duration`, `System.Video.FrameWidth`),
  thumbnails from `IShellItemImageFactory`, the same providers Explorer uses, run on a dedicated STA
  thread, pre-generated in the background and cached as 320×180 JPEGs keyed by item id + mtime.
- Catalog version is a persisted counter bumped on any change. Responses carry an ETag that depends on
  the version *and* on which locks the caller has open.
- **Sync costs only what changed.** A node sends back the ETag of the catalog it holds: the owner answers
  `304` when nothing changed, or a **delta** (`deltaFrom` + complete share headers + only the items whose
  fingerprint changed + `removedItems`). The owner keeps an in-memory change log (last 512 versions, item
  fingerprints = name, folder, size, dates, media info, lock); a version from before its last restart, a
  different view (locks opened), or a delta bigger than half the catalog gets the full catalog instead.
  JSON between nodes is Brotli/gzip compressed, also over the reverse connection. Catalogs are still
  fetched in the background for every node (the "new" badges need them), but that now costs a few bytes.

## Folders

Every item has a `Folder` ("a/b", "" = share root) and every share a `Folders` list (all folders, including
empty ones). The default folder is the item's directory on disk relative to the shared folder; when a share
has several shared folders, each becomes a top-level folder; individually shared files sit at the root.

The owner can reorganize without touching the disk: `ItemFolders` (item id → folder, stored only where it
differs from the disk folder) and `Folders` (explicitly created/kept folders) live in the share definition.
Operations (`/api/shares/{id}/move`, `/folders`, `/folders/rename`, `/folders/delete`) bump the catalog
version so browsing nodes refresh. Removing a folder moves its contents up one level; a folder cannot be
moved into itself. Downloads use `<node>\<share>\<folder…>\<file>` and skip files that already exist with the
same size, so downloading a folder twice does not create "name (2)" copies.

In the UI a share chip opens folder navigation (breadcrumb, Up/Backspace, folder tiles with a cover image
and "new" count); "All", search and "New only" show flat lists across folders.

## Access control

| Lock id | Meaning |
|---|---|
| `s:<shareId>` | the share; gates listing of its items |
| `i:<itemId>` | a single video with its own rule; gates thumbnail/stream/download |

Grants are server-side records `{lockId, clientId, token, expiresAt?}` (`grants.json`). Clients present
tokens in `X-Ocean-Grants: lock=token;…`. Revocation is immediate; changing a share's mode or password
revokes its grants. "Ask" requests are kept in memory for 3 min; the owner's UI receives them as events,
and the requester polls `GET /p/access/{requestId}` every 2 s.

## Peer API (HTTPS 38471, mutual TLS)

| Method | Path | Notes |
|---|---|---|
| GET | `/p/ping` | identity + catalog version |
| GET | `/p/catalog` | per-caller view; `If-None-Match` supported |
| POST | `/p/access` | `{lockId, password?}` → granted / wrong_password / pending / denied |
| GET | `/p/access/{id}` | poll a pending request |
| GET | `/p/thumb/{itemId}` | JPEG; 403 `{error:"locked", lock}` when not authorized |
| GET/HEAD | `/p/stream/{itemId}` | `Accept-Ranges: bytes`; rate-limited through a global token bucket when an upload cap is set; `{itemId}@light` = the lighter copy |
| GET/POST | `/p/light/{itemId}` | state of the lighter copy; POST asks for one ("I'm watching this") |
| POST | `/p/cast/{id}/join`, `/beat`, `/leave`; GET `/state` | multicast: join (key + group), heartbeat with stats (returns the host's state), leave |
| POST | `/p/screen/ask`, `/answer`, `/ready`, `/cancel` | screen sharing: "show me your screen" / "may I show you mine?" / "look at my screen", the answer, "I share now: watch here", taking a question back |
| POST | `/p/screen/{id}/join`, `/beat`, `/leave`; GET `/stream?from=` | screen sharing: join (key, size, where packets go), report in, leave; the stream itself for whatever UDP didn't bring |
| GET | `/p/neighbors` | nodes this node hears directly, its load and a revision (304 when unchanged); empty when bridging is off |
| GET/HEAD/POST | `/p/via/{nodeId}/p/…` | carry a signed request on to a node the caller can't reach |

## Local API (HTTP 127.0.0.1:38470)

Status, event stream, peers and their catalogs, unlocking, thumbnail/stream proxies, share CRUD and
per-item policies and tags, votes, top list, search, pending requests and grants, downloads, settings, updates,
node info (`GET /api/peers/{id}/info`). A node's Ocean version is taken from the `Ocean/x.y.z` user agent of its
own requests (every version sends it; each PC on a bridged path builds its own request, so it is always the
connecting PC's) and from `CatalogDto.AppVersion`, kept in `peer-state` so it survives while the node is offline.
Newer catalogs also carry `Os` (Windows edition and build).
See `Api/LocalApi.cs`.

## Lighter copies for watching (`Ocean.Service.Media.LightCopies`)

- A viewer that has watched another node's video for 30 s (`PlayerViewModel`) posts `/p/light/{itemId}` to the
  owner (through the local `/api/peers/{peer}/light/{item}`). The owner queues the video unless its copy is
  already ready, failed or skipped for this version of the file (key = modified time + size), bridging is off,
  or ffmpeg is missing. `GET` returns the state: `none | queued | preparing (+percent) | ready (+size) | off`.
  Only a player showing the banner polls (every 3 s); nothing is pushed to everyone.
- One conversion at a time, `ffmpeg` at below-normal priority: `scale=-2:min(720,ih)`, libx264 veryfast,
  CRF 25 capped at 1.8 Mbps, AAC 96 kbps, `+faststart`. Progress comes from `-progress pipe:1`. Sources under
  2 Mbps that are not above 720p are skipped, and a copy that is not at least 40 % smaller is dropped.
- Copies live in `cache\light\{itemId}-{key}.mp4` with `index.json`. A sweep (at start and every 30 min) removes
  copies whose video changed or left the share, copies not watched for `LightKeepDays`, and the least recently
  watched beyond `LightCacheGB`. Serving or requesting a copy counts as watching it.
- The catalog carries `ItemDto.Light` ("preparing" / "ready") and `LightSize`, included in the change-log
  fingerprint and bumped through the coalesced `BumpSoon` (one version per 8 s at most).
- Viewers ask for `/p/stream/{itemId}@light`; the owner serves the copy under the original's locks. On the viewing
  node, `PeerService.FindItem` turns `{id}@light` into an item of its own (own size, `.mp4` name), so the stream
  cache, downloads-from-cache and bridges treat it like any other video. Downloads use the original id.
- Player: the original plays while the copy is prepared ("Preparing… 35 %"); when ready, Switch resumes at the
  same position with the lighter URL, and two stalls after the first 10 s switch automatically. "Watch original"
  switches back and is remembered for the session.

## Multicast (`Ocean.Service.Cast`)

- **Viewer counts.** `ViewerTracker` holds one entry per open `/p/stream` response (item → client → count);
  `LocalItemDto.Viewers` and the `viewers` event give the owner's app "N watching" live (debounced 500 ms).
- **Hosting.** `CastHost` keeps one session per node: a random 32-byte key, the video (the lighter copy when one
  is ready), the host's position (taken at a time stamp, so it extrapolates while playing), viewers (by heartbeat,
  15 s timeout) and a sender thread. `CatalogDto.Cast` / `PeerDto.Cast` carry it to everyone (`Bump("cast")` on
  start and stop, so the "LIVE" marks appear at once); `peerCast` events tell the app. Starting is refused while
  `active ≥ max(1, floor(0.3 × online nodes incl. me))`, with the reason in `MyCastDto.Error`.
- **Packets** (`CastWire`): `OCST` + session id + type + sequence, then an AES-GCM-sealed payload (nonce = type +
  sequence): data = offset + bytes (1200), control = position/playing/host time/viewers/ended every 500 ms. Every
  8 data packets a parity packet (XOR of their sealed payloads) recovers one loss per group; the receiver judges a
  group 32 packets after its parity, since copies sent on several adapters arrive in any order and are deduplicated
  by sequence. Sent to 239.255.77.1:38473 on every real IPv4 adapter, TTL 1, loopback on.
- **Pacing.** The sender streams the file in byte order from the host's position: 4 × the bitrate while within 5 s
  of the position (start, seek), 1.5 × beyond, never more than 20 s ahead. A seek moves the cursor to the position
  minus 1 s.
- **Viewing.** `CastReceiver` joins through `POST /p/cast/{id}/join` (the video's own locks apply; the key only
  travels on the TLS channel), opens a `StreamCache.ExternalFeed` for the stream item (registered with
  `PeerService.RegisterCastItem`, so the player's usual `/api/peers/{host}/stream/{id}` reads it from the cache),
  and writes packets into the sparse file; a block counts as cached when all its bytes arrived. `CopyAsync` waits
  up to 6 s for a block the feed is about to deliver (between HighWater − 1 MB and HighWater + 3 MB) and fetches
  only one block at a time otherwise, so what the multicast brings is never fetched twice and what it doesn't
  (lost, far ahead, or nothing at all on this network) comes directly. Heartbeats every 5 s carry stats and bring
  the host's state back when no control packet arrives (`MulticastWorking` false → "direct stream").
- **Unattended.** `POST /api/cast/detach` (the host closed the player and chose "Keep it running", opened another
  video, hid the window, or the app restarted with a session left over) marks the session unattended: a pause is
  lifted and it plays on by its own clock; the sender ends it 10 s after the video is over (`Duration`, or the
  whole file sent when the duration is unknown). The next state report from the player takes it back
  (`MyCastDto.Unattended` false); the app opens the player at the session's current position for that
  (`ReopenCastAsync`). Ending always checks the session is still the current one, so an old sender thread never
  stops a newer session.
- **Network changes.** When the PC's networks change (Wi-Fi reconnecting, a new address, a VPN switched on or off)
  the host opens its send sockets again on the adapters there are now (and every 10 s while there is none), and a
  viewer drops and joins the group again on the adapters the host is reached through; a viewer whose heartbeats are
  answered but who has had no packet for 20 s joins again too (at most once a minute).
- **Service restarts under a viewer.** The app's player keeps the followed session (`FollowPeerId`/`FollowSessionId`);
  when the app reconnects to a service that no longer receives it, it joins again if the host still sends it, else it
  says the multicast ended. Events about another session (a late "left the previous one" after joining a new one) are
  ignored by the follower. Every leave is logged with its reason (Leave, player closed, another video, tray).
- **Pace.** Speed and A-B repeat are off for host and viewers during a multicast (the session carries a position, not
  a speed); starting or joining one sets the speed back to normal and clears the repeat.
- **Player.** Host: the view posts its position every second and at once on play/pause/seek. Follower: the view
  keeps the player at the host's position carried forward on this PC's clock, 1.5 s behind, seeking when more than
  2.5 s off and pausing with the host; controls are locked ("The host controls playback"); Leave or the host's end
  unlocks them and the cached bytes stay, so the video can go on as a normal stream.

## Screen sharing (`Ocean.Service.Screen`, app: `ScreenViewModel`, `ScreenEncoder`, `ScreenDecoder`)

A node shares one thing at a time: a monitor, one window or a part of a monitor; with everyone or with invited PCs.
View only (nothing typed or clicked at the viewer reaches the host), picture only (no sound).

- **Who does what.** The service cannot see the desktop (session 0), so the app captures and encodes and the
  service carries. The app runs ffmpeg (shipped for the lighter copies): `gfxcapture` (Windows.Graphics.Capture:
  a monitor or a window by handle, cropped for a region, scaled on the graphics card to at most 1920 x 1080) →
  H.264 → HTTP POST straight to `POST /api/screen/feed/{session}`. Encoders in order: `h264_nvenc`, `h264_qsv`,
  `h264_amf`, `libx264`; one that fails within 4 s is not tried again in that run of the app. Key picture every
  4 s with its parameters (`dump_extra`), no B pictures. 15 pictures a second (30 with "Smooth motion"); at most
  3 Mbps (5) for full HD, less for smaller pictures; a still desktop takes a fraction (measured: about 0.7 Mbps
  for an ordinary full-HD desktop, a third to a half of one CPU core).
- **Nobody watches, nothing runs.** `MyScreenDto.Wanted` is true while somebody watches, is joining or has just
  accepted; the app starts and stops ffmpeg by it, so a share with everyone costs nothing until someone opens it.
- **Never without the app.** ffmpeg is in a job object that ends with the app (also a crash). The service ends a
  session after 8 s without any app connected (`EventHub.SubscriberCount`), and a session for invited PCs once no
  invited PC watches, is coming or may still answer. An app that starts and finds a session it has no capture for
  stops it.
- **The stream** is a byte stream with offsets counted from the session's first byte. `ScreenRing` keeps the last
  8 MB and notes where key pictures start (an SPS unit after a start code): a viewer always starts at the latest
  one, from a run of the encoder that is still going (`MinStart`).
- **Packets** (`ScreenHost.SendData`): the multicast's packet format (`CastWire`: sealed with AES-GCM under the
  session key, one parity packet per 8 and at the end of every picture) on port 38474: to the multicast group
  (239.255.77.1) when everyone may watch, straight to each viewer's own UDP port otherwise (only when the join came
  straight from that PC, not through a bridge or a reverse link). A control packet twice a second: head offset,
  viewers, ended.
- **Nothing is ever missing** (`ScreenViewer`): UDP only gives the low delay. The viewer puts the stream together
  in order from two sources: the packets, and `GET /p/screen/{id}/stream?from=` over the peer connection. The
  direct stream always brings the start (`from=-1`: the latest key picture, waiting for one while the encoder
  starts), then only what is missing for a moment: a hole after 120 ms, a missing end after 300 ms (the control
  packet says where the head is), everything after 2 s without packets. It is dropped again as soon as packets
  keep up (fresh, none lost for 2 s); after four switches in 20 s it stays for 30 s. Through bridges, reverse links
  and where multicast doesn't arrive, everything comes that way. Measured between two services on one PC: 147 ms
  median from "picture handed to the encoder" to "picture decoded at the viewer" at 15 pictures a second (two
  picture intervals); with 3 % of the packets thrown away the same, worst case 224 ms; with 10 % and with all
  packets thrown away every picture still decodes without an error.
- **Asking** (`ScreenAsks`). "Show me your screen" (`kind: show`): the asked PC's user allows or declines; on allow
  they pick what to share (or the asker is let into what they share already), and the answer names the session.
  "May I show you my screen?" (`kind: offer`, the mirror): watch or decline; on yes the asker's user picks what to
  share, and `POST /p/screen/ready {askId, sessionId}` tells the other PC where to watch (or `/cancel` when nothing
  was picked; either side gives up after 2 min). "Look at my screen" (`kind: watch`, sent by the host for every PC
  invited into a running session): watch or decline. A question needs
  the app (`away` without one), waits 45 s (2 min while the user picks), one per PC and kind, not again within 10 s
  of a decline, and a PC can be muted (`screen-muted.json`). Answers the asker may get: allowed, declined, timeout,
  away, busy, muted, old (a 404: an Ocean without screen sharing).
- **With everyone:** the session is in the catalog (`CatalogDto.Screen`, also in partial updates) and `PeerDto.Screen`;
  `peerScreen` tells the apps (LIVE mark, Home card, toast). It counts with the multicasts towards "at most 30 % of
  the PCs stream at once". A node that shares no videos is asked for its list once per version for this (and for
  its Windows and Ocean version), where before it was never asked.
- **Showing** (`ScreenDecoder`): ffmpeg reads `GET /api/screen/view/stream` (the stream put together by the service),
  decodes and writes raw pictures to a pipe; the newest is drawn into a `WriteableBitmap`. No analysing before the
  first picture (`-probesize 32 -analyzeduration 0`; not `-fflags nobuffer`, which holds everything back until the
  second key picture): the first picture is there about a second after opening.
- **Seen by the user:** a red bar "Sharing ... with ..., N watching, Stop sharing" on top of everything and on every
  virtual desktop while a session runs; it is excluded from capture (`WDA_EXCLUDEFROMCAPTURE`) and can't be closed.
- Local API: `/api/screen` (GET, `/start`, `/invite`, `/stop`, `/feed/{id}`), `/api/screen/ask/{peer}`,
  `/api/screen/prompts[/{id}]`, `/api/screen/muted`, `/api/screen/view` (GET, `/leave`, `/stream`),
  `/api/peers/{peer}/screen/{id}/join`. Events: `myScreen`, `screenPrompt`, `screenRequest`, `screenInvite`,
  `screenView`, `peerScreen`.

## Screen recording (app: `ScreenRecorder`, `RecordingAudio`, `RecordViewModel`)

Entirely in the app (the service isn't involved). Settings per Windows user in `%LocalAppData%\Ocean\recording.json`
(quality, speaker, microphone, folder; the folder defaults to the user's Videos folder).

- **Segments and pause.** A recording is one or more segments: Pause ends the running one (its file is finished
  in the background), going on starts the next (picture and sound as before; the microphone's mute carries over).
  At the end each segment is cut at its own end (`-t`, last picture held to it with `setts`) and they are joined
  with the concat demuxer, stream copy, into the MP4; the segment files go once that worked (otherwise they stay,
  each playable). Measured: a 4.6 s pause is gone from the timeline, a mark made while paused isn't in the file, and
  sound and picture stay together on both sides of it.
- **The boundary** (`Views.RecordBoundary`). Four thin click-through strips (top, bottom, left, right), each its own
  layered window, kept out of every capture (`WDA_EXCLUDEFROMCAPTURE`; measured: none of its red in the recording):
  a fine line with rounded corner accents and a soft glow breathing every 1.6 s (red), finely dashed and still while
  paused (amber), with a small REC / PAUSED tag. Strips rather than one see-through window over the whole area:
  Windows redraws layered windows in software, the whole area at every step of the pulse. A part of a screen and
  a window get the line 3 px outside them, a whole screen along its edges (only where capture exclusion works:
  Windows 10 2004 and later). A recorded window is followed every 80 ms; the boundary hides while it is minimized
  or on another desktop.
- **Before and after.** A 3-2-1 countdown over the recorded area (`RecordCountdownWindow`, kept out of capture;
  Esc or a click cancels; off in the settings). Less than 2 GB free: a warning first, with how many minutes that is
  at the chosen quality. A reminder at every full hour. *Discard* (asks first; the question is kept out of the
  recording) deletes every segment. "Record the same again" (`LastTarget` in recording.json): a screen is found
  again by its position and size, a part of a screen by its screen, a window only while it is open (else the picker).
  Saved: `RecordSavedWindow` (bottom right, 30 s): Open, Add to my shares (an existing share, or a new "My
  recordings" for everyone or for approved people; or "Shared in X" when its folder is shared already), Trim…, Show
  in folder, Copy path.
- **Trim** (`RecordingTrim`, `TrimWindow`). Only the end: stream copy, instant and lossless; it is cut in decode
  order (every packet from the first picture shown after the end on is dropped, `noise=drop=gte(n,N)`), never by
  time, which could keep a B-picture without the later picture it is built on. A new start: encoded again (x264
  CRF 23, a little above Balanced, so a second generation stays sharp; about 50x real time for screen content). A
  new "(trimmed)" file; the original stays.
- **The area marker** (`RegionSelectWindow`): drag (edges snap within 10 px to window and screen edges; Shift keeps
  16:9), or click a window for exactly its area, or pick 1280 × 720 / 1920 × 1080 / 1080 × 1080 and click where
  the frame goes.
- **Folders.** Recordings: `Videos\Ocean\Recordings` of the Windows user. Downloads: the service runs as the
  system account, whose Videos is the Public one, so its default is `C:\Users\Public\Videos\Ocean\Downloads`;
  the app moves that (and the older `...\Videos\Ocean`) to its user's `Videos\Ocean\Downloads` when it connects.
  A folder someone chose is never changed.
- **Two ffmpeg processes.** The capture (`gfxcapture` → download → colour conversion → raw pictures in NUT) writes
  straight into the encoder's input (`ChildProcess`: the pipe handle is given to it with an explicit inherit list, so
  no other handle of the app leaks into it and nothing passes through the app). The capture waits for the screen to
  change, also before it can stop: with one process, stopping a still window waited forever and the encoder kept the
  last pictures. Now Stop ends the capture at once; the encoder sees the end of its input, writes what it holds and
  closes the file (ready about 0.4 s after Stop). Measured on a test window changing every frame: the two processes
  together cost the same processor time as one ffmpeg doing the same (about 25 ms per 1280x720 picture).
- **Pictures.** Only changed pictures (`-fps_mode vfr`, at most 15/30/60 a second), a key picture at least every
  10 s, H.264 with B-pictures (16–25 % smaller than without, measured), MP4 + AAC: plays everywhere, Ocean included.
  Small file and Balanced: x264 `veryfast`, CRF 30/27 (the smallest files: on real screen recordings Balanced is
  about a sixth of Windows' own recorder at SSIM 0.993); High: the graphics card's encoder if one passes a test
  encode with the exact options (`RecordingEncoders`, once per run of the app), else x264. HEVC would be about 40 %
  smaller again but needs a paid extension on many PCs (and in Ocean's player), so it isn't used. Colour conversion
  stays on the processor (this ffmpeg can't create the textures for `scale_d3d11` here); at 60 fps the fast
  converter (12 instead of 14 ms per full-HD picture).
- **Sound** (`RecordingAudio`). WASAPI loopback of the default output and the default microphone, read every 5 ms.
  Every block carries Windows' capture time stamp (QPC) and is put on the timeline at that moment; time 0 is the
  first picture (reported by `showinfo` on the capture's first frame, minus 12 ms for the report's own delay, plus
  half a picture's time: a change on screen shows in the next picture, on average half an interval later). The mix
  goes to the encoder 250 ms behind real time, so late blocks still land in place. Jitter of the stamps within 15 ms
  is smoothed (blocks stay continuous); larger gaps stay gaps; a device that goes away is opened again (the new
  default) within a second. Soft limiter on the sum. The microphone can be muted while recording.
  Measured with flashes and beeps (sound vs picture): High within ±15 ms, Balanced within ±30 ms, Small file within
  ±55 ms (at 15 pictures a second the picture itself is up to 67 ms late); no drift over 2 and 3 minutes; before the
  time stamps, the sound was 150–200 ms late (Windows hands sound over in chunks).
- **The file.** Matroska while recording (playable after a crash or a power cut), then MP4 with the index at the
  front (stream copy, under a second). It ends exactly at Stop (`-t`), and the last picture lasts until then (`setts`
  on the last packet by decode time): a still screen would otherwise end the video early. Stops by itself when a
  recorded window closes or the disk has less than 500 MB free; quitting Ocean finishes the file first. A sound
  device that can't be opened is reported, and the recording goes on without it.
- **UI.** *Record my screen* (sidebar) → the picker in recording mode (quality, speaker, microphone, where it is
  saved) → the red recording bar (time, size, sound, mute the microphone, Stop), on top, on every desktop, excluded
  from capture → a toast that opens the folder with the file selected.

## Votes, tags and "Top on the network" (`Ocean.Service.Social`)

Tags belong to the owner (`ShareDefinition.ItemTags`, at most 8 per video, 24 characters each) and travel inside
the catalog like any other item field. Votes are counted by the owner's PC: `POST /p/rate/{item}` carries
`{vote: 1|-1|0, finished}` from a viewer that passed the video's access check; `votes.json` keeps one entry per
voter id (a second click replaces, 0 takes back) with the time of each vote. Totals go out in the catalog
(`Up`, `Down`, `Finished`, `Score`); who voted never leaves the owner's PC. The voter's PC keeps its own votes in
`my-votes.json` (annotated into cached catalogs as `MyVote`) and marks them pending while the owner is offline;
they are delivered when the node comes back (`DeliverPendingVotesAsync` on Added/Online). The owner's answer decides
what happens to a vote (`MyRatingDto.Reason`): counted; `offline`/`unreachable` (kept); `old`, a 404 from an Ocean
before 1.3.2, told apart from a removed video by the node's known version or, unknown, by the video still being in
its list (kept; sent when the node's version changes to one with votes); `locked`, a 403 (kept; sent when a new
approval is stored); `gone` / `rejected` (dropped). When a node turns out to run an Ocean without votes, every vote
for it goes back to waiting (`MyVotes.Requeue`), since none can have been counted. The owner writes `votes.json` 2 s
after the last vote, and at once when the service stops.

`Score = sum(vote * 0.5^(age / 30 days)) + 0.25 * sum(finished * 0.5^(age / 30 days))`: a like is worth 1, a
dislike -1, watching to the end a quarter, all fading with a 30-day half-life so the top list follows what people
watch now. `TopList` ranks every known catalog plus my own (copies of my videos shared back by others are skipped)
and offers tag counts: `GET /api/top?tag=&limit=`.

## Notes (`Ocean.Service.Social.Notes`)

Anyone who can open a video can leave notes on it and vote each note 👍/👎. A video's notes live on its owner's PC
(`notes.json`): no broadcast, nothing kept elsewhere, so a big network costs nothing until someone opens them. Only
the number travels, as `ItemDto.NoteCount` in the catalog (a change bumps the catalog like a vote, coalesced).

- Peer API, all under the video's own access rule (a locked video's notes are locked too): `GET /p/notes/{item}`,
  `POST /p/notes/{item}` `{text, atSeconds?}`, `POST /p/notes/{item}/{note}/edit|delete|vote`. Writes are POSTs only,
  so they pass bridges and reverse links. The writer is the calling PC: node id and name from its certificate and
  announcements, never from the request. A writer edits and deletes their notes; the owner deletes any.
- Limits (`NoteLimits`): 1000 characters, one note per writer and video every 5 s, 500 notes per video (beyond, the
  oldest of the least voted go). No voting on one's own note. Line breaks kept, control characters dropped.
- `atSeconds`: the moment the note is about; a note started in the player takes the moment writing began (the
  writer can drop it or take the current one). Clicking it plays from there.
- This PC's service (`PeerService.NotesAsync`) forwards the app's requests (`/api/notes/{peer|me}/{item}...`) and
  keeps the last notes read per video: while the owner is offline (or unreachable) they are shown, marked so.
  Problems for the app: `offline`, `unreachable`, `old` (a 404 without a body: an Ocean without notes), `locked`,
  `gone` (a 404 with `{error: "gone"}`).
- The owner's app gets `noteAdded` for a note on one of its videos (a toast; the tray when hidden).
- App: a panel beside the player (N, the 💬 button), or a window from a video's menu (one per video). Sorted Top
  (votes, a fresh note lifted a little), Newest or By moment; 30 at a time; long notes folded; read again every 20 s
  while shown.

## Search (`Ocean.Service.Social.NetSearch`)

`GET /api/search?q=` matches every cached catalog (online or not) and my own shares inside the service, so the
app never pulls lists to search them. Text is folded (lower case, accents stripped, punctuation to spaces, the
extension kept as a word), every term must match (AND), `#term` only tags, `@term` only the owner. Scoring per
term: whole name 12, a name word 8, a word prefix 6, inside the name 4, a tag 5, folder/share 2, owner 1; ties
by online then newest. The result carries per-node counts for the chips and the best 200 rows.

## Updates (`Ocean.Service.Update.Updater`)

Three modes (`ServiceConfig.UpdateMode`; settings from before it map `CheckUpdates` on/off to auto-check/manual):
`auto-update` downloads a newer version by itself, `auto-check` (default) only announces it (`POST /api/update/download`
fetches it when the user clicks Update now; the app then runs the installer by itself), `manual` never looks by itself.
Every start looks at once: the service starting, and the app starting (`POST /api/update/startup`) unless a check
succeeded in the last 10 min; while there is no connection it tries again after 10 s, 1 min and 10 min. A check or a
background download that fails stays silent (logged only, nothing in the status); only "Check for updates" reports
it. After that, checks are due 4 h after the last one, 30 min after a failed one, by the wall clock: `update-state.json` keeps the time
and the newest release seen, the service looks every 5 min whether a check is due, so sleep and restarts neither skip
nor repeat checks, and a known newer version is announced right after a restart. The service asks
`api.github.com/repos/kittysoftpaw0510/Ocean/releases/latest`; when the API refuses (60 requests an hour per internet
address, shared by every PC behind one router) it reads `github.com/.../releases/latest`, which redirects to the tag,
and takes the installer from its fixed address under `releases/download/vX.Y.Z/` (size from the download's headers).
Every check is one line in the log. A newer `vX.Y.Z` with `Ocean-Setup-X.Y.Z.exe` is downloaded to `<data>\updates\`
(`.part` then renamed; size must match, SHA-256 checked when the release has a `.sha256` asset; older files
removed). The status (`GET /api/update`, `POST /api/update/check`, event `update`) drives the home-page banner
and *Settings > About*. The app runs the installer itself (`/SILENT /NORESTART /SUPPRESSMSGBOXES`, Windows asks
for elevation); the installer stops the service and apps, installs and relaunches Ocean as the signed-in user
(`postinstall runasoriginaluser`, no longer `skipifsilent`). `OCEAN_UPDATE_FROM=1.0.0` makes a dev node pretend
to be older. No internet: the check fails quietly (debug log) and is retried next time.

## CPU limit (`Ocean.Service.State.CpuLimit`)

At start the service puts itself in a Windows job object with a hard CPU cap (`CpuLimitPercent`, 25 % of the
whole PC by default, 100 = none; a change in Settings applies at once). Processes it starts join the job, so
ffmpeg is inside the same budget. ffmpeg also gets a nested job capped at two thirds of it; a nested job's rate is a
share of its parent's budget, not of the PC. Both jobs kill their processes when closed, so ffmpeg never
outlives the service. ffmpeg's thread count follows its share (more threads would only wait, and take memory).
Measured on 28 cores: a lighter copy of a 14-min 1080p video took 54 s at 17 % average (21 % peak) instead of
30 s at 61 %; streaming under the cap still ran at 2.1 Gbit/s. The app's player is not capped.

## Downloads

Jobs are persisted (`downloads.json`) and resumed with HTTP ranges from a `.oceanpart` file. Destination
= `<download dir>\<peer name>\<relative path>` after sanitizing each segment (invalid characters,
reserved device names, 200-char segment cap, unique suffix on collision); .NET handles paths beyond
`MAX_PATH`. Up to N parallel jobs (setting), 3 automatic retries with back-off for transient errors.
Downloads read through the stream cache (below), so whatever was already watched is copied from disk.

## Bandwidth

- **Viewer side.** The player's requests go to `PeerMedia`: the same video in my own shares (name + size)
  or a finished download of it plays from disk; otherwise bytes come through the **stream cache**
  (`cache/streams/`): one sparse file per video filled in 1 MB blocks + a block map, keyed by node, item,
  size and mtime. Cached blocks are served from disk, missing runs are fetched from the owner and stored.
  LRU eviction beyond the size in Settings (default 5 GB, 0 = off). A finished download drops the video's
  cached blocks.
- **Owner side.** `UploadScheduler` paces every outgoing video byte. Requests marked
  `X-Ocean-Purpose: download` yield to streams: while anyone is watching, downloads together get a fifth
  of the speed limit (or, without a limit, a quarter of the peak upload rate seen recently, at least
  1 MB/s). Connections take turns in arrival order (fair per connection). At most 4 downloads are served
  at once (2 per node); others get `503 busy` + `Retry-After` and wait without counting as a failure.

## UI

WPF, MVVM (CommunityToolkit.Mvvm), a single dark theme. Large libraries stay smooth thanks to a
uniform-tile `VirtualizingWrapPanel` (only visible rows have containers) and lazy thumbnail loading
(decoded at 320 px, frozen, LRU-capped). The tray icon is a WinForms `NotifyIcon` drawn at runtime.
The box above the node list filters nodes; the box at the top of the content area searches videos (typing
opens the search page, clearing it returns to the page you came from).

## Data directory (`%ProgramData%\Ocean`, override `OCEAN_DATA_DIR`)

```
config.json      node name, secret, ports, shares (+ password hashes, PBKDF2-SHA256)
node.pfx         identity key pair
local.token      UI ↔ service secret
grants.json      issued grants (owner side)
peers.json       last-visit times and grants we hold (browsing side)
downloads.json   download jobs
votes.json       votes on my videos (voter id -> vote, time); my-votes.json  my votes on others' videos (+ pending)
updates/         downloaded installer of a newer version
catalog/         per-share item index + version
thumbs/          own thumbnails;  cache/thumbs/<peer>/  remote thumbnails
cache/streams/   watched video bytes from other PCs (sparse files + block maps)
cache/peers/     last known catalog of each node (offline view, delta base)
logs/            service-YYYYMMDD.log + app-YYYYMMDD.log (14 days; the app log falls back to %LocalAppData%\Ocean\logs)
```

## Possible next steps

- LibVLC-based in-app playback as an optional download for codec-independent playback.
- Transfer resumption across peers that re-appear with a new address (already handled by id, not IP).
- IPv6 multicast discovery alongside IPv4 broadcast.
- Per-peer bandwidth caps and QoS for concurrent viewers.
