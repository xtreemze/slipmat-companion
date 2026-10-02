# Jellyfin Audio Gateway Plugin

Audio Gateway is an **optional acceleration and enrichment extension** for Slipmat. Jellyfin remains the media server and Slipmat remains fully capable without this plugin.

The plugin may precompute, cache, batch, synchronize, or enrich information that Slipmat can also obtain or own through its client/Rust paths. It must never become the sole owner of a user-visible Slipmat capability.

## Jellyfin catalog installation

For a normal Jellyfin installation, adding the repository and installing **Audio Gateway** is sufficient:

1. Add `https://xtreemze.github.io/slipmat-companion/manifest.json` under **Dashboard → Plugins → Repositories**.
2. Install **Audio Gateway**.
3. Restart Jellyfin.

No separate artifact-store mount or analyzer service is required for the plugin's core batching, podcast acquisition, or subscription-replica capabilities. The plugin creates a writable store under Jellyfin's own data directory automatically.

The dashboard's **Artifact store override** is advanced configuration only. Existing deployments with a real `/store` mount remain compatible, and `SLIPMAT_ARTIFACT_STORE_ROOT` can override the managed path.

Audio analysis is integrated into the plugin and uses the FFmpeg binary already managed by Jellyfin. The companion amplitude fallback is explicitly identified as `awf_v1_riff_mono_u8_peak`; it is a mono RIFF/WAVE u8 max-peak envelope and is not byte-compatible with Slipmat's retired `audiowaveform` `awf_v1_native_mono_b8` DAT format. No second analyzer service, URL, container, or media mount is required. Missing artifacts are queued asynchronously from normal companion requests and Jellyfin library changes; the dashboard also exposes a daily **Slipmat audio analysis** scheduled task for idempotent backfill. The integrated analyzer preserves the source-declared sample rate and the first one or two source channels, matching Slipmat's canonical Rust accumulator topology. It writes amplitude envelopes, SLWS v2 five-band spectral tiers with Rust-matching proportional max-pooling for coarse tiers, canonical v6 `SBND` source-boundary evidence, conservative `SRHY` rhythm evidence with Rust-equivalent candidate/phase tie-breaking and f32 BPM quantization, and `AnalysisBlocks` containing timing, harmonic, legacy-compatible loudness fields, provider-neutral measured EBU loudness evidence (LUFS/dBTP/LRA with analyzer provenance), and an energy curve. Multichannel sources select their first two lanes rather than downmixing. Missing source stream metadata or weak evidence fails closed while Slipmat retains its local fallback.

## Compatibility policy

This repository carries no legacy plugin/API compatibility surface.

- Supported Jellyfin server: **12.1.0**, the current Jellyfin 12 release targeted by this plugin contract.
- Plugin package references must match that server release exactly.
- `/Plugins/AudioGateway/v1` is removed; there is no v1 compatibility shim.
- When Jellyfin support moves to a new certified release, update and certify the plugin rather than accumulating old-server compatibility code.
- If the plugin is absent, incompatible, unhealthy, or partially configured, Slipmat continues through its canonical client-side path.

## Shipping capability boundary

The current build advertises and ships only capabilities that are already client-owned:

- `analysisArtifacts` — optional precomputed waveform/spectral/analysis artifacts;
- `trackMetadataBatching` — optional reduction of Jellyfin metadata request fan-out;
- `artworkPalette` — optional bounded extraction of provenance-bearing RGB swatches from album/track primary artwork so clients can receive colour evidence in pre-playback metadata instead of decoding artwork locally;
- `podcastDirectorySearch` — optional authenticated server-side Podcast Index search; API credentials never leave the Jellyfin process;
- `podcastSubscriptions` — optional authenticated per-user replica of Slipmat podcast subscription intent for cross-device continuity;
- `podcastFeedRefresh` — optional authenticated bounded server fetch for podcast feeds, chapters, and transcripts when direct browser acquisition is blocked by CORS;
- `directoryResourceFetch` — bounded raw-JSON fallback for client-owned gpodder, Radio Browser, and iptv-org discovery;
- `liveGuideResourceFetch` — bounded XMLTV acquisition for guide URLs published by the current iptv-org catalog; parsing remains client-owned;
- `remoteMediaRelay` — short-lived opaque SSRF-hardened relay for browser media/sample acquisition without exposing a generic proxy;
- native iptv-org Live TV projection — administrator-selected channels published through Jellyfin's `ILiveTvService`, separate from Slipmat's per-user subscription authority.

The following protocol slots intentionally advertise `false`:

