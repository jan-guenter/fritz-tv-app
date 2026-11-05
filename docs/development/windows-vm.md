# Windows development VM

This environment provides native Windows compilation, desktop automation, and opt-in FRITZ!Box testing. The application uses native Windows tooling; WSL is not required.

## Verified starting point

Preflight on 2026-11-05 found Windows 11 Pro x64, 8 GiB RAM, 12 logical processors, approximately 225 GiB free, VirtualBox Guest Additions 7.2.6, 3D acceleration, and an active `vboxuser` console session. Key-authenticated SSH works at:

```bash
ssh -p 2222 vboxuser@127.0.0.1
```

The VM uses NAT with guest address `10.0.2.15`. The host SSH forward is bound to `127.0.0.1:2222`, targeting guest TCP 22. A snapshot command returned UUID `37009a97-7fdf-4a1f-ab9e-a8e5e6b86257` for `fritz-tv-before-bootstrap-20261105`, but a split VirtualBox management connection was subsequently discovered. This snapshot is **not a verified rollback checkpoint** for the running guest.

HTTP descriptor access and RTSP `SETUP`/`PLAY` returned success from Windows, but the initial UDP probe received **zero** packets, including after UDP priming. Live streaming must pass a reception/decode check after forwarding and firewall configuration; an HTTP response or RTSP session is insufficient.

## Bootstrap

`scripts/dev/windows-toolchain.json` pins versions verified against official WinGet, PyPI, and GitHub release metadata. Update this manifest in reviewed PRs rather than substituting floating versions. WinGet checks its manifest's installer SHA-256; the optional GitHub runner archive is checked against its pinned SHA-256. The verification report also records Authenticode status and signer where available. A portable/unsigned component is reported as such rather than falsely described as signed.

Before changing a fresh VM, verify that host management and the live guest agree on VM state, then take a snapshot on the host:

```bash
VBoxManage snapshot "Windows 11" take fritz-tv-before-bootstrap-20261105
```

Stage `Initialize-WindowsVm.ps1` and `windows-toolchain.json` together in a user-local bootstrap directory. Before Git is available they can be copied with SCP/SFTP. Use `C:\src\fritz-tv-app` for the eventual checkout.

From elevated Windows PowerShell 5.1 or newer, run:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File C:\Users\vboxuser\AppData\Local\FritzTVDevelopment\bootstrap\Initialize-WindowsVm.ps1 -ConfigurationPath C:\Users\vboxuser\AppData\Local\FritzTVDevelopment\bootstrap\windows-toolchain.json
```

Supply an absolute `ConfigurationPath` when invoking a staged script through SSH. The bootstrap is also valid from its repository location after cloning.

The script installs/verifies:

- PowerShell 7, Git for Windows, .NET 10 SDK, and uv.
- MKVToolNix, FFmpeg/ffprobe, the x64 Visual C++ runtime, and Inno Setup.
- Pinned Python 3.14 through uv and Windows-MCP in its isolated uv tool environment.

It refreshes process PATH, sets PowerShell 7 as the native SSH default shell, sets `W. Europe Standard Time` (Berlin with DST), configures the scoped RTP rule, creates an authenticated loopback MCP configuration, and installs the MCP interactive login task. It does not change Windows autologon. The supplied VM already has `AutoAdminLogon=1`; this pre-existing setting is preserved, and the bootstrap does not create or retrieve login credentials.

Useful stages:

```powershell
# Install package tools first; defer desktop automation and firewall work.
.\scripts\dev\Initialize-WindowsVm.ps1 -SkipMcp -SkipFirewall

# Complete MCP/firewall configuration after packages exist.
.\scripts\dev\Initialize-WindowsVm.ps1 -SkipPackages

