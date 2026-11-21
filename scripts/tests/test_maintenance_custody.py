import importlib.util
import os
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

MODULE = pathlib.Path(__file__).resolve().parents[2] / 'MaintenanceRunner' / 'custody.py'
spec = importlib.util.spec_from_file_location('custody', MODULE)
custody = importlib.util.module_from_spec(spec)
spec.loader.exec_module(custody)

class CustodyTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires Linux root')
    def test_capability_probe_uses_only_new_empty_directories(self):
        with tempfile.TemporaryDirectory(dir='/root') as tmp:
            media = pathlib.Path(tmp) / 'existing-media'
            media.write_bytes(b'preserve')
            custody.probe_noreplace(tmp)
            self.assertEqual(media.read_bytes(), b'preserve')
            self.assertEqual([p.name for p in pathlib.Path(tmp).iterdir()], ['existing-media'])

    @unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires Linux root')
    def test_unsupported_capability_blocks_and_preserves_media(self):
        with tempfile.TemporaryDirectory(dir='/root') as tmp:
            media = pathlib.Path(tmp) / 'existing-media'
            media.write_bytes(b'preserve')
            with mock.patch.object(custody, 'rename_directory', side_effect=OSError(22, 'unsupported')):
                with self.assertRaises(OSError):
                    custody.probe_noreplace(tmp)
            self.assertEqual(media.read_bytes(), b'preserve')
            self.assertEqual([p.name for p in pathlib.Path(tmp).iterdir()], ['existing-media'])

    @unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires Linux root')
    def test_capability_probe_rejects_writable_parent_before_creating_anything(self):
        with tempfile.TemporaryDirectory(dir='/root') as tmp:
            os.chmod(tmp, 0o777)
            with self.assertRaises(custody.CustodyError):
                custody.probe_noreplace(tmp)
            self.assertEqual(list(pathlib.Path(tmp).iterdir()), [])

    def test_invalid_leaves_are_rejected(self):
        for value in ('', '.', '..', 'a/b', '\x00'):
            with self.assertRaises(custody.CustodyError): custody._leaf(value)

    @unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires Linux root and renameat2')
    def test_real_noreplace_and_directory_identity(self):
        with tempfile.TemporaryDirectory(dir='/root') as tmp:
            root = pathlib.Path(tmp)
            (root / 'source').mkdir(); (root / 'occupied').mkdir()
            identity = (root / 'source').stat()
            with self.assertRaises(custody.CustodyError):
                custody.rename_directory(tmp, 'source', 'occupied', identity.st_dev, identity.st_ino)
            self.assertTrue((root / 'source').is_dir()); self.assertTrue((root / 'occupied').is_dir())
            custody.rename_directory(tmp, 'source', 'backup', identity.st_dev, identity.st_ino)
            self.assertEqual((root / 'backup').stat().st_ino, identity.st_ino)
            self.assertFalse((root / 'source').exists())

    @unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires Linux root')
    def test_world_writable_parent_fails_closed(self):
        with tempfile.TemporaryDirectory(dir='/root') as tmp:
            root = pathlib.Path(tmp)
            (root / 'source').mkdir()
            identity = (root / 'source').stat()
            root.chmod(0o777)
            with self.assertRaisesRegex(custody.CustodyError, 'parent-not-exclusively-root-controlled'):
                custody.rename_directory(tmp, 'source', 'backup', identity.st_dev, identity.st_ino)
            self.assertTrue((root / 'source').exists())
            self.assertFalse((root / 'backup').exists())

    @unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires Linux root')
    def test_empty_mountpoint_identity_is_pinned_and_rechecked(self):
        with tempfile.TemporaryDirectory(dir='/root') as tmp:
            root = pathlib.Path(tmp)
            identity = custody.create_empty_mountpoint(tmp, 'source', os.stat(tmp).st_dev)
            self.assertEqual(identity['path'], str(root / 'source'))
            self.assertGreater(identity['inode'], 0)
            self.assertGreater(identity['mountId'], 0)
            self.assertEqual(custody.inspect_empty_mountpoint(tmp, 'source', identity['device'], identity['mountId'], identity['inode']), identity)
            (root / 'source' / 'unexpected').write_text('keep')
            with self.assertRaisesRegex(custody.CustodyError, 'mountpoint-not-empty'):
                custody.inspect_empty_mountpoint(tmp, 'source', identity['device'], identity['mountId'], identity['inode'])
            self.assertEqual((root / 'source' / 'unexpected').read_text(), 'keep')

    @unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires Linux root')
    def test_replaced_mountpoint_identity_fails_closed(self):
        with tempfile.TemporaryDirectory(dir='/root') as tmp:
            root = pathlib.Path(tmp)
            identity = custody.create_empty_mountpoint(tmp, 'source', os.stat(tmp).st_dev)
            (root / 'source').rename(root / 'original')
            (root / 'source').mkdir(mode=0o755)
            with self.assertRaisesRegex(custody.CustodyError, 'mountpoint-identity-mismatch'):
                custody.inspect_empty_mountpoint(tmp, 'source', identity['device'], identity['mountId'], identity['inode'])
            self.assertTrue((root / 'source').is_dir())

if __name__ == '__main__': unittest.main()

@unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires Linux root')
class ConfigTests(unittest.TestCase):
    def test_before_after_replay_and_unknown_state(self):
        import hashlib
        digest = lambda value: hashlib.sha256(value).hexdigest()
        with tempfile.TemporaryDirectory(dir='/root') as tmp:
            file = pathlib.Path(tmp) / 'config.xml'
            file.write_bytes(b'before')
            custody.replace_config(tmp, 'config.xml', digest(b'before'), b'after', digest(b'after'))
            self.assertEqual(file.read_bytes(), b'after')
            custody.replace_config(tmp, 'config.xml', digest(b'before'), b'after', digest(b'after'))
            file.write_bytes(b'foreign')
            with self.assertRaisesRegex(custody.CustodyError, 'config-hash-mismatch'):
                custody.replace_config(tmp, 'config.xml', digest(b'before'), b'after', digest(b'after'))
            self.assertEqual(file.read_bytes(), b'foreign')

@unittest.skipUnless(sys.platform == 'linux' and os.geteuid() == 0, 'requires Linux root')
class ProjectionCleanupTests(unittest.TestCase):
    def test_only_exact_empty_owned_projection_is_removed(self):
        import json
        with tempfile.TemporaryDirectory(dir='/root') as tmp:
            root = pathlib.Path(tmp)
            mount = root / 'music'; mount.mkdir()
            marker = root / 'music.slipmat-rclone-cloud-mount.json'
            marker.write_text(json.dumps({'Version': 2, 'RemoteName': 'remote', 'RemoteType': 'local', 'RemotePath': 'path'}))
            (mount / 'user-file').write_text('keep')
            with self.assertRaisesRegex(custody.CustodyError, 'projection-not-empty-or-untrusted'):
                custody.remove_empty_projection(tmp, 'music', marker.name, 'remote', 'local', 'path')
            self.assertTrue((mount / 'user-file').exists()); self.assertTrue(marker.exists())
            (mount / 'user-file').unlink()
            custody.remove_empty_projection(tmp, 'music', marker.name, 'remote', 'local', 'path')
            self.assertFalse(mount.exists()); self.assertFalse(marker.exists())
            custody.remove_empty_projection(tmp, 'music', marker.name, 'remote', 'local', 'path')
