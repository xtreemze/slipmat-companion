#!/bin/sh
# Disposable VM only. Requires a fresh dedicated image and mountpoint.
set -eu
[ "$(id -u)" = 0 ] || { echo 'root required' >&2; exit 1; }
[ "$#" = 1 ] && [ -f "$1" ] || { echo 'usage: script custody.py' >&2; exit 1; }
image=/var/lib/slipmat-ntfs3-proof-3.img
mountpoint=/srv/slipmat-ntfs3-proof-3
[ ! -e "$image" ] && [ ! -e "$mountpoint" ] || { echo 'proof paths already exist; refusing' >&2; exit 1; }
(umask 077; set -C; : > "$image")
mkdir -m 755 "$mountpoint"
truncate -s 128M "$image"
mkfs.ntfs -F -Q "$image" >/dev/null
loop=$(losetup --find --show "$image")
mounted=0
cleanup() {
  if [ "$mounted" = 1 ]; then umount "$mountpoint" || { echo 'unmount failed; loop retained' >&2; return 1; }; fi
  losetup -d "$loop"
}
trap cleanup EXIT HUP INT TERM
mount -t ntfs3 -o uid=0,gid=0,dmask=022,fmask=133 "$loop" "$mountpoint"
mounted=1
# The formatted NTFS volume root initially has mode 0777 despite dmask.
chmod 755 "$mountpoint"
findmnt -no SOURCE,FSTYPE,OPTIONS "$mountpoint"
python3 - "$1" "$mountpoint" <<'PY'
import importlib.util, os, pathlib, sys
spec = importlib.util.spec_from_file_location('custody', sys.argv[1])
custody = importlib.util.module_from_spec(spec); spec.loader.exec_module(custody)
p = pathlib.Path(sys.argv[2]); src = p / 'source'; occupied = p / 'occupied'; backup = p / 'backup'
src.mkdir(); (src / 'track.flac').write_bytes(b'disposable-ntfs3-proof\x00')
occupied.mkdir()
identity = src.stat()
try:
    custody.rename_directory(str(p), 'source', 'occupied', identity.st_dev, identity.st_ino)
except custody.CustodyError as e:
    assert str(e) == 'destination-exists', repr(e)
else:
    raise AssertionError('occupied destination accepted')
assert (src / 'track.flac').read_bytes() == b'disposable-ntfs3-proof\x00'
assert occupied.is_dir() and not list(occupied.iterdir())
print('empty occupied destination rejected; both directories intact')
custody.rename_directory(str(p), 'source', 'backup', identity.st_dev, identity.st_ino)
assert not src.exists() and backup.stat().st_ino == identity.st_ino
assert (backup / 'track.flac').read_bytes() == b'disposable-ntfs3-proof\x00'
print('forward rename and directory fsync succeeded; inode and bytes preserved')
custody.rename_directory(str(p), 'backup', 'source', identity.st_dev, identity.st_ino)
assert not backup.exists() and src.stat().st_ino == identity.st_ino
assert (src / 'track.flac').read_bytes() == b'disposable-ntfs3-proof\x00'
print('restore rename and directory fsync succeeded; inode and bytes preserved')
print('mountpoint and source modes:', oct(p.stat().st_mode & 0o777), oct(src.stat().st_mode & 0o777))
PY
