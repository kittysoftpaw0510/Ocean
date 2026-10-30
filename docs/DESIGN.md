# Ocean — design

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

## Discovery (UDP 38472)

Every 4 s each node broadcasts a signed JSON `hello` (to `255.255.255.255` and to each interface's
directed broadcast). On hearing a new node it answers with a unicast `hello`, so a newcomer sees everybody
within one round trip. A `bye` is sent on shutdown; silent nodes expire after 16 s. The beacon carries
the catalog version and share count, so browsing nodes refetch a catalog only when it changed.

Beacon: `{v, t, id, pk, name, user, host, port, realm, cat, shares, ts, sig}`.

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
  thumbnails from `IShellItemImageFactory` — the same providers Explorer uses, run on a dedicated STA
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
| GET/HEAD | `/p/stream/{itemId}` | `Accept-Ranges: bytes`; rate-limited through a global token bucket when an upload cap is set |

## Local API (HTTP 127.0.0.1:38470)

Status, event stream, peers and their catalogs, unlocking, thumbnail/stream proxies, share CRUD and
per-item policies, pending requests and grants, downloads, settings. See `Api/LocalApi.cs`.

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

## Data directory (`%ProgramData%\Ocean`, override `OCEAN_DATA_DIR`)

```
config.json      node name, secret, ports, shares (+ password hashes, PBKDF2-SHA256)
node.pfx         identity key pair
local.token      UI ↔ service secret
grants.json      issued grants (owner side)
peers.json       last-visit times and grants we hold (browsing side)
downloads.json   download jobs
catalog/         per-share item index + version
thumbs/          own thumbnails;  cache/thumbs/<peer>/  remote thumbnails
cache/streams/   watched video bytes from other PCs (sparse files + block maps)
cache/peers/     last known catalog of each node (offline view, delta base)
logs/            service-YYYYMMDD.log (14 days), app-errors.log
```

## Possible next steps

- LibVLC-based in-app playback as an optional download for codec-independent playback.
- Transfer resumption across peers that re-appear with a new address (already handled by id, not IP).
- IPv6 multicast discovery alongside IPv4 broadcast.
- Per-peer bandwidth caps and QoS for concurrent viewers.
