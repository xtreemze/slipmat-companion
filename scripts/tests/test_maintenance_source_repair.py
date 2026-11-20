"""Disposable Linux custody proof for one exact source-path rename primitive."""
import importlib.util
import base64
import contextlib
import errno
import hashlib
import json
import os
import pathlib
import stat
import subprocess
import sys
import tempfile
import types
import unittest
import uuid
from unittest import mock

MODULE = pathlib.Path(__file__).resolve().parents[2] / 'MaintenanceRunner' / 'custody.py'
spec = importlib.util.spec_from_file_location('custody', MODULE)
custody = importlib.util.module_from_spec(spec)
spec.loader.exec_module(custody)


@unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires disposable Linux root')
class SourceRepairRenameTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(dir='/root')
        self.addCleanup(self.directory.cleanup)
        self.parent = pathlib.Path(self.directory.name)

    def identity(self, path, kind=None):
        flags = os.O_PATH | os.O_CLOEXEC | os.O_NOFOLLOW
        fd = os.open(path, flags)
        try:
            value = os.fstat(fd)
            result = {'device': value.st_dev, 'inode': value.st_ino, 'mountId': custody._fd_mount_id(fd)}
            if kind is not None:
                result['kind'] = kind
            return result
        finally:
            os.close(fd)

    def move(self, source='source', destination='target', *, parent=None, entry=None):
        return custody.rename_source_entry(str(self.parent), source, destination,
                                           parent or self.identity(self.parent),
                                           entry or self.identity(self.parent / source, 'file'))

    def move_request(self, action, **fields):
        request = {
            'action': action, 'parent': str(self.parent), 'source': 'source', 'destination': 'target',
            'parentIdentity': self.identity(self.parent),
            'entryIdentity': self.identity(self.parent / 'source', 'file'),
        }
        return request | fields

    def dispatch(self, request):
        return subprocess.run([sys.executable, '-I', str(MODULE)], input=json.dumps(request),
                              text=True, capture_output=True, timeout=10, check=False)

    def test_observe_and_rename_dispatch_track_forward_and_reverse(self):
        source = self.parent / 'source'
        source.write_bytes(b'version preserved')
        observe = self.move_request('source-repair-observe-move')
        rename = observe | {'action': 'source-repair-rename-move'}
        steps = (
            (observe, {'paths': {'source': 'expected', 'destination': 'missing'}}),
            (rename, {}),
            (observe, {'paths': {'source': 'missing', 'destination': 'expected'}}),
            (rename | {'source': 'target', 'destination': 'source'}, {}),
            (observe, {'paths': {'source': 'expected', 'destination': 'missing'}}),
        )
        for request, fields in steps:
            result = self.dispatch(request)
            self.assertEqual(result.returncode, 0, result.stdout)
            self.assertEqual(json.loads(result.stdout), {'status': 'ok'} | fields)
            self.assertEqual(result.stderr, '')
        self.assertEqual(source.read_bytes(), b'version preserved')

    def test_observe_foreign_inode_symlink_and_fifo_without_following(self):
        source = self.parent / 'source'
        source.write_bytes(b'original')
        observe = self.move_request('source-repair-observe-move')
        target = self.parent / 'target'
        for create in (lambda: target.write_bytes(b'other'), lambda: target.symlink_to('source'),
                       lambda: os.mkfifo(target)):
            with self.subTest(create=create):
                create()
                result = self.dispatch(observe)
                self.assertEqual(result.returncode, 0, result.stdout)
                self.assertEqual(json.loads(result.stdout), {
                    'status': 'ok', 'paths': {'source': 'expected', 'destination': 'foreign'},
                })
                target.unlink()
        source.chmod(0o666)
        result = self.dispatch(observe)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(json.loads(result.stdout)['paths'], {'source': 'foreign', 'destination': 'missing'})

    def test_move_dispatch_rejects_unpinned_parent_extra_fields_and_occupied_target(self):
        source = self.parent / 'source'
        source.write_bytes(b'original')
        target = self.parent / 'target'
        target.write_bytes(b'preserve')
        observe = self.move_request('source-repair-observe-move')
        unpinned = observe['parentIdentity'] | {'inode': observe['parentIdentity']['inode'] + 1}
        for request in (
            observe | {'parentIdentity': unpinned}, observe | {'unexpected': True},
            observe | {'action': 'source-repair-rename-move', 'parentIdentity': unpinned},
            observe | {'action': 'source-repair-rename-move'},
        ):
            with self.subTest(request=request):
                result = self.dispatch(request)
                self.assertEqual(result.returncode, 2, result.stdout)
                self.assertEqual(json.loads(result.stdout), {'status': 'blocked'})
                self.assertEqual(result.stderr, '')
        self.assertEqual(source.read_bytes(), b'original')
        self.assertEqual(target.read_bytes(), b'preserve')

    def test_observe_refuses_nonexact_alias_even_when_lookup_resolves_expected_inode(self):
        (self.parent / 'source').write_bytes(b'original')
        request = self.move_request('source-repair-observe-move')
        real_exact = custody._exact_name_in_directory

        def alias_listing(fd, name):
            return name != 'source' and real_exact(fd, name)

        with mock.patch.object(custody, '_exact_name_in_directory', side_effect=alias_listing):
            paths = custody.observe_source_repair_move(
                request['parent'], request['source'], request['destination'],
                request['parentIdentity'], request['entryIdentity'])
        self.assertEqual(paths, {'source': 'foreign', 'destination': 'missing'})

    def test_noreplace_probe_dispatch_requires_exact_fields_and_pinned_parent(self):
        keeper = self.parent / 'keep'
        keeper.write_bytes(b'outside the probe')
        identity = self.identity(self.parent)
        request = {'action': 'source-repair-probe-noreplace', 'parent': str(self.parent),
                   'parentIdentity': identity}
        for invalid in (request | {'unexpected': True},
                        request | {'parentIdentity': identity | {'mountId': identity['mountId'] + 1}}):
            with self.subTest(request=invalid):
                result = self.dispatch(invalid)
                self.assertEqual(result.returncode, 2, result.stdout)
                self.assertEqual(json.loads(result.stdout), {'status': 'blocked'})
        result = self.dispatch(request)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(json.loads(result.stdout), {'status': 'ok'})
        self.assertEqual(result.stderr, '')
        self.assertEqual(list(self.parent.iterdir()), [keeper])
        self.assertEqual(keeper.read_bytes(), b'outside the probe')

    def test_noreplace_probe_attempts_occupied_and_absent_targets_for_both_kinds(self):
        attempts = []
        rename = custody._rename_noreplace

        def record(parent_fd, source, destination):
            source_stat = os.stat(source, dir_fd=parent_fd, follow_symlinks=False)
            try:
                os.stat(destination, dir_fd=parent_fd, follow_symlinks=False)
                occupied = True
            except FileNotFoundError:
                occupied = False
            attempts.append((stat.S_IFMT(source_stat.st_mode), occupied))
            return rename(parent_fd, source, destination)

        with mock.patch.object(custody, '_rename_noreplace', side_effect=record):
            custody.probe_source_repair_noreplace(str(self.parent), self.identity(self.parent))
        self.assertEqual(attempts, [(stat.S_IFREG, True), (stat.S_IFREG, False),
                                    (stat.S_IFDIR, True), (stat.S_IFDIR, False)])
        self.assertEqual(list(self.parent.iterdir()), [])

    def test_noreplace_probe_refuses_unsupported_and_unobserved_rename(self):
        rename = custody._rename_noreplace

        def pretend_success(parent_fd, source, destination):
            try:
                os.stat(destination, dir_fd=parent_fd, follow_symlinks=False)
            except FileNotFoundError:
                return
            return rename(parent_fd, source, destination)

        for failure, exception in ((OSError(errno.EINVAL, 'unsupported'), OSError),
                                   (pretend_success, custody.CustodyError)):
            with self.subTest(failure=failure):
                with mock.patch.object(custody, '_rename_noreplace', side_effect=failure):
                    with self.assertRaises(exception):
                        custody.probe_source_repair_noreplace(str(self.parent), self.identity(self.parent))
                self.assertEqual(list(self.parent.iterdir()), [])

    def test_noreplace_probe_preserves_foreign_replacement_in_owned_name(self):
        def inject_foreign(parent_fd, source, destination):
            os.unlink(destination, dir_fd=parent_fd)
            fd = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600, dir_fd=parent_fd)
            try:
                os.write(fd, b'foreign replacement')
            finally:
                os.close(fd)
            raise custody.CustodyError('destination-exists')

        with mock.patch.object(custody, '_rename_noreplace', side_effect=inject_foreign):
            with self.assertRaises((custody.CustodyError, OSError)):
                custody.probe_source_repair_noreplace(str(self.parent), self.identity(self.parent))
        leftover, = self.parent.iterdir()
        foreign, = leftover.iterdir()
        self.assertEqual(foreign.read_bytes(), b'foreign replacement')

    def test_regular_file_moves_without_changing_content_or_identity(self):
        original = self.parent / 'source'
        original.write_bytes(b'exact local bytes')
        expected = self.identity(original, 'file')
        self.move(entry=expected)
        self.assertFalse(original.exists())
        self.assertEqual((self.parent / 'target').read_bytes(), b'exact local bytes')
        self.assertEqual(self.identity(self.parent / 'target', 'file'), expected)

    def test_empty_and_populated_directory_moves_keep_descendants(self):
        for populated in (False, True):
            with self.subTest(populated=populated):
                with tempfile.TemporaryDirectory(dir='/root') as directory:
                    parent = pathlib.Path(directory)
                    source = parent / 'source'
                    source.mkdir()
                    if populated:
                        (source / 'nested').mkdir()
                        (source / 'nested' / 'track.flac').write_bytes(b'media stays local')
                    expected = self.identity(source, 'directory')
                    custody.rename_source_entry(directory, 'source', 'target', self.identity(parent), expected)
                    self.assertFalse(source.exists())
                    self.assertEqual(self.identity(parent / 'target', 'directory'), expected)
                    if populated:
                        self.assertEqual((parent / 'target' / 'nested' / 'track.flac').read_bytes(), b'media stays local')

    def test_existing_target_is_never_replaced(self):
        (self.parent / 'source').write_bytes(b'original')
        (self.parent / 'target').write_bytes(b'foreign')
        with self.assertRaisesRegex(custody.CustodyError, 'destination-exists'):
            self.move()
        self.assertEqual((self.parent / 'source').read_bytes(), b'original')
        self.assertEqual((self.parent / 'target').read_bytes(), b'foreign')

    def test_existing_target_symlink_is_not_followed_or_replaced(self):
        (self.parent / 'source').write_bytes(b'original')
        (self.parent / 'outside').write_bytes(b'foreign')
        (self.parent / 'target').symlink_to('outside')
        with self.assertRaisesRegex(custody.CustodyError, 'destination-exists'):
            self.move()
        self.assertEqual((self.parent / 'source').read_bytes(), b'original')
        self.assertTrue((self.parent / 'target').is_symlink())
        self.assertEqual((self.parent / 'outside').read_bytes(), b'foreign')

    def test_existing_target_directory_is_not_replaced(self):
        (self.parent / 'source').write_bytes(b'original')
        (self.parent / 'target').mkdir()
        (self.parent / 'target' / 'foreign').write_bytes(b'kept')
        with self.assertRaisesRegex(custody.CustodyError, 'destination-exists'):
            self.move()
        self.assertEqual((self.parent / 'source').read_bytes(), b'original')
        self.assertEqual((self.parent / 'target' / 'foreign').read_bytes(), b'kept')

    def test_symlink_fifo_and_unknown_kind_refuse_without_mutation(self):
        (self.parent / 'outside').write_bytes(b'outside')
        (self.parent / 'source').symlink_to('outside')
        with self.assertRaises(custody.CustodyError):
            self.move(entry=self.identity(self.parent / 'source', 'file'))
        self.assertTrue((self.parent / 'source').is_symlink())
        self.assertFalse((self.parent / 'target').exists())
        (self.parent / 'source').unlink()
        os.mkfifo(self.parent / 'source')
        with self.assertRaises(custody.CustodyError):
            self.move(entry=self.identity(self.parent / 'source', 'file'))
        self.assertTrue(stat.S_ISFIFO(os.lstat(self.parent / 'source').st_mode))
        (self.parent / 'source').unlink()
        (self.parent / 'source').write_bytes(b'kept')
        with self.assertRaises(custody.CustodyError):
            self.move(entry=self.identity(self.parent / 'source', 'unknown'))
        self.assertEqual((self.parent / 'source').read_bytes(), b'kept')

    def test_parent_and_entry_identity_or_mount_mismatch_refuse(self):
        (self.parent / 'source').write_bytes(b'kept')
        parent = self.identity(self.parent)
        entry = self.identity(self.parent / 'source', 'file')
        for field in ('device', 'inode', 'mountId'):
            with self.subTest(field=field, target='parent'):
                with self.assertRaises(custody.CustodyError):
                    self.move(parent=parent | {field: parent[field] + 1}, entry=entry)
            with self.subTest(field=field, target='entry'):
                with self.assertRaises(custody.CustodyError):
                    self.move(parent=parent, entry=entry | {field: entry[field] + 1})
        with self.assertRaises(custody.CustodyError):
            self.move(parent=parent, entry=entry | {'kind': 'directory'})
        self.assertEqual((self.parent / 'source').read_bytes(), b'kept')
        self.assertFalse((self.parent / 'target').exists())

    def test_real_cross_mount_entry_refuses_before_rename(self):
        parent = pathlib.Path('/')
        source = pathlib.Path('/dev')
        if self.identity(parent)['mountId'] == self.identity(source)['mountId']:
            self.skipTest('container has no separate /dev mount')
        destination = 'source-repair-test-' + uuid.uuid4().hex
        with self.assertRaisesRegex(custody.CustodyError, 'cross-device-move-forbidden'):
            custody.rename_source_entry('/', 'dev', destination, self.identity(parent),
                                        self.identity(source, 'directory'))
        self.assertTrue(source.is_dir())
        self.assertFalse((parent / destination).exists())

    def test_unsupported_noreplace_does_not_fall_back(self):
        (self.parent / 'source').write_bytes(b'kept')
        with mock.patch.object(custody, '_rename_noreplace', side_effect=OSError(errno.EINVAL, 'unsupported')):
            with self.assertRaises(OSError):
                self.move()
        self.assertEqual((self.parent / 'source').read_bytes(), b'kept')
        self.assertFalse((self.parent / 'target').exists())

    def test_unsafe_parent_or_entry_mode_refuses(self):
        source = self.parent / 'source'
        source.write_bytes(b'kept')
        self.parent.chmod(0o777)
        with self.assertRaisesRegex(custody.CustodyError, 'parent-not-exclusively-root-controlled'):
            self.move()
        self.parent.chmod(0o700)
        source.chmod(0o666)
        with self.assertRaisesRegex(custody.CustodyError, 'source-not-exclusively-root-controlled'):
            self.move()
        source.chmod(0o600)
        os.chown(source, 12345, -1)
        with self.assertRaisesRegex(custody.CustodyError, 'source-not-exclusively-root-controlled'):
            self.move()
        self.assertEqual(source.read_bytes(), b'kept')
        self.assertFalse((self.parent / 'target').exists())

    def test_symlink_parent_and_invalid_leaves_refuse(self):
        (self.parent / 'source').write_bytes(b'kept')
        alias = self.parent.parent / ('alias-' + self.parent.name)
        alias.symlink_to(self.parent)
        self.addCleanup(alias.unlink)
        with self.assertRaises(custody.CustodyError):
            custody.rename_source_entry(str(alias), 'source', 'target', self.identity(self.parent),
                                        self.identity(self.parent / 'source', 'file'))
        for leaf in ('', '.', '..', 'child/other', 'source', 'bad\\name', 'bad:name',
                     'bad\nname', 'bad\u0085name', 'bad ', 'bad.'):
            with self.subTest(leaf=leaf), self.assertRaises(custody.CustodyError):
                self.move(destination=leaf)
        with self.assertRaisesRegex(custody.CustodyError, 'source-repair-leaf-invalid'):
            self.move(destination=42)
        with self.assertRaisesRegex(custody.CustodyError, 'invalid-parent-or-target'):
            custody.rename_source_entry(42, 'source', 'target', self.identity(self.parent),
                                        self.identity(self.parent / 'source', 'file'))
        self.assertFalse((self.parent / 'target').exists())

    def test_source_spelling_must_match_descriptor_listing(self):
        (self.parent / 'source').write_bytes(b'kept')
        real_scandir = custody.os.scandir
        parent_identity = self.identity(self.parent)

        def wrong_listing(fd):
            with real_scandir(fd) as entries:
                listed = [types.SimpleNamespace(name='Source' if entry.name == 'source' else entry.name)
                          if os.fstat(fd).st_ino == parent_identity['inode'] else entry
                          for entry in entries]
            return contextlib.nullcontext(iter(listed))

        with mock.patch.object(custody.os, 'scandir', side_effect=wrong_listing):
            with self.assertRaisesRegex(custody.CustodyError, 'source-repair-source-spelling-changed'):
                self.move()
        self.assertEqual((self.parent / 'source').read_bytes(), b'kept')
        self.assertFalse((self.parent / 'target').exists())

    def test_parent_component_spelling_must_match_descriptor_listing(self):
        (self.parent / 'source').write_bytes(b'kept')
        real_scandir = custody.os.scandir
        ancestor_identity = self.identity(self.parent.parent)

        def wrong_listing(fd):
            with real_scandir(fd) as entries:
                listed = [types.SimpleNamespace(name=self.parent.name.upper())
                          if os.fstat(fd).st_ino == ancestor_identity['inode']
                          and entry.name == self.parent.name else entry for entry in entries]
            return contextlib.nullcontext(iter(listed))

        with mock.patch.object(custody.os, 'scandir', side_effect=wrong_listing):
            with self.assertRaisesRegex(custody.CustodyError, 'source-repair-parent-spelling-changed'):
                self.move()
        self.assertEqual((self.parent / 'source').read_bytes(), b'kept')
        self.assertFalse((self.parent / 'target').exists())

    def test_source_spelling_rechecked_immediately_before_rename(self):
        (self.parent / 'source').write_bytes(b'kept')
        real_exact = custody._exact_name_in_directory
        reads = 0

        def disappear_on_second_read(fd, name):
            nonlocal reads
            if name == 'source':
                reads += 1
                return reads == 1
            return real_exact(fd, name)

        with mock.patch.object(custody, '_exact_name_in_directory', side_effect=disappear_on_second_read):
            with self.assertRaisesRegex(custody.CustodyError, 'source-repair-source-spelling-changed'):
                self.move()
        self.assertEqual(reads, 2)
        self.assertEqual((self.parent / 'source').read_bytes(), b'kept')
        self.assertFalse((self.parent / 'target').exists())

    def test_fsync_failure_reports_uncertain_committed_state_without_rollback(self):
        (self.parent / 'source').write_bytes(b'kept')
        with mock.patch.object(custody.os, 'fsync', side_effect=OSError('injected fsync failure')):
            with self.assertRaisesRegex(custody.CustodyError, 'source-repair-commit-uncertain'):
                self.move()
        self.assertFalse((self.parent / 'source').exists())
        self.assertEqual((self.parent / 'target').read_bytes(), b'kept')


@unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires disposable Linux root')
class PreservationReadbackTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(dir='/root')
        self.addCleanup(self.directory.cleanup)
        self.parent = pathlib.Path(self.directory.name)
        self.archive = self.parent / 'archive'
        self.archive.mkdir(mode=0o700)

    def identity(self, path):
        fd = os.open(path, os.O_PATH | os.O_CLOEXEC | os.O_NOFOLLOW)
        try:
            info = os.fstat(fd)
            return {'device': info.st_dev, 'inode': info.st_ino, 'mountId': custody._fd_mount_id(fd)}
        finally:
            os.close(fd)

    def manifest(self, entries):
        files = [item for item in entries if item['kind'] == 'file']
        value = {'schemaVersion': 1, 'fileCount': len(files),
                 'totalBytes': sum(item['size'] for item in files), 'entries': entries}
        data = json.dumps(value, separators=(',', ':'), ensure_ascii=False).encode('utf-8')
        return data, hashlib.sha256(data).hexdigest()

    def verify(self, data, digest, *, parent=None, archive=None):
        return custody.verify_source_repair_archive(str(self.parent), 'archive',
                                                    parent or self.identity(self.parent),
                                                    archive or self.identity(self.archive), data, digest)

    def terminal_fixture(self):
        source = self.parent / 'music'
        entries = [
            ('library', 'directory', None),
            ('library/Album', 'directory', None),
            ('library/Album/Empty', 'directory', None),
            ('library/Album/é.flac', 'file', b'audio one'),
            ('Track.flac', 'file', b'audio two'),
            ('track.flac', 'file', b'audio retained'),
            ('unselected skip', 'directory', None),
        ]
        selected = []
        for path, kind, data in entries:
            target = source / path
            item = {'path': path, 'kind': kind}
            if kind == 'directory':
                target.mkdir(parents=True)
            else:
                target.write_bytes(data)
                item |= {'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
            if path != 'unselected skip':
                selected.append(item)
        moves = [{'source': before, 'destination': after, 'kind': kind,
                  'identity': self.identity(source / before)} for before, after, kind in
                 [('library/Album', 'library/Album preserved', 'directory'),
                  ('Track.flac', 'Track preserved.flac', 'file')]]
        rows = [(path, kind) for path, kind, _ in entries]
        return source, moves, selected, rows

    def terminal_request(self, source, moves, selected, rows, repaired=False):
        data, digest = self.manifest(selected)
        records = [(path.encode('utf-8'), kind[0].upper().encode('ascii')) for path, kind in rows]
        namespace = hashlib.sha256(b''.join(kind + b'\0' + path + b'\0' for path, kind in sorted(records)))
        return {'action': 'source-repair-verify-tree', 'sourceRoot': str(source),
                'sourceIdentity': self.identity(source), 'moves': moves, 'repaired': repaired,
                'manifestBase64': base64.b64encode(data).decode('ascii'), 'manifestSha256': digest,
                'namespaceSha256': namespace.hexdigest()}

    def dispatch_terminal(self, request, expected='ok'):
        payload = json.dumps(request) if isinstance(request, dict) else request
        result = subprocess.run([sys.executable, '-I', str(MODULE)], input=payload,
                                text=True, capture_output=True, timeout=10, check=False)
        self.assertEqual(result.returncode, 0 if expected == 'ok' else 2, result.stdout)
        self.assertEqual(json.loads(result.stdout), {'status': expected})
        self.assertEqual(result.stderr, '')

    def test_terminal_verifies_exact_original_and_repaired_namespace_and_contents(self):
        source, moves, selected, rows = self.terminal_fixture()
        original = self.terminal_request(source, moves, selected, rows)
        self.dispatch_terminal(original)

        for move in moves:
            (source / move['source']).rename(source / move['destination'])
        after = [
            ('library', 'directory'),
            ('library/Album preserved', 'directory'),
            ('library/Album preserved/Empty', 'directory'),
            ('library/Album preserved/é.flac', 'file'),
            ('Track preserved.flac', 'file'),
            ('track.flac', 'file'),
            ('unselected skip', 'directory'),
        ]
        repaired = self.terminal_request(source, moves, selected, after, repaired=True)
        self.dispatch_terminal(repaired)
        self.dispatch_terminal(original, 'blocked')
        (source / 'track.flac').write_bytes(b'other retained')
        self.dispatch_terminal(repaired, 'blocked')

    def test_terminal_refuses_namespace_omissions_and_unbound_move_identities(self):
        source, moves, selected, rows = self.terminal_fixture()
        request = self.terminal_request(source, moves, selected, rows)
        self.dispatch_terminal(request | {'unexpected': 'field'}, 'blocked')
        self.dispatch_terminal(request | {'repaired': 0}, 'blocked')
        self.dispatch_terminal('{"repaired":false,' + json.dumps(request)[1:], 'blocked')
        (source / 'unselected skip').rmdir()
        self.dispatch_terminal(request, 'blocked')
        (source / 'unselected skip').mkdir()
        omitted = [item for item in selected if not item['path'].endswith('Empty')]
        self.dispatch_terminal(self.terminal_request(source, moves, omitted, rows), 'blocked')
        (source / 'Track.flac').rename(source / 'replacement-original')
        (source / 'Track.flac').write_bytes(b'audio two')
        (source / 'replacement-original').unlink()
        self.dispatch_terminal(request, 'blocked')

    def test_terminal_refuses_unselected_special_entry_even_with_matching_file_digest_record(self):
        source, moves, selected, rows = self.terminal_fixture()
        for add in (lambda path: path.symlink_to('Track.flac'), os.mkfifo):
            with self.subTest(add=add):
                path = source / 'foreign'
                add(path)
                request = self.terminal_request(source, moves, selected, rows + [('foreign', 'file')])
                self.dispatch_terminal(request, 'blocked')
                path.unlink()

    def test_terminal_rejects_unselected_metadata_drift_during_selected_content_read(self):
        source, moves, selected, rows = self.terminal_fixture()
        unrelated = source / 'unrelated.txt'
        unrelated.write_bytes(b'original')
        before = unrelated.stat()
        request = self.terminal_request(source, moves, selected, rows + [('unrelated.txt', 'file')])
        verify_payloads = custody._verify_archive_tree

        def change_after_read(directory_fd, prefix, *args, **kwargs):
            verify_payloads(directory_fd, prefix, *args, **kwargs)
            if not prefix:
                unrelated.write_bytes(b'modified')
                os.utime(unrelated, ns=(before.st_atime_ns, before.st_mtime_ns - 1_000_000_000))

        with mock.patch.object(custody, '_verify_archive_tree', side_effect=change_after_read):
            with self.assertRaisesRegex(custody.CustodyError, 'source-repair-terminal-tree-changed'):
                custody.verify_source_repair_tree(str(source), request['sourceIdentity'], moves, False,
                                                  base64.b64decode(request['manifestBase64']),
                                                  request['manifestSha256'], request['namespaceSha256'])
        self.assertEqual(unrelated.read_bytes(), b'modified')

    def test_complete_readback_including_empty_directory_and_case_distinct_files(self):
        (self.archive / 'empty').mkdir()
        (self.archive / 'Track.flac').write_bytes(b'upper')
        (self.archive / 'track.flac').write_bytes(b'lower')
        entries = [{'path': 'empty', 'kind': 'directory'},
                   {'path': 'Track.flac', 'kind': 'file', 'size': 5,
                    'sha256': hashlib.sha256(b'upper').hexdigest()},
                   {'path': 'track.flac', 'kind': 'file', 'size': 5,
                    'sha256': hashlib.sha256(b'lower').hexdigest()}]
        data, digest = self.manifest(entries)
        self.assertEqual(self.verify(data, digest), self.identity(self.archive))

    def test_nested_tree_and_empty_file_read_back_exactly(self):
        (self.archive / 'Artist').mkdir()
        (self.archive / 'Artist' / 'Album').mkdir()
        (self.archive / 'Artist' / 'Album' / 'empty.nfo').write_bytes(b'')
        entries = [{'path': 'Artist', 'kind': 'directory'},
                   {'path': 'Artist/Album', 'kind': 'directory'},
                   {'path': 'Artist/Album/empty.nfo', 'kind': 'file', 'size': 0,
                    'sha256': hashlib.sha256(b'').hexdigest()}]
        self.assertEqual(self.verify(*self.manifest(entries)), self.identity(self.archive))
        (self.archive / 'Artist' / 'Album' / 'unexpected').write_bytes(b'extra')
        with mock.patch.object(custody.os, 'read', side_effect=AssertionError('payload read before tree check')):
            with self.assertRaisesRegex(custody.CustodyError, 'source-repair-archive-tree-invalid'):
                self.verify(*self.manifest(entries))

    def test_missing_extra_and_wrong_payload_refuse(self):
        (self.archive / 'track.flac').write_bytes(b'actual')
        good = {'path': 'track.flac', 'kind': 'file', 'size': 6,
                'sha256': hashlib.sha256(b'actual').hexdigest()}
        for entries in ([], [good, {'path': 'missing', 'kind': 'directory'}],
                        [good | {'sha256': hashlib.sha256(b'wrong!').hexdigest()}]):
            with self.subTest(entries=entries), self.assertRaises(custody.CustodyError):
                self.verify(*self.manifest(entries))
        self.assertEqual((self.archive / 'track.flac').read_bytes(), b'actual')

    def test_special_entries_and_alias_names_refuse(self):
        (self.archive / 'actual').write_bytes(b'data')
        item = {'path': 'Actual', 'kind': 'file', 'size': 4, 'sha256': hashlib.sha256(b'data').hexdigest()}
        with self.assertRaises(custody.CustodyError):
            self.verify(*self.manifest([item]))
        (self.archive / 'actual').unlink()
        (self.archive / 'actual').symlink_to('elsewhere')
        with self.assertRaises(custody.CustodyError):
            self.verify(*self.manifest([item | {'path': 'actual'}]))
        (self.archive / 'actual').unlink()
        os.mkfifo(self.archive / 'actual')
        with self.assertRaises(custody.CustodyError):
            self.verify(*self.manifest([item | {'path': 'actual'}]))

    def test_hardlinked_archive_file_is_not_independent(self):
        source = self.parent / 'source.flac'
        source.write_bytes(b'original')
        os.link(source, self.archive / 'track.flac')
        item = {'path': 'track.flac', 'kind': 'file', 'size': 8,
                'sha256': hashlib.sha256(b'original').hexdigest()}
        with self.assertRaisesRegex(custody.CustodyError, 'source-repair-archive-tree-invalid'):
            self.verify(*self.manifest([item]))
        self.assertEqual(source.read_bytes(), b'original')

    def test_manifest_and_archive_identity_fail_before_payload_read(self):
        (self.archive / 'track.flac').write_bytes(b'data')
        item = {'path': 'track.flac', 'kind': 'file', 'size': 4, 'sha256': hashlib.sha256(b'data').hexdigest()}
        data, digest = self.manifest([item])
        with mock.patch.object(custody.os, 'read', side_effect=AssertionError('payload read')):
            with self.assertRaises(custody.CustodyError):
                self.verify(data, '0' * 64)
            with self.assertRaises(custody.CustodyError):
                self.verify(data, digest, archive=self.identity(self.archive) | {'inode': 1})
            with self.assertRaises(custody.CustodyError):
                self.verify(data, digest, parent=self.identity(self.parent) | {'mountId': 1})

    def test_malformed_manifest_refuses_before_payload_read(self):
        duplicate = b'{"schemaVersion":1,"schemaVersion":1,"fileCount":0,"totalBytes":0,"entries":[]}'
        oversized = b'{' + b' ' * (32 * 1024 * 1024) + b'}'
        for data in (duplicate, oversized,
                     self.manifest([{'path': 'missing/child', 'kind': 'directory'}])[0]):
            with self.subTest(length=len(data)):
                with mock.patch.object(custody.os, 'read', side_effect=AssertionError('payload read')):
                    with self.assertRaises(custody.CustodyError):
                        self.verify(data, hashlib.sha256(data).hexdigest())

    def planned_moves(self, source, data):
        return [{'source': entry['path'], 'kind': entry['kind'],
                 'identity': self.identity(source / entry['path'])}
                for entry in json.loads(data)['entries'] if '/' not in entry['path']]

    def test_move_requires_complete_subtree_and_pinned_identity_before_reservation(self):
        self.archive.rmdir()
        source = self.parent / 'source'
        album = source / 'artist' / 'album'
        (album / 'nested').mkdir(parents=True)
        (album / 'nested' / 'track').write_bytes(b'kept')
        (source / 'artist' / 'unrelated').mkdir()
        entries = [{'path': path, 'kind': 'directory'} for path in
                   ('artist', 'artist/album', 'artist/album/nested')]
        move = {'source': 'artist/album', 'kind': 'directory', 'identity': self.identity(album)}

        def reserve(manifest_entries, moves):
            data, digest = self.manifest(manifest_entries)
            return custody.create_or_resume_source_repair_archive(
                str(source), self.identity(source), str(self.parent), 'archive', self.identity(self.parent),
                None, data, digest, moves)

        with self.assertRaises(custody.CustodyError):
            reserve(entries, [move])
        entries.append({'path': 'artist/album/nested/track', 'kind': 'file', 'size': 4,
                        'sha256': hashlib.sha256(b'kept').hexdigest()})
        with self.assertRaises(custody.CustodyError):
            reserve(entries, [move | {'identity': move['identity'] | {'inode': move['identity']['inode'] + 1}}])
        with self.assertRaises(custody.CustodyError):
            reserve(entries, [move | {'source': 'artist/missing'}])
        self.assertFalse(self.archive.exists())
        self.assertEqual(reserve(entries, [move]), self.identity(self.archive))

    def test_moved_subtree_drift_after_copy_revokes_receipt(self):
        source = self.parent / 'source'
        album = source / 'album'
        album.mkdir(parents=True)
        data, digest = self.manifest([{'path': 'album', 'kind': 'directory'}])
        copy = custody._copy_archive_tree

        def add_unreviewed_child(*args):
            copy(*args)
            if args[2] == '':
                (album / 'new-file').write_bytes(b'unreviewed')

        with mock.patch.object(custody, '_copy_archive_tree', side_effect=add_unreviewed_child):
            with self.assertRaises(custody.CustodyError):
                custody.create_or_resume_source_repair_archive(
                    str(source), self.identity(source), str(self.parent), 'archive', self.identity(self.parent),
                    self.identity(self.archive), data, digest, self.planned_moves(source, data))
        self.assertEqual((album / 'new-file').read_bytes(), b'unreviewed')

    def test_reserve_then_copy_and_independent_readback(self):
        self.archive.rmdir()
        source = self.parent / 'source'
        source.mkdir()
        (source / 'empty').mkdir()
        (source / 'Track.flac').write_bytes(b'upper')
        (source / 'track.flac').write_bytes(b'lower')
        (source / 'unrelated.flac').write_bytes(b'outside the preservation subset')
        (source / 'other').mkdir()
        entries = [{'path': 'empty', 'kind': 'directory'},
                   {'path': 'Track.flac', 'kind': 'file', 'size': 5,
                    'sha256': hashlib.sha256(b'upper').hexdigest()},
                   {'path': 'track.flac', 'kind': 'file', 'size': 5,
                    'sha256': hashlib.sha256(b'lower').hexdigest()}]
        data, digest = self.manifest(entries)
        reserve = custody.create_or_resume_source_repair_archive(
            str(source), self.identity(source), str(self.parent), 'archive', self.identity(self.parent),
            None, data, digest, self.planned_moves(source, data))
        self.assertEqual(reserve, self.identity(self.archive))
        self.assertEqual(list(self.archive.iterdir()), [])
        with self.assertRaises(custody.CustodyError):
            custody.create_or_resume_source_repair_archive(
                str(source), self.identity(source), str(self.parent), 'archive', self.identity(self.parent),
                None, data, digest, self.planned_moves(source, data))
        copied = custody.create_or_resume_source_repair_archive(
            str(source), self.identity(source), str(self.parent), 'archive', self.identity(self.parent),
            reserve, data, digest, self.planned_moves(source, data))
        self.assertEqual(copied, reserve)
        self.assertEqual(self.verify(data, digest, archive=reserve), reserve)
        self.assertEqual((source / 'Track.flac').read_bytes(), b'upper')
        self.assertFalse((self.archive / 'unrelated.flac').exists())

    def test_interrupted_copy_resumes_completed_files_without_replacing_them(self):
        source = self.parent / 'source'
        source.mkdir()
        (source / 'one').write_bytes(b'one')
        (source / 'two').write_bytes(b'two')
        entries = [{'path': name, 'kind': 'file', 'size': len(name),
                    'sha256': hashlib.sha256(name.encode()).hexdigest()} for name in ('one', 'two')]
        data, digest = self.manifest(entries)
        archive_id = self.identity(self.archive)
        real_open = custody.os.open

        def fail_before_second(path, flags, *args, **kwargs):
            if path == 'two' and flags & os.O_CREAT:
                raise OSError('injected interruption')
            return real_open(path, flags, *args, **kwargs)

        with mock.patch.object(custody.os, 'open', side_effect=fail_before_second):
            with self.assertRaises(OSError):
                custody.create_or_resume_source_repair_archive(
                    str(source), self.identity(source), str(self.parent), 'archive', self.identity(self.parent),
                    archive_id, data, digest, self.planned_moves(source, data))
        first = self.archive / 'one'
        self.assertEqual(first.read_bytes(), b'one')
        first_id = self.identity(first)
        custody.create_or_resume_source_repair_archive(
            str(source), self.identity(source), str(self.parent), 'archive', self.identity(self.parent),
            archive_id, data, digest, self.planned_moves(source, data))
        self.assertEqual(self.identity(first), first_id)
        self.assertEqual(self.verify(data, digest, archive=archive_id), archive_id)

    def test_foreign_partial_file_blocks_without_overwrite(self):
        source = self.parent / 'source'
        source.mkdir()
        (source / 'track').write_bytes(b'original')
        (self.archive / 'track').write_bytes(b'foreign!')
        entries = [{'path': 'track', 'kind': 'file', 'size': 8,
                    'sha256': hashlib.sha256(b'original').hexdigest()}]
        data, digest = self.manifest(entries)
        with self.assertRaises(custody.CustodyError):
            custody.create_or_resume_source_repair_archive(
                str(source), self.identity(source), str(self.parent), 'archive', self.identity(self.parent),
                self.identity(self.archive), data, digest, self.planned_moves(source, data))
        self.assertEqual((self.archive / 'track').read_bytes(), b'foreign!')
        self.assertEqual((source / 'track').read_bytes(), b'original')

    def test_mid_file_write_stall_keeps_partial_and_blocks_resume(self):
        source = self.parent / 'source'
        source.mkdir()
        payload = b'a' * (1024 * 1024 + 1)
        (source / 'track').write_bytes(payload)
        data, digest = self.manifest([{'path': 'track', 'kind': 'file', 'size': len(payload),
                                      'sha256': hashlib.sha256(payload).hexdigest()}])
        archive_id = self.identity(self.archive)
        actual_write = custody.os.write
        writes = 0

        def stalled_write(fd, chunk):
            nonlocal writes
            writes += 1
            return 0 if writes == 2 else actual_write(fd, chunk)

        with mock.patch.object(custody.os, 'write', side_effect=stalled_write):
            with self.assertRaises(OSError):
                custody.create_or_resume_source_repair_archive(
                    str(source), self.identity(source), str(self.parent), 'archive', self.identity(self.parent),
                    archive_id, data, digest, self.planned_moves(source, data))
        partial = self.archive / 'track'
        self.assertEqual(partial.stat().st_size, 1024 * 1024)
        with self.assertRaises(custody.CustodyError):
            custody.create_or_resume_source_repair_archive(
                str(source), self.identity(source), str(self.parent), 'archive', self.identity(self.parent),
                archive_id, data, digest, self.planned_moves(source, data))
        self.assertEqual(partial.stat().st_size, 1024 * 1024)

    def test_archive_dispatch_rejects_extra_fields_and_verifies_exact_request(self):
        source = self.parent / 'source'
        source.mkdir()
        (source / 'track').write_bytes(b'kept')
        data, digest = self.manifest([{'path': 'track', 'kind': 'file', 'size': 4,
                                      'sha256': hashlib.sha256(b'kept').hexdigest()}])
        request = {'action': 'source-repair-archive-create-or-resume', 'sourceRoot': str(source),
                   'sourceIdentity': self.identity(source), 'moves': self.planned_moves(source, data), 'archiveParent': str(self.parent),
                   'archiveLeaf': 'archive', 'archiveParentIdentity': self.identity(self.parent),
                   'archiveIdentity': self.identity(self.archive),
                   'manifestBase64': base64.b64encode(data).decode('ascii'), 'manifestSha256': digest}

        def invoke(value):
            completed = subprocess.run([sys.executable, str(MODULE)], input=json.dumps(value).encode(),
                                       capture_output=True, check=False)
            return completed.returncode, json.loads(completed.stdout)

        invalid = request | {'unreviewed': True}
        self.assertEqual(invoke(invalid), (2, {'status': 'blocked'}))
        self.assertFalse((self.archive / 'track').exists())
        code, output = invoke(request)
        self.assertEqual(code, 0)
        self.assertEqual(output['identity'], request['archiveIdentity'])
        verify = {key: value for key, value in request.items() if key not in ('sourceRoot', 'sourceIdentity', 'moves')}
        verify['action'] = 'source-repair-archive-verify'
        self.assertEqual(invoke(verify), (0, {'status': 'ok', 'identity': request['archiveIdentity']}))


if __name__ == '__main__':
    unittest.main()
