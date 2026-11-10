import importlib.util
import pathlib
import unittest

MODULE = pathlib.Path(__file__).resolve().parents[2] / 'MaintenanceRunner' / 'host_guard.py'
spec = importlib.util.spec_from_file_location('host_guard', MODULE)
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)

class HostGuardTests(unittest.TestCase):
    def test_rejects_service_running_or_mask_missing(self):
        for state in [guard.ServiceState('masked', 'active', 0), guard.ServiceState('masked-runtime', 'inactive', 0), guard.ServiceState('masked', 'inactive', 21), guard.ServiceState('masked', 'inactive', 0, 'exited'), guard.ServiceState('masked', 'inactive', 0, 'dead', 'loaded')]:
            with self.assertRaises(guard.GuardError): guard.require_stopped_masked(state)
        guard.require_stopped_masked(guard.ServiceState('masked', 'inactive', 0))

    def test_mountinfo_pins_instance_source_and_filesystem(self):
        data = '48 23 0:44 / /mnt/T7 rw,relatime shared:17 - fuseblk /dev/sdb1 rw,user_id=0\n'
        mount = guard.select_mount(data, 48)
        guard.require_mount(mount, '/mnt/T7', 'fuseblk', '/dev/sdb1')
        for identity in [(49, '/mnt/T7', 'fuseblk', '/dev/sdb1'), (48, '/mnt/T7', 'fuseblk', '/dev/sdc1'), (48, '/mnt/T7', 'fuse', '/dev/sdb1')]:
            with self.assertRaises(guard.GuardError): guard.require_mount(guard.select_mount(data, identity[0]), *identity[1:])

    def test_rejects_other_mount_namespace(self):
        guard.require_namespace('mnt:[42]', 'mnt:[42]')
        with self.assertRaises(guard.GuardError): guard.require_namespace('mnt:[42]', 'mnt:[43]')

    def test_never_authorizes_operations(self):
        self.assertEqual(guard.execution_state(), 'blocked-runner-adapter-unimplemented')

if __name__ == '__main__': unittest.main()
