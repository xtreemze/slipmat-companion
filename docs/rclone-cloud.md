# rclone cloud setup and recovery

Audio Gateway treats rclone as an operator-owned cloud adapter. The Jellyfin plugin never performs provider login, reads `rclone.conf`, or returns provider credentials.

## Server contract

The Jellyfin service/container must be able to execute the intended rclone binary and see the intended rclone configuration.

- Put `rclone` on the Jellyfin service `PATH`, or set `SLIPMAT_RCLONE`.
- Use rclone's normal config location, or set `RCLONE_CONFIG` for the Jellyfin service/container.
- Confirm the same execution context can run `rclone listremotes` and browse the selected remote.

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

Do not replace the remote's backing account/root without also choosing a new projection path, unless you intentionally want to reuse the same projected source identity.

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
