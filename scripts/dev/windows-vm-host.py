#!/usr/bin/env python3
"""Manage only FRITZ TV-owned VirtualBox UDP forwards and its SSH tunnel.

Uses Python's standard library. No VM/host changes happen without an explicit
subcommand. SSH host-key verification stays enabled; credentials are never read.
"""

from __future__ import annotations

import argparse
import csv
import getpass
import ipaddress
import json
import os
import re
import subprocess
import sys
from pathlib import Path

RULE_PREFIX = "fritz-tv-rtp-"


def parse_vm_info(text: str) -> tuple[str, dict[str, tuple[str, ...]]]:
    state = ""
    rules: dict[str, tuple[str, ...]] = {}
    for line in text.splitlines():
        if line.startswith('VMState="'):
            state = line.removeprefix('VMState="').removesuffix('"')
        if re.match(r"Forwarding\(\d+\)=", line):
            value = line.split("=", 1)[1].strip('"')
            fields = tuple(next(csv.reader([value])))
            if len(fields) != 6:
                raise ValueError("Unexpected VirtualBox forwarding rule format")
            rules[fields[0]] = fields
    if not state:
        raise ValueError("VirtualBox did not report a VM state")
    return state, rules


def forwarding_changes(
    current: dict[str, tuple[str, ...]], source: str | None, guest: str, remove: bool = False
) -> list[tuple[str, str]]:
    if remove:
        return [("delete", f"{RULE_PREFIX}{port}") for port in range(50000, 50032) if f"{RULE_PREFIX}{port}" in current]
    address = ipaddress.IPv4Address(source)
    target = ipaddress.IPv4Address(guest)
    if address.is_unspecified or address.is_loopback or address.is_multicast:
        raise ValueError("RTP forwards must bind the host's specific FRITZ!Box-facing IPv4 address")
    if target.is_unspecified or target.is_multicast or target.is_loopback:
        raise ValueError("Guest IPv4 address is invalid")
    changes: list[tuple[str, str]] = []
    for port in range(50000, 50032):
        name = f"{RULE_PREFIX}{port}"
        desired = (name, "udp", str(address), str(port), str(target), str(port))
        existing = current.get(name)
        if existing != desired:
            if existing:
                changes.append(("delete", name))
            changes.append(("add", ",".join(desired)))
    return changes


def ensure_management_context(proc_root: Path = Path("/proc"), ipc_directory: Path | None = None) -> None:
    """Refuse a private/stale IPC path before VBoxManage can start another service."""
    # VirtualBox defines this IPC location; its lock ownership and inode are inspected below.
    directory = ipc_directory or Path("/tmp") / f".vbox-{getpass.getuser()}-ipc"  # noqa: S108
    try:
        current = (directory / "lock").stat()
    except FileNotFoundError:
        current = None
    for process in proc_root.iterdir():
        if not process.name.isdigit():
            continue
        try:
            if process.stat().st_uid != os.getuid():
                continue
            command = (process / "cmdline").read_bytes().split(b"\0", 1)[0]
            if Path(os.fsdecode(command)).name != "VBoxSVC":
                continue
            for descriptor in (process / "fd").iterdir():
                target = os.readlink(descriptor)
                if "/.vbox-" not in target or not target.endswith(("/lock", "/lock (deleted)")):
                    continue
                if target.endswith(" (deleted)") or current is None:
                    raise ValueError(
                        "Live VBoxSVC has an unavailable IPC lock; "
                        "repair the host management context before running VBoxManage"
                    )
                original = descriptor.stat()
                if (original.st_dev, original.st_ino) != (current.st_dev, current.st_ino):
                    raise ValueError(
                        "Live VBoxSVC and the current IPC directory use different locks; "
                        "refusing to start another management service"
                    )
        except (FileNotFoundError, ProcessLookupError):
            continue


def run(arguments: list[str], *, capture: bool = False) -> subprocess.CompletedProcess[str]:
    if Path(arguments[0]).name == "VBoxManage":
        ensure_management_context()
    # All callers pass structured internal executable/argument lists, with no shell expansion.
    return subprocess.run(arguments, check=True, text=True, capture_output=capture, shell=False)  # noqa: S603


