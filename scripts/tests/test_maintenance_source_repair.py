"""Disposable Linux custody proof for one exact source-path rename primitive."""
import importlib.util
import contextlib
import errno
import os
import pathlib
import stat
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


if __name__ == '__main__':
    unittest.main()
