#!/bin/sh
# Run only inside a disposable Linux systemd VM with a masked Jellyfin stand-in.
set -eu
if [ "$(id -u)" -ne 0 ] || [ "$#" -ne 5 ]; then
    echo "Usage: sudo $0 FIXTURE_ROOT RUNNER FIXTURE_HELPER RCLONE CUSTODY_HELPER" >&2
    exit 2
fi
root=$1 runner=$2 fixture=$3 rclone=$4 helper=$5
case "$root" in /mnt/slipmat-maintenance-fixture-[a-zA-Z0-9_-]*) ;; *) echo 'Invalid fixture path' >&2; exit 2;; esac
case "$root" in *[!a-zA-Z0-9_/-]*|*..*) echo 'Invalid fixture path' >&2; exit 2;; esac
for path in "$runner" "$fixture" "$rclone" "$helper"; do
    case "$path" in /*) ;; *) echo 'Absolute paths required' >&2; exit 2;; esac
    case "$path" in *[!a-zA-Z0-9_./-]*) echo 'Unsafe path' >&2; exit 2;; esac
done
[ ! -e "$root" ] && [ "$(readlink /etc/systemd/system/jellyfin.service)" = /dev/null ] && [ ! -e /run/systemd/system/jellyfin.service ] || { echo 'Fresh fixture and masked stand-in required' >&2; exit 2; }
# Fail if a real Jellyfin unit is installed. This test temporarily replaces the mask.
[ ! -e /usr/lib/systemd/system/jellyfin.service ] && [ ! -e /lib/systemd/system/jellyfin.service ] || { echo 'Real or unknown Jellyfin unit present' >&2; exit 2; }
job=$("$fixture" init "$root" "$rclone" "$helper")
case "$job" in *[!0-9a-f]*|'') echo 'Invalid job ID' >&2; exit 2;; esac
[ "${#job}" -eq 32 ] || exit 2
"$runner" prepare "$root/journal" "$job" "$root/draft.json" "$root/bundle/spec.json"
"$runner" run "$root/journal" "$job" "$root/bundle/spec.json"
"$fixture" inspect "$root" "$job" ready-to-finalize
findmnt -rn --mountpoint "$root/music" -o TARGET,SOURCE,FSTYPE,OPTIONS
systemctl is-active "slipmat-vfs-$job.service"
setpriv --reuid=65534 --regid=65534 --clear-groups -- cat "$root/music/track.flac"
before=$(sha256sum "$root/journal/$job.json" | cut -d ' ' -f1)
# Only remove the mask we verified above; restore it even when startup fails.
restore_mask() {
    if [ ! -e /etc/systemd/system/jellyfin.service ] && [ ! -L /etc/systemd/system/jellyfin.service ]; then ln -s /dev/null /etc/systemd/system/jellyfin.service; fi
    rm -f /run/systemd/system/jellyfin.service
    systemctl daemon-reload
}
printf '[Unit]\nDescription=Disposable Jellyfin startup guard fixture\n[Service]\nType=oneshot\nExecStartPre=%s startup %s/journal %s %s/bundle/spec.json\nExecStart=/usr/bin/true\n' "$runner" "$root" "$job" "$root" > /run/systemd/system/jellyfin.service
rm /etc/systemd/system/jellyfin.service
trap restore_mask EXIT HUP INT TERM
systemctl daemon-reload
systemctl start jellyfin.service
after=$(sha256sum "$root/journal/$job.json" | cut -d ' ' -f1)
[ "$before" = "$after" ] || { echo 'Startup modified journal' >&2; exit 1; }
restore_mask
trap - EXIT HUP INT TERM
"$runner" rollback "$root/journal" "$job" "$root/bundle/spec.json"
"$fixture" inspect "$root" "$job" rolled-back
cmp "$root/music/track.flac" "$root/remote/track.flac"
[ ! -e "$root/music.backup" ]
! findmnt -rn --mountpoint "$root/music"
[ "$(readlink /etc/systemd/system/jellyfin.service)" = /dev/null ]
printf 'VM fixture passed: %s %s\n' "$root" "$job"
