#!/usr/bin/env python3
"""Linux descriptor-pinned renames; caller must hold journal lease and barrier."""
import ctypes
import errno
import hashlib
import json
import os
import stat
import sys
import unicodedata

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


def _rename_noreplace(parent_fd, source, destination):
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


def _expected_identity(value, include_kind=False):
    if not isinstance(value, dict) or set(value) != ({'device', 'inode', 'mountId', 'kind'} if include_kind
                                                   else {'device', 'inode', 'mountId'}):
        raise CustodyError('source-repair-identity-invalid')
    if any(type(value[field]) is not int or value[field] <= 0 for field in ('device', 'inode', 'mountId')):
        raise CustodyError('source-repair-identity-invalid')
    if include_kind and value['kind'] not in ('file', 'directory'):
        raise CustodyError('source-repair-kind-invalid')


def _source_repair_leaf(value):
    if not isinstance(value, str):
        raise CustodyError('source-repair-leaf-invalid')
    _leaf(value)
    if ('\\' in value or ':' in value or value[-1] in (' ', '.')
            or not unicodedata.is_normalized('NFC', value)
            or any(unicodedata.category(character) == 'Cc' for character in value)):
        raise CustodyError('source-repair-leaf-invalid')


def _exact_name_in_directory(directory_fd, name):
    # A descriptor-relative listing exposes the stored spelling even on casefold mounts.
    # Bound the scan instead of allocating an arbitrarily large directory listing.
    scan_fd = os.open('.', os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                      dir_fd=directory_fd)
    try:
        with os.scandir(scan_fd) as entries:
            for index, entry in enumerate(entries):
                if index >= 100_000:
                    raise CustodyError('source-repair-directory-too-large')
                if entry.name == name:
                    return True
        return False
    finally:
        os.close(scan_fd)


def _open_source_repair_parent(parent):
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
    current_fd = os.open('/', flags)
    try:
        for component in parent.split('/')[1:]:
            info = os.fstat(current_fd)
            if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
                raise CustodyError('parent-not-exclusively-root-controlled')
            if not component:
                continue
            if not _exact_name_in_directory(current_fd, component):
                raise CustodyError('source-repair-parent-spelling-changed')
            next_fd = os.open(component, flags, dir_fd=current_fd)
            os.close(current_fd)
            current_fd = next_fd
        return current_fd
    except BaseException:
        os.close(current_fd)
        raise


def rename_source_entry(parent, source, destination, expected_parent, expected_entry):
    """Atomically move one regular file or directory under one pinned root-controlled parent.

    Caller owns the whole-job journal lease and persistent stopped-service barrier. The
    expected identities must come from a separately reviewed plan, not this function.
    A failed parent fsync leaves an uncertain physical outcome for journal reconciliation.
    """
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise CustodyError('linux-root-required')
    _source_repair_leaf(source); _source_repair_leaf(destination)
    _expected_identity(expected_parent)
    _expected_identity(expected_entry, include_kind=True)
    if (source == destination or not isinstance(parent, str)
            or not os.path.isabs(parent) or os.path.realpath(parent) != parent):
        raise CustodyError('invalid-parent-or-target')
    parent_fd = _open_source_repair_parent(parent)
    try:
        parent_stat = os.fstat(parent_fd)
        parent_mount = _fd_mount_id(parent_fd)
        if (not stat.S_ISDIR(parent_stat.st_mode) or parent_stat.st_uid != 0 or parent_stat.st_mode & 0o022):
            raise CustodyError('parent-not-exclusively-root-controlled')
        if ((parent_stat.st_dev, parent_stat.st_ino, parent_mount) !=
                (expected_parent['device'], expected_parent['inode'], expected_parent['mountId'])):
            raise CustodyError('source-repair-parent-identity-changed')
        if not _exact_name_in_directory(parent_fd, source):
            raise CustodyError('source-repair-source-spelling-changed')

        # O_PATH opens even a FIFO without blocking; O_NOFOLLOW pins a symlink itself,
        # which fails the regular-file/directory type check below.
        source_fd = os.open(source, os.O_PATH | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=parent_fd)
        try:
            info = os.fstat(source_fd)
            mount = _fd_mount_id(source_fd)
            kind = expected_entry['kind']
            if not (stat.S_ISREG(info.st_mode) if kind == 'file' else stat.S_ISDIR(info.st_mode)):
                raise CustodyError('source-repair-kind-changed')
            if info.st_uid != 0 or info.st_mode & 0o022:
                raise CustodyError('source-not-exclusively-root-controlled')
            if (info.st_dev, info.st_ino, mount) != (expected_entry['device'], expected_entry['inode'], expected_entry['mountId']):
                raise CustodyError('source-repair-entry-identity-changed')
            if info.st_dev != parent_stat.st_dev or mount != parent_mount:
                raise CustodyError('cross-device-move-forbidden')
            named = os.stat(source, dir_fd=parent_fd, follow_symlinks=False)
            if (named.st_dev, named.st_ino, stat.S_IFMT(named.st_mode)) != (info.st_dev, info.st_ino, stat.S_IFMT(info.st_mode)):
                raise CustodyError('source-name-changed')
            if not _exact_name_in_directory(parent_fd, source):
                raise CustodyError('source-repair-source-spelling-changed')
            _rename_noreplace(parent_fd, source, destination)
            try:
                os.fsync(parent_fd)
            except OSError as exc:
                raise CustodyError('source-repair-commit-uncertain') from exc
        finally:
            os.close(source_fd)
    finally:
        os.close(parent_fd)


