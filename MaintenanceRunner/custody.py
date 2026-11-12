#!/usr/bin/env python3
"""Linux descriptor-pinned directory rename; caller must hold journal lease and barrier."""
import ctypes
import errno
import os
import stat
import sys

class CustodyError(Exception):
    pass


def _leaf(name):
    if not name or name in ('.', '..') or '/' in name or '\x00' in name:
        raise CustodyError('invalid-leaf')


def _trusted_ancestors(path):
    current = '/'
    for component in path.strip('/').split('/'):
        if not component:
            continue
        current = os.path.join(current, component)
        entry = os.lstat(current)
        if not stat.S_ISDIR(entry.st_mode) or entry.st_uid != 0 or entry.st_mode & 0o022:
            raise CustodyError('parent-not-exclusively-root-controlled')


def _fd_mount_id(fd):
    with open('/proc/self/fdinfo/' + str(fd), encoding='ascii') as stream:
        for line in stream:
            if line.startswith('mnt_id:'):
                try:
                    return int(line.split(':', 1)[1].strip())
                except ValueError:
                    break
    raise CustodyError('mount-identity-unavailable')


def inspect_empty_mountpoint(parent, source, expected_device, expected_mount_id, expected_inode=None):
    """Return descriptor-pinned identity only for the same private, empty, unmounted directory."""
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise CustodyError('linux-root-required')
    _leaf(source)
    if os.path.realpath(parent) != parent or expected_device <= 0 or expected_mount_id <= 0:
        raise CustodyError('mountpoint-spec-invalid')
    _trusted_ancestors(parent)
    parent_fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
    try:
        parent_stat = os.fstat(parent_fd)
        if parent_stat.st_uid != 0 or parent_stat.st_mode & 0o022:
            raise CustodyError('parent-not-exclusively-root-controlled')
        try:
            source_fd = os.open(source, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=parent_fd)
        except FileNotFoundError:
            raise CustodyError('mountpoint-missing') from None
        try:
            source_stat = os.fstat(source_fd)
            mount_id = _fd_mount_id(source_fd)
            if (source_stat.st_dev != expected_device or source_stat.st_uid != 0 or source_stat.st_mode & 0o022
                    or mount_id != expected_mount_id or mount_id != _fd_mount_id(parent_fd)):
                raise CustodyError('mountpoint-identity-mismatch')
            if expected_inode is not None and source_stat.st_ino != expected_inode:
                raise CustodyError('mountpoint-identity-mismatch')
            if os.listdir(source_fd):
                raise CustodyError('mountpoint-not-empty')
            named = os.stat(source, dir_fd=parent_fd, follow_symlinks=False)
            if not stat.S_ISDIR(named.st_mode) or (named.st_dev, named.st_ino) != (source_stat.st_dev, source_stat.st_ino):
                raise CustodyError('mountpoint-identity-mismatch')
            return {'path': os.path.join(parent, source), 'device': source_stat.st_dev,
                    'inode': source_stat.st_ino, 'mountId': mount_id}
        finally:
            os.close(source_fd)
    finally:
        os.close(parent_fd)


def create_empty_mountpoint(parent, source, expected_device, expected_mount_id=None):
    """Create one private empty mountpoint without replacing any existing path."""
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise CustodyError('linux-root-required')
    _leaf(source)
    if os.path.realpath(parent) != parent or expected_device <= 0:
        raise CustodyError('mountpoint-spec-invalid')
    _trusted_ancestors(parent)
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
    parent_fd = os.open(parent, flags)
    try:
        parent_stat = os.fstat(parent_fd)
        if parent_stat.st_uid != 0 or parent_stat.st_mode & 0o022 or parent_stat.st_dev != expected_device:
            raise CustodyError('parent-identity-mismatch')
        actual_mount_id = _fd_mount_id(parent_fd)
        if expected_mount_id is not None and actual_mount_id != expected_mount_id:
            raise CustodyError('mountpoint-identity-mismatch')
        try:
            os.mkdir(source, 0o755, dir_fd=parent_fd)
        except FileExistsError:
            raise CustodyError('mountpoint-already-present') from None
        os.fsync(parent_fd)
    finally:
        os.close(parent_fd)
    return inspect_empty_mountpoint(parent, source, expected_device, actual_mount_id)