def discover_route(destination: str) -> str:
    ipaddress.IPv4Address(destination)
    result = run(["ip", "-j", "route", "get", destination], capture=True)
    routes = json.loads(result.stdout)
    if not routes:
        raise ValueError("No host route to the FRITZ!Box")
    source = routes[0].get("prefsrc") or routes[0].get("src")
    if not source:
        raise ValueError("The route has no preferred source; supply --host-address")
    return str(ipaddress.IPv4Address(source))


def configure_forwards(args: argparse.Namespace) -> None:
    info = run(["VBoxManage", "showvminfo", args.vm, "--machinereadable"], capture=True)
    state, rules = parse_vm_info(info.stdout)
    remove = args.command == "remove-rtp-forwards"
    source = None if remove else (args.host_address or discover_route(args.fritz_address))
    changes = forwarding_changes(rules, source, args.guest_address, remove)
    for action, value in changes:
        if state in {"running", "paused"}:
            command = ["VBoxManage", "controlvm", args.vm, "natpf1"]
        elif state in {"poweroff", "saved", "aborted"}:
            command = ["VBoxManage", "modifyvm", args.vm, "--natpf1"]
        else:
            raise ValueError(f"Cannot change forwarding while VM state is {state}")
        command += ["delete", value] if action == "delete" else [value]
        print(json.dumps(command))
        if not args.dry_run:
            run(command)
    if not changes:
        print("FRITZ TV UDP forwards already match the requested state.")


def tunnel(args: argparse.Namespace) -> None:
    state = Path(os.environ.get("XDG_STATE_HOME", Path.home() / ".local/state")) / "fritz-tv-vm"
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    state.chmod(0o700)
    socket = state / "ssh-control"
    base = [
        "ssh",
        "-p",
        str(args.ssh_port),
        "-o",
        "BatchMode=yes",
        "-o",
        "StrictHostKeyChecking=yes",
        "-S",
        str(socket),
    ]
    target = f"{args.user}@127.0.0.1"
    if args.command == "stop-tunnel":
        run([*base, "-O", "exit", target])
        return
    # The SSH executable and options are constructed above; user values remain separate arguments.
    check = subprocess.run([*base, "-O", "check", target], text=True, capture_output=True, check=False, shell=False)  # noqa: S603
    if check.returncode == 0:
        print("The managed SSH tunnel is already running.")
        return
    run(
        [
            *base,
            "-f",
            "-N",
            "-M",
            "-o",
            "ExitOnForwardFailure=yes",
            "-o",
            "ServerAliveInterval=30",
            "-o",
            "ServerAliveCountMax=3",
            "-L",
            f"127.0.0.1:{args.local_port}:127.0.0.1:{args.guest_port}",
            target,
        ]
    )
    print(f"MCP tunnel: http://127.0.0.1:{args.local_port}/mcp/")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["forward-rtp", "remove-rtp-forwards", "start-tunnel", "stop-tunnel"])
    parser.add_argument("--vm", default="Windows 11")
    parser.add_argument("--fritz-address", default="192.168.0.1")
    parser.add_argument("--host-address")
    parser.add_argument("--guest-address", default="10.0.2.15")
    parser.add_argument("--dry-run", action="store_true", help="Print forwarding changes without applying them")
    parser.add_argument("--user", default="vboxuser")
    parser.add_argument("--ssh-port", type=int, default=2222)
    parser.add_argument("--local-port", type=int, default=18000)
    parser.add_argument("--guest-port", type=int, default=8000)
    args = parser.parse_args()
    for port in (args.ssh_port, args.local_port, args.guest_port):
        if not 1 <= port <= 65535:
            parser.error("Ports must be in 1..65535")
    if args.dry_run and args.command in {"start-tunnel", "stop-tunnel"}:
        parser.error("--dry-run applies only to forwarding commands")
    try:
        if args.command in {"start-tunnel", "stop-tunnel"}:
            tunnel(args)
        else:
            configure_forwards(args)
        return 0
    except (OSError, subprocess.CalledProcessError, ValueError) as error:
        print(f"windows-vm-host: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
