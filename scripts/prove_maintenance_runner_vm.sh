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
# A separate, root-owned filesystem prevents cache writes from reaching source custody.
mount -t tmpfs -o size=32m,mode=0755 "slipmat-cache-$job" "$root/cache"
[ "$(stat -c %d "$root/cache")" != "$(stat -c %d "$root/music")" ]
"$runner" prepare "$root/journal" "$job" "$root/draft.json" "$root/bundle/spec.json"
# The persistent mask was verified before fixture creation. Restore it after
# every attempted startup, including rejection or interruption.
restore_mask() {
    if [ ! -e /etc/systemd/system/jellyfin.service ] && [ ! -L /etc/systemd/system/jellyfin.service ]; then ln -s /dev/null /etc/systemd/system/jellyfin.service; fi
    rm -f /run/systemd/system/jellyfin.service
    systemctl daemon-reload
}
trap restore_mask EXIT HUP INT TERM
install_guard() {
    printf '[Unit]\nDescription=Disposable Jellyfin startup guard fixture\n[Service]\nType=oneshot\nExecStartPre=%s startup %s/journal %s %s/bundle/spec.json\nExecStart=/usr/bin/true\n' "$runner" "$root" "$job" "$root" > /run/systemd/system/jellyfin.service
    rm /etc/systemd/system/jellyfin.service
    systemctl daemon-reload
}
# Startup must never mutate the authoritative journal. Incomplete or changed
# custody is rejected by ExecStartPre, not repaired during service activation.
check_startup() {
    expected=$1
    journal_before=$(sha256sum "$root/journal/$job.json" | cut -d ' ' -f1)
    install_guard
    if [ "$expected" = blocked ]; then
        if systemctl start jellyfin.service; then
            echo 'Startup unexpectedly admitted incomplete or changed state' >&2
            exit 1
        fi
        # Do not count unrelated service-manager failures as a passed guard test.
        # The runner uses exit 1 only when it deliberately blocks startup.
        guard_result=$(systemctl show jellyfin.service --property=ExecStartPre --value)
        case "$guard_result" in
            *'code=exited'*'status=1'*) ;;
            *) echo 'Startup failure was not a guard rejection' >&2; exit 1 ;;
        esac
    else
        systemctl start jellyfin.service
    fi
    journal_after=$(sha256sum "$root/journal/$job.json" | cut -d ' ' -f1)
    [ "$journal_before" = "$journal_after" ] || { echo 'Startup modified journal' >&2; exit 1; }
    restore_mask
}
# Prepared intent is incomplete. Refuse Jellyfin startup before cutover.
check_startup blocked
"$runner" run "$root/journal" "$job" "$root/bundle/spec.json"
"$fixture" inspect "$root" "$job" ready-to-finalize
findmnt -rn --mountpoint "$root/music" -o TARGET,SOURCE,FSTYPE,OPTIONS
systemctl is-active "slipmat-vfs-$job.service"
setpriv --reuid=65534 --regid=65534 --clear-groups -- cat "$root/music/track.flac"
# Simulate a changed plugin configuration after successful promotion.
# Keep the exact promoted bytes so the fixture can restore them safely.
cp "$root/profile.xml" "$root/bundle/profile-promoted-for-test.xml"
printf '\n<!-- injected startup drift -->\n' >> "$root/profile.xml"
check_startup blocked
cp "$root/bundle/profile-promoted-for-test.xml" "$root/profile.xml"
cmp "$root/bundle/profile-promoted-for-test.xml" "$root/profile.xml"
check_startup stable
"$runner" rollback "$root/journal" "$job" "$root/bundle/spec.json"
"$fixture" inspect "$root" "$job" rolled-back
# A completed rollback is independently admissible at service startup.
check_startup stable
cmp "$root/music/track.flac" "$root/remote/track.flac"
[ ! -e "$root/music.backup" ]
! findmnt -rn --mountpoint "$root/music"
[ "$(readlink /etc/systemd/system/jellyfin.service)" = /dev/null ]
[ "$(findmnt -rn --mountpoint "$root/cache" -o SOURCE,FSTYPE)" = "slipmat-cache-$job tmpfs" ]
umount "$root/cache"
! findmnt -rn --mountpoint "$root/cache"
printf 'VM fixture passed: %s %s\n' "$root" "$job"