# Inventory installed tools and write a report without installing/configuring them.
.\scripts\dev\Initialize-WindowsVm.ps1 -VerifyOnly
```

Verification reports are written to `%LOCALAPPDATA%\FritzTVDevelopment\verification.json`. Reports include UTC timestamp, tool versions/paths/signatures, timezone, tested commit SHA when the checkout exists, task states, and MCP listeners. Desktop and live-media acceptance remain explicitly false until their separate checks pass. A report generated during installation can legitimately list tools as absent.

Reconnect after the SSH default shell changes. A new SSH process may still inherit an older PATH until Windows service/environment refresh; the bootstrap resolves known tool locations and refreshes its own PATH. Do not change global execution policy to run the script.

## Desktop control and credentials

Windows-MCP runs in the logged-in VirtualBox console, using a per-user login task named `windows-mcp-server`. Its released installer registers an `ONLOGON` task at limited run level. The bootstrap reuses the task and only restarts it when its managed configuration changes.

The server binds guest `127.0.0.1:8000`. Its random bearer token is stored in `%LOCALAPPDATA%\FritzTVDevelopment\windows-mcp-token.txt`; server configuration is in `%USERPROFILE%\.windows-mcp\config.toml`. ACLs restrict these files to the current user and SYSTEM. Never commit either file or print the token in CI logs.

Start the scoped host tunnel:

```bash
python3 scripts/dev/windows-vm-host.py start-tunnel
```

This creates an SSH control socket under `${XDG_STATE_HOME:-~/.local/state}/fritz-tv-vm`, binds host `127.0.0.1:18000`, and forwards to guest loopback 8000. Host-key checking stays enabled. Stop only this managed connection with:

```bash
python3 scripts/dev/windows-vm-host.py stop-tunnel
```

The equivalent foreground command is:

```bash
ssh -N -p 2222 -L 127.0.0.1:18000:127.0.0.1:8000 vboxuser@127.0.0.1
```

Configure Codex's local MCP server URL as `http://127.0.0.1:18000/mcp/`, with its bearer credential stored outside Git. Refresh MCP connections after configuration. Verify authenticated initialize/tool discovery, a screenshot, accessibility snapshot, launching Notepad, typing a disposable string, and closing without saving. This demonstrates the actual interactive desktop connection rather than only a listening TCP socket.

Keep the console logged in and unlocked for desktop checks. SSH commands and builds remain available when it is locked; UI automation does not. After reboot, confirm that the existing login behavior has restored the unlocked console; log in manually if it has not. RDP is optional for manual diagnostics and is not the automation session transport.

## RTP/RTCP networking

The Windows test configuration reserves UDP **50000–50031**, allocated in adjacent RTP/RTCP pairs. The `FritzTvVmRtp` guest firewall rule allows only this range, scoped to the FRITZ!Box and the NAT gateway source addresses (`192.168.0.1`, `10.0.2.2` by default). The bootstrap accepts `AllowedRtpSources` if packet capture establishes a different translated source.

Inspect the host forwarding change before applying it:

```bash
python3 scripts/dev/windows-vm-host.py forward-rtp --dry-run
python3 scripts/dev/windows-vm-host.py forward-rtp
```

The helper discovers the host's preferred source address for `192.168.0.1` using `ip route get`. It binds each UDP forward to that specific interface address, not every host interface. It changes only `fritz-tv-rtp-50000` through `fritz-tv-rtp-50031`, preserves SSH/unrelated rules, and skips matching rules on rerun. A VPN address change is handled by running it again.

If needed, explicitly select a known host-facing address or guest address:

```bash
python3 scripts/dev/windows-vm-host.py forward-rtp --host-address 192.168.0.205 --guest-address 10.0.2.15
```

Remove only the managed RTP forwards with `remove-rtp-forwards`; this cleanup also works while the VPN route is unavailable. Confirm the VM name/state in `VBoxManage list vms` and `showvminfo` match the SSH-connected machine before changing VM configuration.

Live acceptance must receive RTP packets containing correctly aligned MPEG-TS, discover PAT/PMT and the selected service, and decode both a TV and a radio sample. Record packet loss and discontinuities. Do not commit broadcaster captures. SSDP multicast remains a separate LAN validation because this VPN does not provide it.

## Interactive GitHub runner

Register a repository runner only with a short-lived registration token obtained through the trusted host's GitHub authentication. Save the token temporarily in a user-protected file outside the checkout; pass its path rather than putting the secret in a command line or source file.

```powershell
.\scripts\dev\Initialize-WindowsVm.ps1 -SkipPackages -SkipMcp -SkipFirewall -ConfigureRunner -RunnerTokenPath C:\Users\vboxuser\AppData\Local\FritzTVDevelopment\runner-registration-token.txt
```

The bootstrap verifies the official runner archive hash, registers runner `fritz-tv-win11` under `C:\actions-runner-fritz-tv`, and installs `FritzTV-GitHubRunner` as an interactive limited-user login task. Delete the short-lived token file after registration. No Windows service is created, and the existing Windows login policy is left unchanged.

VM desktop jobs must run only for trusted protected-main commits or explicitly dispatched trusted workflows. Fork PRs use hosted runners. Serialize the VM's FlaUI tests and exploratory desktop control. Before dispatch, release any exploratory automation and ensure the console is unlocked.

