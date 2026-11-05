# Windows development VM

This environment provides native Windows compilation, desktop automation, and opt-in FRITZ!Box testing. The application uses native Windows tooling; WSL is not required.

## Current verified status

On 2026-11-05 the pinned toolchain was installed and native Windows PowerShell 5.1 `-VerifyOnly` reported `AllRequiredToolsInstalled=True`. PowerShell 7 is the SSH shell and the timezone is `W. Europe Standard Time`. Authenticated MCP initialize/tool discovery, screenshots, accessibility inspection, application launch, mouse/keyboard input and independent saved-file readback passed. The MCP and repository runner login tasks were observed running in console session 1 after reboot.

All 32 scoped UDP forwards are configured; actual Windows TV/radio RTP reception and FFmpeg decoding passed. The private runner guard matches SHA-256 `f656f8db976aedaadc3b1f11f6ed50d97bf7251563a0b85dceeb71c3e87b387d`, and all thirteen admission-policy cases passed against the installed copy. This establishes the configured runner and policy; a successful trusted-main desktop workflow dispatch is a separate execution check.

The verified rollback checkpoint is the cold snapshot `fritz-tv-toolchain-ready-20261105`, UUID `80a59afa-472b-4b1e-9433-c453588d349c`, taken after the management consistency recovery below. Reapply later bootstrap/guard changes after restoring it. The initially reported pre-bootstrap snapshot was not a valid checkpoint.

[Recorded acceptance](verification.md#recorded-acceptance) distinguishes hosted checks, package lifecycle tests, Linux native rendering and Windows application acceptance. Current final Windows TV/radio/recording/timer results are maintained in the [implementation plan](../implementation-plan.md#current-execution-evidence).

## Initial preflight

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
.\scripts\dev\Initialize-WindowsVm.ps1 -VerifyOnly -WorkspaceRoot C:\src\fritz-tv-app
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

### Runner-local admission policy

The workflow's own `if` condition is supplemented by a fail-closed runner pre-job hook. `-ConfigureRunner` requires `Assert-TrustedWindowsJob.ps1` and `Test-TrustedWindowsJob.ps1` beside the bootstrap script, runs the policy tests, copies the guard to `%LOCALAPPDATA%\FritzTVDevelopment\runner-hooks`, verifies the copied SHA-256, and activates `ACTIONS_RUNNER_HOOK_JOB_STARTED` through the runner's local `.env`. The installed hook is outside the mutable checkout. Runner reconfiguration refuses to interrupt an active `Runner.Worker` job and restarts an idle listener so it reads the new environment.

Admission requires all of the following: this repository, `refs/heads/main`, `workflow_dispatch`, the exact `.github/workflows/windows-ui.yml@refs/heads/main` workflow reference, a self-hosted runner, a valid commit SHA, and a matching dispatch event payload. Pull requests, tags, other branches/workflows, missing context, and contradictory payloads exit nonzero before checkout/container/user steps. GitHub documents that a failing pre-job hook prevents the job from running. Action archives may already have been downloaded; this is not a promise that no untrusted bytes have reached the runner.

The runner source selects the hook path from its worker process environment (`JobExtension.cs`), invokes it with an empty workflow environment (`JobHookProvider.cs`), and then exports runner-provided GitHub context (`ScriptHandler.cs` / `GitHubContext.cs`). Workflow-level environment values therefore cannot disable this hook or replace its authoritative `GITHUB_*` fields. Keep the runner patched and review this behavior when upgrading it. `PSExecutionPolicyPreference=RemoteSigned` is scoped to the runner process/children so local checkout scripts can execute; no machine-wide execution policy is changed.

```powershell
.\scripts\dev\Test-TrustedWindowsJob.ps1
```

The policy test starts isolated PowerShell child processes for thirteen allow/deny cases and leaves the caller's environment unchanged. Hosted Windows CI runs it too. The bootstrap verification report records guard presence and hash; that report alone does not prove an actual runner-hook invocation. Validate one trusted dispatch and confirm denied contexts before treating the VM lane as operational.

References: [GitHub pre-job hooks](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/run-scripts), [runner hook selection](https://github.com/actions/runner/blob/main/src/Runner.Worker/JobExtension.cs), [hook handler environment](https://github.com/actions/runner/blob/main/src/Runner.Worker/JobHookProvider.cs), [script context export](https://github.com/actions/runner/blob/main/src/Runner.Worker/Handlers/ScriptHandler.cs).


Use GitHub-hosted Windows for clean restore/build/analyzer/headless tests and installer production. The VM adds native desktop/media/regression coverage. Record the exact commit SHA and upload TRX, screenshots, logs, and dependency inventory without local credential files.

## Build, test, and recovery

From `C:\src\fritz-tv-app`:

```powershell
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
```

Run the separate `tests/FritzTV.Windows.UiTests` project only in the interactive session; [verification commands](verification.md) show the exact Microsoft.Testing.Platform invocation and environment variables. The trusted-main `Windows desktop acceptance` workflow uses the same project. Live tests require the explicit descriptor URL and reserved RTP range. The bootstrap alone does not establish application-media acceptance.

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

Measured on 2026-11-05; reports retain UTC timestamps, versions and tested SHAs. These results supplement the historical preflight and incident record rather than treating their initial failures as successful checks.

| Gate | Measured status |
|---|---|
| SSH and console | Key-authenticated SSH; Guest Additions and unlocked console verified |
| Rollback protection | Initial reported snapshot rejected; actual runtime disk backed up and genuine cold snapshot `80a59afa-472b-4b1e-9433-c453588d349c` created after recovery |
| Tool bootstrap | All pinned tools installed; native PowerShell 5.1 parsing and `-VerifyOnly` succeeded; PowerShell 7 SSH shell and Berlin timezone verified |
| Authenticated MCP | Tool discovery, screenshot, accessibility, launch, click/type/save and independent readback passed; credentials remain outside Git |
| Windows live reception | TV/radio RTP-carried MPEG-TS and FFmpeg decoding passed with zero detected loss in recorded probes; HTTP/RTSP-only probes were not counted |
| Interactive runner | Registered `fritz-tv-win11`; interactive login task observed running; installed guard hash above and thirteen policy cases passed |
| Hosted compilation/tests/packages | CI at `b52a62d` passed all five protected-main checks; 157 unit/headless cases included 153 passes and four native opt-in skips. The separate Windows UI project passed nine fixture tests and skipped three interactive cases |
| Windows installer lifecycle | `3209b17`: install 0.1.0, upgrade 0.1.1, reinstall preserving autostart, and uninstall cleanup passed in a private install directory; package test did not launch the GUI |
| Native application acceptance | Live-only `fcf6b29` passed 1/1 in 51.470 seconds: playback/timeshift/warmup, source growth after viewing stopped, and MKV publication. Ten non-live smoke/fixture cases passed separately at `356b999`; five corrected retention cases passed separately over SSH. See the revision-specific [evidence](verification.md#recorded-acceptance) |
| LAN multicast and physical hardware | SSDP requires a reachable LAN; representative GPU decoding and physical sleep behavior require physical Windows hardware |

Official references: [OpenSSH configuration](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh-server-configuration), [Windows-MCP](https://github.com/CursorTouch/Windows-MCP), [uv tools](https://docs.astral.sh/uv/guides/tools/), [GitHub self-hosted runners](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners), [FlaUI](https://github.com/FlaUI/FlaUI).