- `atlasProjectionCache` — the existing C# Atlas implementation still contains resolver and product-semantic decisions;
- `acquisitionSearch` — the experimental streamrip implementation has no production client-owned equivalent yet;
- `podcastSnapshotCache` — normalized feed snapshots remain client/provider-owned; the companion fetcher does not persist or normalize them.

Those Atlas/provider sources remain in the repository as refactoring material, but they are excluded from the shipping plugin assembly in `Jellyfin.Plugin.AudioGateway.csproj`. The test project compiles them separately so their behavioral tests remain available while the implementations are reduced to pure cache/adapter behavior. Their controllers also retain `[NonController]` as a second guard.

Presence of experimental source code does not grant product authority. The capability response declares `authoritative: false`, and a Slipmat client rejects an extension that claims authority or exposes an incompatible protocol/server version.

## Active endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/Plugins/AudioGateway/diagnostics/capabilities` | Public extension protocol/module negotiation |
| GET | `/Plugins/AudioGateway/artifacts/analysis/{itemId}` | Authenticated precomputed analyzer sidecar with ETag / 304 support |
| GET | `/Plugins/AudioGateway/artifacts/waveform/{itemId}?variant=awf_v1_riff_mono_u8_peak&pps=10` | Authenticated waveform/spectral artifact with ETag / 304 support |
| GET | `/Plugins/AudioGateway/events/track/{itemId}?include=waveform,sidecar&waveformPps=10` | Authenticated optional track metadata composition, including artwork palette evidence when available |
| POST | `/Plugins/AudioGateway/events/track/batch` | Authenticated batch track metadata lookup, including artwork palette evidence before activation |
| GET | `/Plugins/AudioGateway/podcasts/subscriptions` | Authenticated current-user podcast subscription replica |
| POST | `/Plugins/AudioGateway/podcasts/subscriptions/sync` | Authenticated deterministic merge of the current user's client/server subscription replicas |
| POST | `/Plugins/AudioGateway/podcasts/resources/fetch` | Authenticated bounded feed/chapter/transcript acquisition for browser CORS fallback |
| POST | `/Plugins/AudioGateway/podcasts/directory/search` | Authenticated bounded Podcast Index search using server-side credentials |
| POST | `/Plugins/AudioGateway/directories/resources/fetch` | Authenticated allowlisted raw-JSON directory fallback |
| POST | `/Plugins/AudioGateway/media/relay/prepare` | Authenticated bounded opaque media-relay preparation |
| GET | `/Plugins/AudioGateway/media/relay/{relayId}` | Authenticated short-lived relay response |
| GET | `/Plugins/AudioGateway/livetv/iptv-org/channels` | Elevated bounded iptv-org browse/search for Jellyfin Live TV publication |
| POST | `/Plugins/AudioGateway/livetv/iptv-org/catalog/refresh` | Elevated provider refresh with last-known-good fallback |
| POST | `/Plugins/AudioGateway/livetv/iptv-org/guide/fetch` | Authenticated bounded XMLTV fetch for catalog-listed guide URLs |
| POST | `/Plugins/AudioGateway/livetv/iptv-org/refresh` | Elevated refresh of Jellyfin's native Live TV projection |
| GET | `/Plugins/AudioGateway/cloud/rclone/status` | Elevated sanitized rclone/cloud projection health |
| GET | `/Plugins/AudioGateway/cloud/rclone/remotes` | Elevated configured-rclone remote descriptors (no credentials) |
| GET | `/Plugins/AudioGateway/cloud/rclone/browse` | Elevated structured browse of one configured rclone remote |
| POST | `/Plugins/AudioGateway/cloud/rclone/mkdir` | Elevated creation of a folder within a configured rclone remote |
| POST | `/Plugins/AudioGateway/cloud/rclone/reconcile` | Elevated one-way rclone copy into the Jellyfin projection |

Podcast routes never accept a user ID parameter. The Jellyfin `Jellyfin-UserId` authentication claim selects the storage namespace for subscription replication; a client payload cannot address another user's subscriptions.

Atlas concept/catalog/request routes and streamrip status/search routes do not exist in the shipping plugin assembly.

## Operator-managed rclone cloud projection

Audio Gateway uses a **pre-installed and pre-configured rclone** as its generic cloud-storage adapter. The plugin does not implement provider authentication itself and does not read or expose the credential-bearing rclone configuration.

Configure remotes on the server with rclone first. Tele2 Cloud and other supported cloud providers then appear through the same plugin interface. rclone itself owns provider-specific OAuth/session behavior, token refresh, and backend semantics.

The Jellyfin plugin settings provide:

- a selector populated from `rclone listremotes --long --json`;
- a structured remote file manager backed by `rclone lsjson`;
- remote folder creation through `rclone mkdir`;
- **Use this folder** selection for the media root;
- automatic Jellyfin-managed local materialization storage, with an advanced absolute-path override;
- manual and six-hour scheduled reconciliation.

The executable is operator-owned. Audio Gateway invokes `rclone` from Jellyfin's service `PATH`, or `SLIPMAT_RCLONE` when explicitly provided by the host. rclone's own `RCLONE_CONFIG` mechanism may point the service at the intended config file. The plugin never calls `config dump`, `config show`, or any other command that returns provider secrets.

Audio Gateway intentionally does **not** start or expose rclone's remote-control API. rclone documents RC access as shell-equivalent and capable of reading stored credentials and running broad filesystem/command operations; that authority is too wide for this companion boundary.

Materialization is one-way and additive:

```text
operator-configured rclone remote
        |
     rclone copy
        |
        v
marker-owned local projection
        |
        v
Jellyfin virtual folder + guarded library scan
```

`rclone copy` updates/adds remote content without deleting destination files. Audio Gateway does not automatically delete cloud objects or local projected media. The projection marker binds to the configured rclone remote name/type and selected remote root; changing that source against an existing marker fails closed.

This projection is optional server acceleration. It does not own Slipmat media identity, source selection, playback, queue, Rail, podcast identity, or offline-retention policy.

## Capability contract

`GET /Plugins/AudioGateway/diagnostics/capabilities` returns:

- `protocolVersion`: extension protocol understood by this build;
- `mode`: `optional-acceleration`;
- `authoritative`: always `false`;
- `supportedJellyfinVersion`: exact stable server release targeted by the plugin;
- `modules`: individually negotiable optional modules;
- analyzer/store health and degraded reasons.

A module being unavailable is not a product failure. It means Slipmat uses its canonical local/client implementation.

Artwork palette extraction is intentionally presentation-neutral. The companion samples Jellyfin-owned primary artwork with the same SkiaSharp image stack used by Jellyfin, emits at most six representative RGB swatches with normalized sampling weights plus an opaque artwork revision, and caches the result in bounded memory. Album-primary artwork is preferred with embedded track art as the fallback, matching the existing artwork URL projection. Filesystem paths and UI-specific CSS/token decisions never leave the server boundary.

## Jellyfin host integration

The plugin uses Jellyfin's native plugin surfaces rather than treating the server only as an HTTP host:

- `ServiceRegistrator` implements `IPluginServiceRegistrator` and registers reusable gateway services in Jellyfin's dependency-injection container;
- the integrated analyzer worker/backfill remains host-managed and non-authoritative;
- `IptvOrgLiveTvService` is registered as Jellyfin's native `ILiveTvService`;
- the operator-managed rclone cloud bridge registers its narrow CLI/process boundary and six-hour reconciliation task;
- artifact and track-event controllers consume reusable services through constructor injection;
- `Plugin` implements `IHasWebPages` and exposes a native Jellyfin dashboard for store/analyzer health, iptv-org publication, and generic rclone cloud projection administration;
- the configuration page reports current health through bounded, non-authoritative endpoints.

### Host-neutral artifact identity

Jellyfin item IDs are request-layer identities, not artifact-store keys. The adapter projects each Jellyfin item into the shared `AnalysisSubjectV1` contract:

```text
hostKind = jellyfin
providerInstanceId = IServerApplicationHost.SystemId
resourceId = Jellyfin item GUID in N format
representationId = absent until an exact media-source identity is available
```

Rust and C# implement the same length-prefixed SHA-256 key derivation and pin the same compatibility vector. Artifacts are stored under opaque `asv1-<sha256>` keys:

```text
analysis/{subjectStoreKey}.json
waveforms/{subjectStoreKey}/{variant}/pps_{pps}.dat
```

V2-family sidecars currently use schema 2.1.0 and carry the subject/store schema versions, the derived subject key, complete-source fingerprint, producer version, artifact references, and optional derived analysis facts. They do not carry a Jellyfin item ID. The plugin derives the expected subject key independently and treats a missing, corrupt, schema-incompatible, or mismatched sidecar as ordinary artifact absence. Client-facing track events add the Jellyfin item ID only at the host-adapter boundary.

Because Slipmat is unreleased, the former raw-`itemId` artifact layout is regenerated rather than dual-written or retained behind an indefinite compatibility shim.

