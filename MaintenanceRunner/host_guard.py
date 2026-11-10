#!/usr/bin/env python3
"""Read-only host preflight; no migration or recovery action is implemented."""
import argparse
import json
import os
import pathlib
import re
import stat
import subprocess
import sys
from typing import NamedTuple


class GuardError(Exception):
    pass


class ServiceState(NamedTuple):
    enabled: str
    active: str
    main_pid: int
    substate: str = 'dead'
    load_state: str = 'masked'


class Mount(NamedTuple):
    mount_id: int
    mountpoint: str
    filesystem: str
    source: str


def require_stopped_masked(state):
    if state != ServiceState('masked', 'inactive', 0, 'dead', 'masked'):
        raise GuardError('service-not-inactive-and-persistently-masked')


def require_namespace(actual, host):
    if not re.fullmatch(r'mnt:\[\d+\]', actual) or actual != host:
        raise GuardError('mount-namespace-mismatch')


def unescape_mountinfo(value):
    return re.sub(r'\\([0-7]{3})', lambda match: chr(int(match.group(1), 8)), value)


def select_mount(text, mount_id):
    for line in text.splitlines():
        fields = line.split()
        try:
            separator = fields.index('-')
            if int(fields[0]) == mount_id and separator >= 6 and len(fields) >= separator + 4:
                return Mount(mount_id, unescape_mountinfo(fields[4]), fields[separator + 1], unescape_mountinfo(fields[separator + 2]))
        except (ValueError, IndexError):
            raise GuardError('invalid-mountinfo') from None
    raise GuardError('mount-instance-missing')


def require_mount(mount, mountpoint, filesystem, source):
    if (mount.mountpoint, mount.filesystem, mount.source) != (mountpoint, filesystem, source):
        raise GuardError('mount-identity-mismatch')


def execution_state():
    # No adapter may call the transaction planner or perform effects until descriptor-
    # pinned operations, persistent lease, journal durability, and recovery are wired.
    return 'blocked-runner-adapter-unimplemented'


def systemctl(*args):
    result = subprocess.run(['/usr/bin/systemctl', *args], stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                            timeout=10, text=True, env={'PATH': '/usr/bin:/bin', 'LC_ALL': 'C'})
    if result.returncode not in (0, 1):
        raise GuardError('systemd-query-failed')
    return result.stdout.strip()


def service_state():
    enabled = systemctl('is-enabled', 'jellyfin.service')
    output = systemctl('show', 'jellyfin.service', '--property=ActiveState', '--property=SubState', '--property=LoadState', '--property=MainPID')
    fields = dict(line.split('=', 1) for line in output.splitlines() if '=' in line)
    try:
        return ServiceState(enabled, fields['ActiveState'], int(fields['MainPID']), fields['SubState'], fields['LoadState'])
    except (KeyError, ValueError):
        raise GuardError('systemd-state-invalid') from None


def fd_mount_id(fd):
    data = pathlib.Path(f'/proc/self/fdinfo/{fd}').read_text()
    match = re.search(r'^mnt_id:\s*(\d+)\s*$', data, re.MULTILINE)
    if not match:
        raise GuardError('fd-mount-id-unavailable')
    return int(match.group(1))


def host_preflight(path, device, inode, expected_mount_id, mountpoint, filesystem, mount_source, expected_boot=None, expected_namespace=None):
    if os.geteuid() != 0:
        raise GuardError('root-required')
    if not sys.platform.startswith('linux'):
        raise GuardError('linux-required')
    if not os.path.isdir('/run/systemd/system'):
        raise GuardError('systemd-unavailable')
    for directory in ('/etc', '/etc/systemd', '/etc/systemd/system'):
        entry = os.lstat(directory)
        if not stat.S_ISDIR(entry.st_mode) or entry.st_uid != 0 or entry.st_mode & 0o022:
            raise GuardError('systemd-mask-parent-untrusted')
    mask = '/etc/systemd/system/jellyfin.service'
    if os.lstat(mask).st_uid != 0 or os.readlink(mask) != '/dev/null':
        raise GuardError('persistent-service-mask-unverified')
    require_stopped_masked(service_state())
    actual_namespace = os.readlink('/proc/self/ns/mnt')
    require_namespace(actual_namespace, os.readlink('/proc/1/ns/mnt'))
    if expected_namespace is not None and actual_namespace != expected_namespace:
        raise GuardError('mount-namespace-changed')
    if expected_boot is not None and pathlib.Path('/proc/sys/kernel/random/boot_id').read_text().strip() != expected_boot:
        raise GuardError('boot-changed')
    if not os.path.isabs(path) or os.path.realpath(path) != path:
        raise GuardError('source-path-not-canonical')
    ancestor = '/'
    for component in pathlib.Path(path).parts[1:-1]:
        ancestor = os.path.join(ancestor, component)
        entry = os.lstat(ancestor)
        if not stat.S_ISDIR(entry.st_mode) or entry.st_uid != 0 or entry.st_mode & 0o022:
            raise GuardError('source-parent-untrusted')
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW
    fd = os.open(path, flags)
    try:
        source_stat = os.fstat(fd)
        if source_stat.st_uid != 0 or source_stat.st_mode & 0o022 or (source_stat.st_dev, source_stat.st_ino) != (device, inode):
            raise GuardError('source-identity-mismatch')
        mount_id = fd_mount_id(fd)
        if mount_id != expected_mount_id:
            raise GuardError('mount-instance-changed')
        mount = select_mount(pathlib.Path('/proc/self/mountinfo').read_text(), mount_id)
        require_mount(mount, mountpoint, filesystem, mount_source)
    finally:
        os.close(fd)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True)
    parser.add_argument('--device', required=True, type=int)
    parser.add_argument('--inode', required=True, type=int)
    parser.add_argument('--mount-id', required=True, type=int)
    parser.add_argument('--mountpoint', required=True)
    parser.add_argument('--filesystem', required=True)
    parser.add_argument('--mount-source', required=True)
    parser.add_argument('--boot-id')
    parser.add_argument('--mount-namespace')
    parser.add_argument('--verify-barrier', action='store_true')
    args = parser.parse_args()
    try:
        host_preflight(args.source, args.device, args.inode, args.mount_id,
                       args.mountpoint, args.filesystem, args.mount_source, args.boot_id, args.mount_namespace)
        print(json.dumps({'status': 'preflight-only' if args.verify_barrier else execution_state()}))
        if args.verify_barrier:
            return 0
    except (GuardError, OSError, subprocess.SubprocessError) as exc:
        code = str(exc) if isinstance(exc, GuardError) else 'host-preflight-unavailable'
        print(json.dumps({'status': 'blocked', 'code': code}))
    return 2


if __name__ == '__main__':
    sys.exit(main())