def rename_directory(parent, source, destination, expected_device, expected_inode):
    """Move a directory without replacing any target under a root-controlled parent.

    The adapter holds a descriptor to the parent and checks source identity and device.
    The caller must hold the host-wide lease and verify the service barrier immediately
    before and after this call. A crash after rename is resolved by journal observation.
    """
    if sys.platform != 'linux':
        raise CustodyError('linux-required')
    if os.geteuid() != 0:
        raise CustodyError('root-required')
    _leaf(source); _leaf(destination)
    if source == destination or os.path.realpath(parent) != parent:
        raise CustodyError('invalid-parent-or-target')
    _trusted_ancestors(parent)
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
    parent_fd = os.open(parent, flags)
    source_fd = None
    try:
        parent_stat = os.fstat(parent_fd)
        if parent_stat.st_uid != 0 or parent_stat.st_mode & 0o022:
            raise CustodyError('parent-not-exclusively-root-controlled')
        source_fd = os.open(source, flags, dir_fd=parent_fd)
        source_stat = os.fstat(source_fd)
        if not stat.S_ISDIR(source_stat.st_mode) or (source_stat.st_dev, source_stat.st_ino) != (expected_device, expected_inode):
            raise CustodyError('source-identity-changed')
        if source_stat.st_uid != 0 or source_stat.st_mode & 0o022:
            raise CustodyError('source-not-exclusively-root-controlled')
        if parent_stat.st_dev != source_stat.st_dev:
            raise CustodyError('cross-device-move-forbidden')
        # Recheck the name while the parent is held; non-root users cannot replace it.
        current = os.stat(source, dir_fd=parent_fd, follow_symlinks=False)
        if (current.st_dev, current.st_ino) != (expected_device, expected_inode):
            raise CustodyError('source-name-changed')
        libc = ctypes.CDLL(None, use_errno=True)
        fn = getattr(libc, 'renameat2', None)
        if fn is None:
            raise CustodyError('renameat2-unavailable')
        fn.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
        fn.restype = ctypes.c_int
        if fn(parent_fd, os.fsencode(source), parent_fd, os.fsencode(destination), 1) != 0:
            error = ctypes.get_errno()
            if error == errno.EEXIST:
                raise CustodyError('destination-exists')
            raise OSError(error, os.strerror(error))
        # Failure here means the outcome is uncertain; never roll back automatically.
        os.fsync(parent_fd)
    finally:
        if source_fd is not None:
            os.close(source_fd)
        os.close(parent_fd)