Integrated analysis observes Jellyfin audio additions/updates through a bounded single-worker queue, and the scheduled backfill reconciles existing local audio without blocking playback. One Jellyfin-FFmpeg source decode feeds exact-source-rate amplitude/spectral/source-boundary/rhythm analysis plus harmonic key/Camelot analysis aligned to Slipmat's Rust/WASM first-channel, framing, Hann-window, single-precision FFT, chroma, and profile-scoring semantics, a bounded energy curve, and EBU R128 loudness/true-peak/LRA measurement. `SBND` v6 is derived from the same 20 Hz four-band/RMS envelope contract and is embedded ahead of `SRHY` exactly as the client parser expects. Host-neutral subject identity remains canonical. Pairwise transition selection, playback policy, and client fallback remain Slipmat-owned.

## Podcast Index directory search

Podcast Index is optional and complements the public gpodder.net client adapter. The browser never receives the Podcast Index API key, API secret, signing input, or authorization digest.

Configure the Jellyfin host process with:

```text
SLIPMAT_PODCAST_INDEX_API_KEY=<your Podcast Index API key>
SLIPMAT_PODCAST_INDEX_API_SECRET=<your Podcast Index API secret>
SLIPMAT_PODCAST_INDEX_USER_AGENT=Slipmat-AudioGateway/1.0
```

The user-agent variable is optional. Both key values must be present before `podcastDirectorySearch` is advertised. Search is bounded to 50 results, uses only the official `search/byterm` endpoint, and returns normalized publisher-feed evidence. Subscription still passes through the client-owned RSS preflight and subscription repository.

Do not put these values in Slipmat browser settings, query parameters, logs, source control, or capability payloads. For Docker, inject them as container environment variables or secrets at deployment time.

## Podcast subscription replica

The companion persists only the portable subscription contract defined by Slipmat. It does not turn feeds into Jellyfin shows, episodes, or `AudioPodcast` library items.

- canonical feed/subscription identity remains defined by Slipmat's provider contracts;
- user identity is not embedded in the portable record;
- records use logical revisions and durable unsubscribe tombstones so stale clients cannot resurrect old subscriptions;
- merge batches are bounded and deterministic;
- credential-bearing/query-bearing feed locators are rejected from the portable record;
- files are stored per authenticated Jellyfin user under the configured plugin store root using atomic replacement;
- listening progress, queue state, downloads, Playback Rail state, and playback generations are not stored here.

## Podcast CORS fallback fetch

`POST /Plugins/AudioGateway/podcasts/resources/fetch` is a narrow execution adapter for cases where the browser cannot directly acquire a podcast resource. It does **not** parse feeds, create normalized snapshots, or become a generic HTTP proxy.

The request contains only:

- `url`;
- `kind`: `feed`, `chapters`, or `transcript`;
- optional `etag` and `lastModified` validators.

The companion performs only GET requests and returns the exact bounded response bytes as base64 with the final URL, content type, validators, and byte length. Upstream `304 Not Modified` becomes `status: "not-modified"` with no body.

Security and resource bounds are enforced in the transport:

- HTTP/HTTPS only; URL credentials and fragments are rejected;
- DNS is resolved at connection time and **every** resolved address must be globally routable;
- loopback, private, link-local, carrier-NAT, documentation, benchmark, multicast, ULA/site-local, and equivalent special ranges are rejected, including mapped/embedded private addresses;
- system proxies and cookies are disabled;
- no Jellyfin authorization, browser cookie, or caller-defined target header is forwarded upstream;
- redirects are manual, capped at five, and each location is revalidated before connection;
- HTTPS-to-HTTP redirects are rejected;
- connection/request timeouts are bounded;
- decoded response ceilings are 4 MiB for feeds, 1 MiB for chapters, and 2 MiB for transcripts.

`podcastFeedRefresh` therefore means “the companion can safely execute bounded podcast resource acquisition.” The client still owns refresh generations, feed parsing, identity, last-known-good snapshot replacement, and all presentation/playback semantics. `podcastSnapshotCache` remains false.

## Authority rules

The plugin may expose facts, reusable artifacts, bounded execution adapters, and optional replicas of client-owned user intent. It does not own:

- playback source/transport authority;
- queue, seek, crossfade, or Playback Rail semantics;
- normalization policy or gain application;
- realtime visualizer state;
- Atlas concept/edition/artifact/playback semantics or resolver decisions;
- podcast feed/show/episode identity semantics;
- normalized podcast feed snapshots or acceptance of refresh generations;
- podcast listening progress or offline-retention policy;
- acquisition/provider semantics without an equivalent client-owned implementation.