Use GitHub-hosted Windows for clean restore/build/analyzer/headless tests and installer production. The VM adds native desktop/media/regression coverage. Record the exact commit SHA and upload TRX, screenshots, logs, and dependency inventory without local credential files.

## Build, test, and recovery

From `C:\src\fritz-tv-app`, once the solution exists:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
```

Run the Windows native-desktop test category only in the interactive session; use the repository's Windows desktop workflow for the concrete test project/filter as it is implemented. Live tests require the explicit descriptor URL and test RTP port range. Neither gate is passed by the bootstrap alone.

For recovery, first stop active tests and preserve their reports. Restart the MCP task or managed SSH tunnel independently when one component fails. Inspect `%USERPROFILE%\.windows-mcp\server.log`, task history, the runner `_diag` directory, and the bootstrap verification report. Re-run bootstrap stages after fixing an interrupted package install; no credentials are replaced on a normal rerun. Apply a requested reboot only between test runs, then confirm the console login and check both login tasks.

Restore a VirtualBox snapshot only after shutdown and after preserving subsequent work. The snapshot resets guest disk state, tool installations, and machine-local credentials; re-establish MCP credentials/tunnel configuration afterward as needed. Take a new development-ready snapshot only after the recorded acceptance checks pass.

## VirtualBox management consistency incident

During setup, the original VirtualBox GUI, service, and VM remained alive while a second management instance recreated the pathname IPC files under `/tmp/.vbox-jan-ipc`. The original service retained a deleted lock file and its old Unix listener; the current pathname did not connect to it. CLI-reported power state could therefore disagree with the running Windows guest.

Run VirtualBox management from the actual host desktop environment. Agent commands must use the approved host execution path rather than a sandbox with isolated temporary/IPC state. The host helper checks live same-user service lock descriptors against the current IPC directory and refuses a deleted/mismatched lock before invoking `VBoxManage`. If CLI state conflicts with SSH or runtime process evidence, stop issuing management/configuration/snapshot commands until the connection is consistent. Do not delete IPC files or kill the VM to repair discovery.

Read-only evidence on 2026-11-05 established:

- Runtime `VBox.log` opened `Windows 11.vdi` (base medium `06b70394-b614-4f29-b8b5-d36653023263`). This base continued changing during Windows setup.
- On-disk `.vbox`/`.vbox-prev` selected snapshot `37009a97-7fdf-4a1f-ab9e-a8e5e6b86257` and differencing medium `9cf83971-28d2-433b-b72c-00ad00cb09fc`, while the original service reported that the machine had no snapshots.
- That differencing image remained 2 MiB with its creation-time modification timestamp. It was not the image opened by the running VM.

Recovery must preserve guest work: save diagnostic/configuration copies, finish active installations, cleanly shut Windows down, wait for the VM process to exit, close the original manager, and let its service exit. Inspect the resulting disk attachment before restarting a single consistent management instance. A stopped-VM backup of the actual base disk is preferable to trusting the inconsistent snapshot. Do not restore, merge, or delete the snapshot files during diagnosis. The original service may rewrite the authoritative settings during clean shutdown; classify unused files only after that state is inspected.

Oracle documents that the service owns configuration and exits after its last client disconnects: [VirtualBox host processes and troubleshooting](https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/Troubleshooting.html).

## Gate evidence

| Gate | Initial evidence/status |
|---|---|
| SSH/key access | Passed on 2026-11-05 |
| Guest Additions/interactive console | Passed preflight |
| Pre-bootstrap snapshot | Command returned success, but protection is unverified because management and runtime state diverged |
| Bootstrap syntax/report | Parsed by native Windows PowerShell 5.1 with zero syntax errors; actual staged `-VerifyOnly` succeeded with explicit and default manifest paths (latest check 2026-11-05T01:57:15Z) |
| Installed toolchain | Installation/verification in progress; use current machine report |
| Authenticated MCP desktop interaction | Pending actual handshake and smoke test |
| RTP reception and TV/radio decoding | Pending NAT/firewall configuration and media test |
| Interactive GitHub runner/native UI tests | Pending registration and workflow execution |
| Physical GPU/power behavior | Requires physical Windows hardware |

Official references: [OpenSSH configuration](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh-server-configuration), [Windows-MCP](https://github.com/CursorTouch/Windows-MCP), [uv tools](https://docs.astral.sh/uv/guides/tools/), [GitHub self-hosted runners](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners), [FlaUI](https://github.com/FlaUI/FlaUI).