def _preservation_manifest(data, expected_sha256):
    if (not isinstance(data, bytes) or len(data) > 32 * 1024 * 1024
            or not isinstance(expected_sha256, str) or len(expected_sha256) != 64
            or any(character not in '0123456789abcdefABCDEF' for character in expected_sha256)
            or hashlib.sha256(data).hexdigest() != expected_sha256.lower()):
        raise CustodyError('source-repair-preservation-manifest-invalid')

    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise CustodyError('source-repair-preservation-manifest-invalid')
            result[key] = value
        return result

    try:
        manifest = json.loads(data.decode('utf-8'), object_pairs_hook=unique_object,
                              parse_constant=lambda _: (_ for _ in ()).throw(ValueError()))
    except (UnicodeError, ValueError, TypeError, RecursionError) as exc:
        raise CustodyError('source-repair-preservation-manifest-invalid') from exc
    if (not isinstance(manifest, dict)
            or set(manifest) != {'schemaVersion', 'fileCount', 'totalBytes', 'entries'}
            or type(manifest['schemaVersion']) is not int or manifest['schemaVersion'] != 1
            or type(manifest['fileCount']) is not int or not 0 <= manifest['fileCount'] <= 200_000
            or type(manifest['totalBytes']) is not int or not 0 <= manifest['totalBytes'] <= 16 * 1024 ** 4
            or not isinstance(manifest['entries'], list) or len(manifest['entries']) > 200_000):
        raise CustodyError('source-repair-preservation-manifest-invalid')
    entries = {}
    children = {'': {}}
    file_count = total_bytes = 0
    for item in manifest['entries']:
        if not isinstance(item, dict) or item.get('kind') not in ('file', 'directory'):
            raise CustodyError('source-repair-preservation-manifest-invalid')
        if (set(item) != ({'path', 'kind', 'size', 'sha256'} if item['kind'] == 'file'
                          else {'path', 'kind'}) or not isinstance(item['path'], str)
                or not 0 < len(item['path']) <= 4096 or item['path'] in entries):
            raise CustodyError('source-repair-preservation-manifest-invalid')
        parts = item['path'].split('/')
        if len(parts) > 128:
            raise CustodyError('source-repair-preservation-manifest-invalid')
        try:
            for part in parts:
                _source_repair_leaf(part)
            item['path'].encode('utf-8')
        except (CustodyError, UnicodeError) as exc:
            raise CustodyError('source-repair-preservation-manifest-invalid') from exc
        if item['kind'] == 'file':
            if (type(item['size']) is not int or not 0 <= item['size'] <= 16 * 1024 ** 4
                    or not isinstance(item['sha256'], str) or len(item['sha256']) != 64
                    or any(character not in '0123456789abcdef' for character in item['sha256'])):
                raise CustodyError('source-repair-preservation-manifest-invalid')
            file_count += 1
            total_bytes += item['size']
        entries[item['path']] = item
        parent = '/'.join(parts[:-1])
        children.setdefault(parent, {})[parts[-1]] = item
        if item['kind'] == 'directory':
            children.setdefault(item['path'], {})
    if (file_count != manifest['fileCount'] or total_bytes != manifest['totalBytes']
            or total_bytes > 16 * 1024 ** 4
            or any(parent and (parent not in entries or entries[parent]['kind'] != 'directory')
                   for parent in children)):
        raise CustodyError('source-repair-preservation-manifest-invalid')
    return children


def _archive_snapshot(fd):
    info = os.fstat(fd)
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid,
            info.st_size, info.st_mtime_ns, info.st_ctime_ns, _fd_mount_id(fd), info.st_nlink)


def _archive_names(directory_fd):
    scan_fd = os.open('.', os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                      dir_fd=directory_fd)
    try:
        result = set()
        with os.scandir(scan_fd) as listing:
            for entry in listing:
                if len(result) >= 100_000 or entry.name in result:
                    raise CustodyError('source-repair-archive-tree-invalid')
                result.add(entry.name)
        return result
    finally:
        os.close(scan_fd)


