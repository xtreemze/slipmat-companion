#!/bin/sh
# SIGKILL only a synthetic runner on a fresh disposable systemd VM.
# The caller MUST NOT supply a production library or live Jellyfin service.
set -eu
if [ "$(id -u)" -ne 0 ] || [ "$#" -ne 5 ]; then
    echo "Usage: $0 FIXTURE_ROOT RUNNER FIXTURE_HELPER RCLONE CUSTODY_HELPER" >&2
    exit 2
fi
root=$1 runner=$2 fixture=$3 rclone=$4 helper=$5
case "$root" in /mnt/slipmat-maintenance-fixture-[a-zA-Z0-9_-]*) ;; *) echo "Invalid fixture root" >&2; exit 2;; esac
case "$root" in *[!a-zA-Z0-9_/-]*|*..*) echo "Unsafe fixture root" >&2; exit 2;; esac
for path in "$runner" "$fixture" "$rclone" "$helper"; do
    case "$path" in /*) ;; *) echo "Absolute tool paths required" >&2; exit 2;; esac
    case "$path" in *[!a-zA-Z0-9_./-]*|*..*) echo "Unsafe tool path" >&2; exit 2;; esac
done
[ ! -e "$root" ] && [ ! -L "$root" ] || { echo "Fixture must be new" >&2; exit 2; }
[ "$(readlink /etc/systemd/system/jellyfin.service)" = /dev/null ] || exit 2
[ ! -e /run/systemd/system/jellyfin.service ] || exit 2
[ ! -e /usr/lib/systemd/system/jellyfin.service ] && [ ! -e /lib/systemd/system/jellyfin.service ] || exit 2
[ "$(systemctl show jellyfin.service --property=ActiveState --value)" = inactive ] || exit 2
marker_dir=/run/slipmat-vfs-proof-interrupted-check
marker=$marker_dir/active.pid
wrapper=$rclone-interruption-fixture
[ ! -e "$wrapper" ] && [ ! -e "$marker_dir" ] || { echo "Fixture signals already exist" >&2; exit 2; }
# The checked wrapper is exclusively test-owned. Its hash enters the immutable
# fixture specification. It only blocks the verifier's read-only check call.
cat > "$wrapper" <<EOF
#!/bin/sh
if [ "\$1" = check ]; then
    printf '%s\n' "\$\$" > "$marker"
    exec /usr/bin/sleep 60
fi
exec "$rclone" "\$@"
EOF
chmod 0755 "$wrapper"
chown root:root "$wrapper"
install -d -o 65534 -g 65534 -m 0700 "$marker_dir"
runner_pid=''
verifier_pid=''
mounted=0
cleanup() {
    if [ -n "$runner_pid" ]; then
        kill -KILL "$runner_pid" 2>/dev/null || :
        wait "$runner_pid" 2>/dev/null || :
    fi
    if [ -n "$verifier_pid" ]; then
        if [ "$(readlink "/proc/$verifier_pid/exe" 2>/dev/null || :)" = /usr/bin/sleep ]; then
            kill -KILL "$verifier_pid" 2>/dev/null || :
        fi
    fi
    if [ "$mounted" = 1 ]; then umount "$root/cache" || :; fi
}
trap cleanup EXIT HUP INT TERM
job=$("$fixture" init "$root" "$wrapper" "$helper")
case "$job" in ''|*[!0-9a-f]*) echo 'Fixture job ID invalid' >&2; exit 2;; esac
[ "$(printf %s "$job" | wc -c)" -eq 32 ] || exit 2
mount -t tmpfs -o size=32m,mode=0755 "slipmat-kill-cache-$job" "$root/cache"
mounted=1
[ "$(stat -c %d "$root/cache")" != "$(stat -c %d "$root/music")" ]
"$runner" prepare "$root/journal" "$job" "$root/draft.json" "$root/bundle/spec.json" &
runner_pid=$!
# Wait for the actual unprivileged rclone-check process, never for a timer-only
# approximation of the transaction phase. The marker does not grant authority.
count=0
while [ ! -s "$marker" ] && [ "$count" -lt 400 ]; do
    kill -0 "$runner_pid" 2>/dev/null || { echo 'Preparation exited before verification marker' >&2; exit 1; }
    sleep 0.05
    count=$((count + 1))
done
[ -s "$marker" ] || { echo 'Verification marker was not created' >&2; exit 1; }
verifier_pid=$(cat "$marker")
case "$verifier_pid" in ''|*[!0-9]*) echo 'Invalid test verifier PID' >&2; exit 1;; esac
[ "$(readlink "/proc/$verifier_pid/exe")" = /usr/bin/sleep ] || { echo 'Unexpected verifier executable' >&2; exit 1; }
# Confirm the verifier is the disposable test process, not a privileged actor.
[ "$(stat -c %u "/proc/$verifier_pid")" = 65534 ] || { echo 'Unexpected verifier owner' >&2; exit 1; }
kill -KILL "$runner_pid"
if wait "$runner_pid"; then echo 'SIGKILL was ignored' >&2; exit 1; else result=$?; fi
runner_pid=''
[ "$result" -eq 137 ] || { echo 'Runner did not exit by SIGKILL' >&2; exit 1; }
# A fresh process must reacquire the canonical journal lease. It MUST observe
# the original local source and cloud copy, without promotion or rollback intent.
"$fixture" inspect-admitted "$root" "$job"
[ ! -e "$root/music.backup" ]
[ -e "$root/music/track.flac" ]
[ ! -e "$root/music.slipmat-rclone-cloud-mount.json" ]
! findmnt -rn --mountpoint "$root/music"
[ "$(systemctl show jellyfin.service --property=ActiveState --value)" = inactive ]
# The stopped verifier is controlled test data, not part of the transaction.
kill -KILL "$verifier_pid"
verifier_pid=''
umount "$root/cache"
mounted=0
printf 'VM interrupted-verification proof passed: %s %s\n' "$root" "$job"