Atlas acquisition requests are persisted canonically in the client request store. Optional server synchronization may later cache those requests, but plugin absence or failure leaves them valid and `local-only` rather than producing a product error.

## Authentication

- `diagnostics/capabilities` is intentionally public for opportunistic discovery;
- all active data/artifact/subscription/podcast-resource routes require an authenticated Jellyfin session/token;
- podcast subscription storage derives the namespace from the authenticated Jellyfin user claim and accepts no user selector from request payload/query parameters;
- the podcast resource fetcher never forwards the Jellyfin token or ambient browser credentials to an upstream podcast host;
- the client uses Jellyfin's current `Authorization: MediaBrowser ...` mechanism rather than deprecated legacy token headers.

## Local prerequisites

- **.NET 10 SDK 10.0.401** (pinned by `global.json`)
- NuGet access to `nuget.org` (configured in `NuGet.Config`)

## Local build

```bash
# From repository root
dotnet restore
dotnet build --configuration Release
dotnet test Tests/ --logger "console;verbosity=normal"
```

For an installable development build:

```bash
dotnet publish -c Release -o ./dist/publish
```

Mount/copy the resulting plugin files into a dedicated Audio Gateway subdirectory under Jellyfin's plugin directory and restart Jellyfin. The plugin provisions its own managed store and analyzer pipeline by default; no external analyzer deployment is required.

This standalone companion repository is licensed under GNU GPL v3. Public plugin distribution is approved in `distribution-policy.json`. CI runs automatically on pull requests and pushes; a successful `main` build automatically publishes a new plugin version when that reviewed four-part version has not already been released.

`SLIPMAT_ARTIFACT_STORE_ROOT` can override the Jellyfin-managed companion store. The historical `/store` path is retained automatically when that mount actually exists. The former external analyzer URL is no longer required for catalog installs.

## Directory layout

```text
./
  Jellyfin.Plugin.AudioGateway.csproj   — shipping Jellyfin 12.1.0 / net10.0 plugin boundary
  Plugin.cs                              — BasePlugin<PluginConfiguration> entry point
  Configuration/                        — optional analyzer/store configuration
  Models/                               — serialized optional-extension contracts
  Api/                                  — active fact/artifact/user-replica adapters plus excluded experimental source
  Services/                             — active adapters plus excluded Atlas/provider refactoring material
  Tests/                                — contract tests and test-only compilation of quarantined code
```

## Naming conventions

- all time values use milliseconds (`durationMs`, `timeMs`, `startMs`, ...);
- `year` is an integer;
- artwork URLs use bounded `maxWidth` values;
- analysis/provider payloads describe facts and provenance; playback policy stays in Slipmat.


## Releases and Jellyfin repository manifest

This is the standalone Slipmat companion repository. Slipmat remains authoritative for product semantics; the plugin is an optional Jellyfin-side acceleration, enrichment, batching, precomputation, synchronization, and bounded server-adapter layer.

- `global.json` pins the supported .NET SDK.
- `Directory.Build.props` carries the reviewed four-part plugin version used for local builds and releases.
- `Jellyfin.Plugin.AudioGateway.slnx` builds the shipping plugin and its test project from the repository root.
- `build.yaml` carries Jellyfin plugin repository metadata, including the stable plugin GUID and target ABI.
- `scripts/verify_release_metadata.py` rejects drift between the Jellyfin target ABI, package references, plugin GUID, assembly artifact, target framework, and reviewed release version.
- `distribution-policy.json` records the owner-approved GPL-3.0-only public-distribution policy.
- `scripts/package_plugin.py` creates the deterministic plugin ZIP.
- `scripts/generate_manifest.py` emits the Jellyfin repository JSON containing the release URL and package checksum.
- `.github/workflows/ci.yml` validates metadata, builds, runs unit/contract tests plus the shared source-boundary media corpus through the runner FFmpeg decoder, packages, exercises manifest generation, and uploads the exact tested release candidate for successful `main` pushes.
- `.github/workflows/release.yml` runs only after successful `main` CI, consumes that exact tested artifact, creates a GitHub Release for an unreleased version, and publishes the Jellyfin repository manifest through GitHub Pages.

To publish a new version, update the same four-part version in `build.yaml` and `Directory.Build.props`, update the quoted `changelog` in `build.yaml`, and merge to `main`. CI and release publication then proceed automatically. Commits that keep an already-published version still run CI but do not create or replace a release. The Pages manifest preserves previously published versions for Jellyfin compatibility selection.

The generated Pages artifact contains `manifest.json`. GitHub Pages is a distribution surface only; Slipmat must continue to work with stock Jellyfin when this companion is absent or unavailable.