def replace_config(parent, name, expected_hash, replacement, target_hash):
    """One bounded config replacement. Caller holds the job lease and host barrier.

    Root-controlled ancestors are mandatory: POSIX rename cannot compare a file hash
    atomically against a concurrent same-UID writer. Replay recognizes target bytes.
    """
    import hashlib
    import secrets
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise CustodyError('linux-root-required')
    _leaf(name)
    if os.path.realpath(parent) != parent:
        raise CustodyError('invalid-parent')
    _trusted_ancestors(parent)
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
    parent_fd = os.open(parent, flags)
    temp_name = '.slipmat-' + secrets.token_hex(16) + '.tmp'
    try:
        path_fd = os.open(name, os.O_PATH | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=parent_fd)
        try:
            info = os.fstat(path_fd)
            if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
                raise CustodyError('config-not-root-controlled')
            fd = os.open('/proc/self/fd/' + str(path_fd), os.O_RDONLY | os.O_CLOEXEC | os.O_NONBLOCK)
        finally:
            os.close(path_fd)
        try:
            with os.fdopen(fd, 'rb', closefd=False) as stream:
                current = stream.read(4 * 1024 * 1024 + 1)
            if len(current) > 4 * 1024 * 1024:
                raise CustodyError('config-too-large')
            digest = hashlib.sha256(current).hexdigest()
            if digest == target_hash.lower():
                return
            if digest != expected_hash.lower() or hashlib.sha256(replacement).hexdigest() != target_hash.lower():
                raise CustodyError('config-hash-mismatch')
            temp_fd = os.open(temp_name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                              stat.S_IMODE(info.st_mode), dir_fd=parent_fd)
            try:
                os.fchown(temp_fd, info.st_uid, info.st_gid)
                os.fchmod(temp_fd, stat.S_IMODE(info.st_mode))
                with os.fdopen(temp_fd, 'wb', closefd=False) as temp:
                    temp.write(replacement)
                    temp.flush()
                    os.fsync(temp_fd)
            finally:
                os.close(temp_fd)
            named = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
            if (named.st_dev, named.st_ino) != (info.st_dev, info.st_ino):
                raise CustodyError('config-identity-changed')
            os.rename(temp_name, name, src_dir_fd=parent_fd, dst_dir_fd=parent_fd)
            os.fsync(parent_fd)
        finally:
            os.close(fd)
    finally:
        # Only our uncommitted scratch file is removed; committed config is retained.
        try:
            os.unlink(temp_name, dir_fd=parent_fd)
        except FileNotFoundError:
            pass
        os.close(parent_fd)



def remove_empty_projection(parent, source, marker, remote_name, remote_type, remote_path):
    """Remove only an empty unmounted managed mountpoint and its exact adjacent marker."""
    import json
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise CustodyError('linux-root-required')
    _leaf(source); _leaf(marker)
    if marker != source + '.slipmat-rclone-cloud-mount.json' or os.path.realpath(parent) != parent:
        raise CustodyError('projection-binding-invalid')
    _trusted_ancestors(parent)
    parent_fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
    try:
        try:
            marker_fd = os.open(marker, os.O_PATH | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=parent_fd)
        except FileNotFoundError:
            try:
                os.stat(source, dir_fd=parent_fd, follow_symlinks=False)
            except FileNotFoundError:
                return
            raise CustodyError('projection-marker-missing')
        try:
            marker_stat = os.fstat(marker_fd)
            if not stat.S_ISREG(marker_stat.st_mode) or marker_stat.st_uid != 0 or marker_stat.st_mode & 0o022:
                raise CustodyError('projection-marker-untrusted')
            with open('/proc/self/fd/' + str(marker_fd), 'rb') as stream:
                raw = stream.read(16385)
            if len(raw) > 16384:
                raise CustodyError('projection-marker-invalid')
            data = json.loads(raw)
            if data != {'Version': 2, 'RemoteName': remote_name, 'RemoteType': remote_type, 'RemotePath': remote_path}:
                raise CustodyError('projection-marker-mismatch')
            try:
                source_fd = os.open(source, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=parent_fd)
            except FileNotFoundError:
                source_fd = None
            if source_fd is not None:
                try:
                    source_stat = os.fstat(source_fd)
                    if source_stat.st_uid != 0 or source_stat.st_mode & 0o022 or os.listdir(source_fd):
                        raise CustodyError('projection-not-empty-or-untrusted')
                    def mount_id(fd):
                        with open('/proc/self/fdinfo/' + str(fd), encoding='ascii') as stream:
                            for line in stream:
                                if line.startswith('mnt_id:'):
                                    return int(line.split(':', 1)[1].strip())
                        raise CustodyError('mount-identity-unavailable')
                    if mount_id(source_fd) != mount_id(parent_fd):
                        raise CustodyError('projection-still-mounted')
                    os.rmdir(source, dir_fd=parent_fd)
                    os.fsync(parent_fd)
                finally:
                    os.close(source_fd)
            current = os.stat(marker, dir_fd=parent_fd, follow_symlinks=False)
            if (current.st_dev, current.st_ino) != (marker_stat.st_dev, marker_stat.st_ino):
                raise CustodyError('projection-marker-changed')
            os.unlink(marker, dir_fd=parent_fd)
            os.fsync(parent_fd)
        finally:
            os.close(marker_fd)
    finally:
        os.close(parent_fd)

