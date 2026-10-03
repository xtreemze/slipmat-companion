# rclone cloud setup and recovery

Audio Gateway treats rclone as an operator-owned cloud adapter. The Jellyfin plugin never performs provider login, reads `rclone.conf`, or returns provider credentials.

## Server contract

The Jellyfin service/container must be able to execute the intended rclone binary and see the intended rclone configuration.

- Put `rclone` on the Jellyfin service `PATH`, or set `SLIPMAT_RCLONE`.
- Use rclone's normal config location, or set `RCLONE_CONFIG` for the Jellyfin service/container.
- Confirm the same execution context can run `rclone listremotes` and browse the selected remote.

The selected cloud library is exposed with `rclone mount`, not `rclone copy`. The Jellyfin service/container must therefore also have the platform filesystem-mount dependency required by rclone. On Linux this normally means FUSE is installed and the service/container can access the FUSE device. Audio Gateway does not elevate privileges or add container capabilities itself.

Each configured cloud-library profile starts its own read-only mount with an independent full VFS cache. A profile owns one rclone remote/root, one Jellyfin library projection, one mount path, and one cache path/policy. Existing single-projection configuration is imported as the stable `legacy` profile.

Fresh profiles default to:

- 16 GiB target cache maximum;
- 24 hours maximum idle cache age;
- 4 GiB minimum free space on the cache volume.

These values are configurable in the Jellyfin plugin page. The cache is disposable: eviction removes only local cached bytes. The cloud object remains authoritative. Open/in-use files can temporarily prevent rclone from immediately reaching the configured cache target.

## OAuth refresh failures

A remote may still appear in `rclone listremotes` even when its provider OAuth token can no longer be refreshed. Audio Gateway classifies known rclone OAuth refresh failures as:

`remote-authentication-required`

Authentication remains an operator action outside Jellyfin. Reconnect the existing remote:

```bash
rclone config reconnect REMOTE:
```

Then verify provider access:

```bash
rclone lsd REMOTE:
```

Do not replace the remote's backing account/root without also choosing a new mount path. The adjacent projection marker binds the local mount identity to the selected rclone remote name/type/root and rejects mismatches.

## Headless Tele2 / Jottacloud traditional OAuth

Tele2 uses rclone's Jottacloud backend with traditional OAuth. The Jottacloud backend does **not** support `rclone authorize` for this authentication mode.

On the browser workstation, open an SSH tunnel to the Jellyfin/rclone server:

```bash
ssh -L localhost:53682:localhost:53682 USER@SERVER
```

Keep that SSH session open. On the server run:

```bash
rclone config reconnect tele2:
```

For the reconnect prompts:

1. **Already have a token - refresh?** → `y`
2. **Use web browser to automatically authenticate rclone with remote?** → `y`
3. If rclone cannot launch a browser on the server, copy the printed `http://127.0.0.1:53682/auth?...` URL into the browser on the workstation.
4. Complete the Tele2 login and wait for rclone to report `Got code`.
5. Choose the desired device/mountpoint. Answering **No** to the non-standard mountpoint prompt selects the default Archive area; choose **Yes** when Sync or a backup-device mountpoint is required.
6. Verify the recovered remote:

```bash
rclone lsd tele2:
```

The authoritative rclone references are:

- Jottacloud backend and OAuth/session behavior: https://rclone.org/jottacloud/
- Headless SSH-tunnel setup on localhost port 53682: https://rclone.org/remote_setup/

### Token-rotation caution

Jottacloud documents refresh-token rotation and automatic reuse detection. Reusing copied refresh-token state from multiple active rclone installations can invalidate the token family and cause `invalid_grant`. Traditional OAuth also has stricter session behavior than many other rclone backends.

Prefer one active server-side Tele2/Jottacloud authentication for the companion. When a reconnect is needed, reconnect that server remote rather than attempting `rclone authorize jottacloud`.


## Media, metadata, and analysis storage

The cloud media tree and the local caches have deliberately different lifetimes:

- media files remain authoritative on the rclone remote;
- the VFS cache contains only recently read media bytes and may be evicted;
- Jellyfin catalog metadata remains in Jellyfin's normal data/database storage;
- Slipmat waveform, spectral, loudness, rhythm, and related analysis sidecars remain in the companion artifact store;
- artwork palette evidence is a bounded derived cache keyed by the Jellyfin artwork revision.

The mount is read-only. Do not enable a workflow that expects Jellyfin to write `.nfo`, artwork, or other metadata files back through this mount. Cloud-side metadata mutation should be a separate explicit operator workflow if ever required.

Automatic Companion analysis deliberately skips every cloud-mounted item, across every configured VFS profile. Otherwise a scheduled backfill or library-change callback could force remote files through FFmpeg and defeat the bounded-cache design. A Slipmat artifact/track request can still enqueue analysis for the specific item being used; that explicit read may temporarily cache the full source file.

Jellyfin's own catalog/library scan is a different operation: it enumerates the mounted namespace so remote files can exist in the Jellyfin catalog and may perform lightweight media reads required by Jellyfin. Audio Gateway does not describe that as analysis or promise that a Jellyfin catalog refresh is payload-free. The protection here is that Companion waveform/peak/spectral/loudness/rhythm backfill never walks uncached VFS media in the background.

## Namespace refresh

rclone maintains directory metadata for the mounted namespace and polls supported remotes. Audio Gateway reconciles every enabled VFS profile every six hours. Each profile is handled independently, so an unavailable provider does not block unrelated mounts. The task can queue Jellyfin namespace/catalog refresh after a mount is ready; it never performs Companion analysis backfill or copies the media collection.

## Migration from the former copy projection

Older Audio Gateway builds used additive `rclone copy` materialization and wrote a `.slipmat-rclone-cloud-projection.json` marker inside the local projection.

The VFS implementation does not automatically delete or reuse that directory. Reconciliation returns `legacy-materialized-projection-present` so the operator can inspect it safely. After confirming that the remote contains the authoritative media, either archive/remove the old local materialized directory manually or configure a new empty mount path, then reconcile again.

This migration rule is intentionally conservative: the plugin never decides that previously downloaded media is safe to delete.


## Multiple VFS libraries

The dashboard can maintain multiple cloud-library profiles. Use a separate profile for each independent Jellyfin library/root rather than collapsing several local paths into one remote namespace.

For each profile:

1. select an operator-configured rclone remote;
2. browse to or create the cloud folder;
3. choose the Jellyfin library name and collection type;
4. optionally override the local mount/cache paths;
5. set a bounded cache policy;
6. save the profile;
7. mount/refresh that profile.

Mount and cache paths must not overlap across profiles. A conflicting profile fails closed.

## Library migration model

Migration is intentionally staged because the local source is the rollback authority until the cloud copy and cutover have both been verified.

### Local Jellyfin library -> cloud VFS

1. Copy the local library to an empty cloud destination using additive `rclone copy`; do not use `sync`, `move`, `delete`, or `purge`.
2. Verify the local source against the cloud destination with `rclone check` and report whether the backend supplied checksums or only weaker size/modification evidence.
3. Create a VFS profile for that remote root.
4. Cut the Jellyfin library over only after the VFS mount is healthy. The migration program in issue #50 preserves the existing Jellyfin library path where possible to minimize catalog identity churn.
5. Keep the original local directory as a rollback backup.
6. After the VFS-backed library has been rescanned and validated, explicitly finalize cleanup to reclaim local disk space. Cleanup is never automatic.

### Cloud VFS -> local Jellyfin library

1. Choose an empty local destination with enough free space.
2. Copy the cloud root to the local destination and verify it.
3. Switch the Jellyfin library back to the verified local path.
4. Keep the cloud source unchanged unless a separate explicit cloud-deletion workflow is ever approved.

### Bulk migration

Multiple libraries are migrated independently. A future bulk coordinator may run the same staged state machine for every eligible library, but it must never merge roots, silently skip verification, or turn local cleanup into a background eviction policy.