def _verify_archive_tree(directory_fd, prefix, children, mount_id, observed, read_payloads):
    before = _archive_snapshot(directory_fd)
    if (not stat.S_ISDIR(before[2]) or before[3] != 0 or before[2] & 0o022
            or before[7] != mount_id):
        raise CustodyError('source-repair-archive-tree-invalid')
    wanted = children[prefix]
    if _archive_names(directory_fd) != set(wanted):
        raise CustodyError('source-repair-archive-tree-invalid')
    for name, item in wanted.items():
        path = prefix + '/' + name if prefix else name
        pinned_fd = os.open(name, os.O_PATH | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=directory_fd)
        try:
            snapshot = _archive_snapshot(pinned_fd)
            if (snapshot[3] != 0 or snapshot[2] & 0o022 or snapshot[7] != mount_id
                    or not (stat.S_ISDIR(snapshot[2]) if item['kind'] == 'directory'
                            else stat.S_ISREG(snapshot[2]))
                    or (item['kind'] == 'file' and snapshot[8] != 1)):
                raise CustodyError('source-repair-archive-tree-invalid')
            if read_payloads and observed.get(path) != snapshot:
                raise CustodyError('source-repair-archive-changed')
            if item['kind'] == 'directory':
                flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
            else:
                flags = os.O_RDONLY | os.O_NONBLOCK | os.O_CLOEXEC | os.O_NOFOLLOW
            entry_fd = os.open(name, flags, dir_fd=directory_fd)
            try:
                if _archive_snapshot(entry_fd) != snapshot:
                    raise CustodyError('source-repair-archive-changed')
                if item['kind'] == 'directory':
                    _verify_archive_tree(entry_fd, path, children, mount_id, observed, read_payloads)
                elif read_payloads:
                    if snapshot[4] != item['size']:
                        raise CustodyError('source-repair-archive-content-mismatch')
                    digest = hashlib.sha256()
                    remaining = item['size']
                    while True:
                        chunk = os.read(entry_fd, min(1024 * 1024, remaining + 1))
                        if not chunk:
                            break
                        remaining -= len(chunk)
                        if remaining < 0:
                            raise CustodyError('source-repair-archive-content-mismatch')
                        digest.update(chunk)
                    if remaining != 0 or digest.hexdigest() != item['sha256']:
                        raise CustodyError('source-repair-archive-content-mismatch')
                if _archive_snapshot(entry_fd) != snapshot:
                    raise CustodyError('source-repair-archive-changed')
            finally:
                os.close(entry_fd)
            if not read_payloads:
                observed[path] = snapshot
        finally:
            os.close(pinned_fd)
    if _archive_snapshot(directory_fd) != before:
        raise CustodyError('source-repair-archive-changed')


def verify_source_repair_archive(archive_parent, archive_leaf, expected_parent, expected_archive,
                                 manifest_bytes, expected_manifest_sha256):
    """Read back a complete, immutable archive tree against a separately bound manifest digest.

    The caller must bind the manifest bytes and both identities to the admitted plan and
    hold the archive's exclusive writer barrier. This function never creates an archive.
    """
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise CustodyError('linux-root-required')
    _source_repair_leaf(archive_leaf)
    _expected_identity(expected_parent)
    _expected_identity(expected_archive)
    children = _preservation_manifest(manifest_bytes, expected_manifest_sha256)
    if (not isinstance(archive_parent, str) or not os.path.isabs(archive_parent)
            or os.path.realpath(archive_parent) != archive_parent):
        raise CustodyError('invalid-parent-or-target')
    parent_fd = _open_source_repair_parent(archive_parent)
    try:
        parent = _archive_snapshot(parent_fd)
        if parent[0:2] != (expected_parent['device'], expected_parent['inode']) or parent[7] != expected_parent['mountId']:
            raise CustodyError('source-repair-parent-identity-changed')
        if not _exact_name_in_directory(parent_fd, archive_leaf):
            raise CustodyError('source-repair-archive-spelling-changed')
        archive_fd = os.open(archive_leaf, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                             dir_fd=parent_fd)
        try:
            archive = _archive_snapshot(archive_fd)
            if (archive[0:2] != (expected_archive['device'], expected_archive['inode'])
                    or archive[7] != expected_archive['mountId']):
                raise CustodyError('source-repair-archive-identity-changed')
            observed = {}
            _verify_archive_tree(archive_fd, '', children, archive[7], observed, False)
            _verify_archive_tree(archive_fd, '', children, archive[7], observed, True)
            if _archive_snapshot(archive_fd) != archive:
                raise CustodyError('source-repair-archive-changed')
            if not _exact_name_in_directory(parent_fd, archive_leaf):
                raise CustodyError('source-repair-archive-spelling-changed')
            reopened_fd = os.open(archive_leaf, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                                  dir_fd=parent_fd)
            try:
                if _archive_snapshot(reopened_fd) != archive or _archive_snapshot(parent_fd) != parent:
                    raise CustodyError('source-repair-archive-changed')
            finally:
                os.close(reopened_fd)
            return {'device': archive[0], 'inode': archive[1], 'mountId': archive[7]}
        finally:
            os.close(archive_fd)
    finally:
        os.close(parent_fd)


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
        _rename_noreplace(parent_fd, source, destination)
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