def probe_noreplace(parent):
    """Probe only newly-created empty metadata directories on the actual filesystem.

    No media path is accepted. Unsupported no-replace or directory fsync semantics
    block preparation before the authoritative source can be moved.
    """
    import tempfile
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise CustodyError('linux-root-required')
    if os.path.realpath(parent) != parent:
        raise CustodyError('invalid-parent-or-target')
    _trusted_ancestors(parent)
    probe = tempfile.mkdtemp(prefix='.slipmat-noreplace-probe-', dir=parent)
    identities = set()
    original = os.lstat(probe)
    try:
        for name in ('source', 'occupied'):
            os.mkdir(os.path.join(probe, name), 0o700)
            entry = os.lstat(os.path.join(probe, name))
            identities.add((entry.st_dev, entry.st_ino))
        source = os.lstat(os.path.join(probe, 'source'))
        occupied = os.lstat(os.path.join(probe, 'occupied'))
        try:
            rename_directory(probe, 'source', 'occupied', source.st_dev, source.st_ino)
        except CustodyError as error:
            if str(error) != 'destination-exists':
                raise
        else:
            raise CustodyError('filesystem-noreplace-unsupported')
        if (os.lstat(os.path.join(probe, 'occupied')).st_dev,
                os.lstat(os.path.join(probe, 'occupied')).st_ino) != (occupied.st_dev, occupied.st_ino):
            raise CustodyError('filesystem-noreplace-unsupported')
        rename_directory(probe, 'source', 'moved', source.st_dev, source.st_ino)
        rename_directory(probe, 'moved', 'source', source.st_dev, source.st_ino)
    finally:
        current = os.lstat(probe)
        if (current.st_dev, current.st_ino) != (original.st_dev, original.st_ino):
            raise CustodyError('probe-cleanup-ambiguous')
        for name in os.listdir(probe):
            entry = os.lstat(os.path.join(probe, name))
            if name not in ('source', 'occupied', 'moved') or not stat.S_ISDIR(entry.st_mode) or (entry.st_dev, entry.st_ino) not in identities:
                raise CustodyError('probe-cleanup-ambiguous')
        for name in os.listdir(probe):
            os.rmdir(os.path.join(probe, name))  # Only this probe's verified empty directories.
        os.rmdir(probe)


def main():
    import base64
    import json
    try:
        request = json.loads(sys.stdin.buffer.read(6 * 1024 * 1024 + 1))
        action = request['action']
        result = {'status': 'ok'}
        if action == 'probe-noreplace':
            probe_noreplace(request['parent'])
        elif action == 'rename':
            rename_directory(request['parent'], request['source'], request['destination'], request['device'], request['inode'])
        elif action == 'create-empty-mountpoint':
            result['identity'] = create_empty_mountpoint(request['parent'], request['source'], request['device'], request['mountId'])
        elif action == 'verify-empty-mountpoint':
            result['identity'] = inspect_empty_mountpoint(request['parent'], request['source'], request['device'], request['mountId'], request['inode'])
        elif action == 'remove-empty-projection':
            remove_empty_projection(request['parent'], request['source'], request['marker'], request['remoteName'], request['remoteType'], request['remotePath'])
        elif action == 'replace-config':
            replacement = base64.b64decode(request['replacementBase64'], validate=True)
            if len(replacement) > 4 * 1024 * 1024:
                raise CustodyError('config-too-large')
            replace_config(request['parent'], request['name'], request['beforeHash'], replacement, request['afterHash'])
        else:
            raise CustodyError('unknown-action')
        print(json.dumps(result, separators=(',', ':')))
        return 0
    except (KeyError, ValueError, TypeError, OSError, CustodyError):
        print('{"status":"blocked"}')
        return 2


if __name__ == '__main__':
    sys.exit(main())
