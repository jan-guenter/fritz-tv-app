"""Regression tests for the scoped, idempotent VirtualBox forwarding planner."""
import importlib.util
from pathlib import Path
import unittest
import tempfile

PATH = Path(__file__).resolve().parents[2] / "scripts/dev/windows-vm-host.py"
SPEC = importlib.util.spec_from_file_location("windows_vm_host", PATH)
HOST = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HOST)


class ForwardingTests(unittest.TestCase):
    def test_parse_existing_rules_and_preserve_ssh(self):
        state, rules = HOST.parse_vm_info('VMState="running"\nForwarding(0)="ssh,tcp,127.0.0.1,2222,,22"\n')
        self.assertEqual("running", state)
        self.assertEqual(("ssh", "tcp", "127.0.0.1", "2222", "", "22"), rules["ssh"])
        changes = HOST.forwarding_changes(rules, "192.168.0.205", "10.0.2.15")
        self.assertEqual(32, len(changes))
        self.assertTrue(all(action == "add" for action, _ in changes))
        self.assertFalse(any(value == "ssh" for _, value in changes))

    def test_matching_forwards_are_idempotent(self):
        changes = HOST.forwarding_changes({}, "192.168.0.205", "10.0.2.15")
        existing = {value.split(",", 1)[0]: tuple(value.split(",")) for _, value in changes}
        self.assertEqual([], HOST.forwarding_changes(existing, "192.168.0.205", "10.0.2.15"))

    def test_route_change_replaces_only_owned_rules(self):
        existing = {"fritz-tv-rtp-50000": ("fritz-tv-rtp-50000", "udp", "192.168.0.204", "50000", "10.0.2.15", "50000"),
                    "ssh": ("ssh", "tcp", "127.0.0.1", "2222", "", "22")}
        changes = HOST.forwarding_changes(existing, "192.168.0.205", "10.0.2.15")
        self.assertEqual(("delete", "fritz-tv-rtp-50000"), changes[0])
        self.assertEqual(33, len(changes))
        self.assertFalse(any(value == "ssh" for _, value in changes))

    def test_remove_only_owned_ports_without_a_vpn_route(self):
        existing = {"ssh": ("ssh", "tcp", "127.0.0.1", "2222", "", "22"),
                    "fritz-tv-rtp-50000": ("fritz-tv-rtp-50000", "udp", "192.168.0.205", "50000", "10.0.2.15", "50000"),
                    "fritz-tv-rtp-unrelated": ("fritz-tv-rtp-unrelated", "udp", "192.168.0.205", "51000", "10.0.2.15", "51000")}
        self.assertEqual([("delete", "fritz-tv-rtp-50000")], HOST.forwarding_changes(existing, None, "", True))

    def test_refuses_broad_or_invalid_binding(self):
        for source in ("0.0.0.0", "127.0.0.1", "239.255.255.250"):
            with self.subTest(source=source), self.assertRaises(ValueError):
                HOST.forwarding_changes({}, source, "10.0.2.15")

    def test_missing_vm_state_is_an_error(self):
        with self.assertRaises(ValueError):
            HOST.parse_vm_info('name="other"')


class ManagementContextTests(unittest.TestCase):
    def make_service(self, root, lock_target):
        process = root / "proc" / "123"
        (process / "fd").mkdir(parents=True)
        (process / "cmdline").write_bytes(b"/usr/lib/virtualbox/VBoxSVC\0--auto-shutdown\0")
        (process / "fd" / "1").symlink_to(lock_target)
        return root / "proc"

    def test_same_lock_is_safe(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            ipc = root / ".vbox-test-ipc"
            ipc.mkdir()
            lock = ipc / "lock"
            lock.touch()
            proc = self.make_service(root, lock)
            HOST.ensure_management_context(proc, ipc)

    def test_deleted_or_private_ipc_lock_is_rejected(self):
        for mode in ("different", "deleted", "missing"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                ipc = root / ".vbox-test-ipc"
                ipc.mkdir()
                old = root / ".vbox-old-ipc"
                old.mkdir()
                old_lock = old / ("lock (deleted)" if mode == "deleted" else "lock")
                old_lock.touch()
                if mode != "missing":
                    (ipc / "lock").touch()
                proc = self.make_service(root, old_lock)
                vm = proc / "124"
                vm.mkdir()
                (vm / "cmdline").write_bytes(b"/usr/lib/virtualbox/VirtualBoxVM\0--startvm\0test-uuid\0")
                with self.assertRaises(ValueError):
                    HOST.ensure_management_context(proc, ipc)


if __name__ == "__main__":
    unittest.main()
