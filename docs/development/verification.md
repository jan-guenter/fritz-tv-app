# Verification and packaging

## Recorded acceptance

Results below were measured on 2026-11-05. They identify the tested revision and distinguish application behavior from package/bootstrap checks. Private reports retain UTC timestamps, tool versions and captured diagnostics; broadcaster media/screenshots and machine credentials remain outside Git.

| Revision/check | Measured result |
|---|---|
| [`e063016` hosted CI](https://github.com/jan-guenter/fritz-tv-app/actions/runs/41995058270) | Windows/Linux strict builds, analysis, formatting, dependency audit and script/workflow lint passed. Unit/headless suite: 154 total, 150 passed, four native opt-in skips. Both installer jobs passed. The separate Windows UI project passed five fixture tests and skipped three interactive cases on the hosted Windows runner. |
| `b12c72f` Windows UIA polling | Strict Windows-targeted build passed with zero warnings/errors. Three regressions passed on the actual VM through SSH: transient provider recovery, persistent outage bounded by the original deadline, and immediate propagation of an unrelated COM error. These fixture checks did not automate the desktop. |
| Clean `ebccec43b543ee681a986fbc05e2c19b54e97040` Linux GUI | Locked restore and Release Desktop build passed without warnings/errors. From 06:24:31–06:26:21 UTC, whole-display X11/Xvfb captures verified embedded live TV; opaque/transparent teletext page 100; explicit Close; pause while capture continued; paused rewind; resume; Go Live; expanded guide with continuing video and visible Quit. Quit exited 0. SDK 10.0.403 and system LibVLC/plugins `3.0.20-3build6` were recorded. |
| `cd207dc` Windows background TV timer | In console session 1 the app ran with `--background` and recorded without watching. The 07:30:39.933179–07:30:59.933179 UTC interval produced a 20.000-second MKV with H.264, three AC3 tracks, DVB subtitles and teletext-derived SRT. State was Completed with no error/warning. FFmpeg exited 0 with known cold open-GOP startup diagnostics. |
| `cd207dc` Windows radio recording | Capture grew 721,920 bytes during three seconds after listening stopped. Explicit Stop finalized a 373.312-second German AAC MKA and FFmpeg decoded without errors. Four lost RTP packets and one continuity gap correctly marked it Partial; the 83,344,724-byte source TS was retained and recovery eligibility cleared. |
| `2a9fe43` Windows native TV | Moving playback, advancing time, pause/rewind, warmup-cover bounds/movement/minimize/restore and resume passed. Capture continued after viewing stopped. The full live case has not passed: finalization of a very short buffered TV capture is under investigation, with opt-in private source retention for diagnosis. |
| `3209b173fb3d247591ec9e24638f9a4d163e3e1a` Linux packages | Actual dpkg install 0.1.0, upgrade 0.1.1, removal and purge passed in an isolated user namespace/private root. Both desktop entries and 66 package licence records validated; root ownership and replaced versioned assembly were checked. Host package status seeded dependency data; desktop cache triggers were disabled and no host profile/installation was modified. |
| `3209b17` Windows installers | Actual silent install 0.1.0, upgrade 0.1.1, reinstall preserving autostart and uninstall passed. Product versions, bundled MKVToolNix 103.0 and 68 licence records were checked. Uninstall removed files, product registration and the app-owned autostart value; the previous autostart value was restored. The package lifecycle test did not launch the application. |
| Windows VM infrastructure | Pinned tools and native bootstrap verification passed; authenticated interactive MCP and runner tasks worked after reboot. All 32 RTP forwards and TV/radio UDP decode probes passed. Installed admission-guard SHA-256 is `f656f8db976aedaadc3b1f11f6ed50d97bf7251563a0b85dceeb71c3e87b387d`; thirteen allow/deny policy cases passed. |

Native decoder startup/restart can report open-GOP reference or programme-table continuity warnings. The GUI evidence establishes visible decoding and controls, not warning-free native output. Linux native rendering and backend probes are separate results; neither establishes Windows physical GPU or physical-machine power behavior. Final Windows application TV/radio/recording/timer results and review status are maintained in the [implementation plan](../implementation-plan.md#current-execution-evidence). SSDP multicast still requires a reachable LAN.

Protected `main` requires a PR with these exact current checks, resolved conversations, and administrator enforcement; force pushes are disabled:

- `Build and test (ubuntu-24.04)`
- `Build and test (windows-2025)`
- `Workflow and packaging lint`
- `Installer packages / package (ubuntu-24.04)`
- `Installer packages / package (windows-2025)`

The [automatic CodeRabbit PR status](https://github.com/jan-guenter/fritz-tv-app/pull/1#issuecomment-6647891537) skipped review at `b12c72f`: 232 selected files exceeded the 150-file limit, and review credits/capacity were insufficient. Three completed CLI reviews produced 38 reports; their corrections and validation are documented in the [review log](code-review.md). No subsequent automatic-review pass is claimed.

## Build and test commands

The root commands in `AGENTS.md` are the hosted compilation, analyzer, unit/headless and formatting gate. .NET 10 uses Microsoft.Testing.Platform with xUnit v3; native UI tests are a separate Windows-only project so normal headless tests do not need an interactive desktop.

```text
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test --no-build -c Release --report-trx --results-directory artifacts/test-results
dotnet format --verify-no-changes --no-restore
```

Python helpers use the standard library. Install the pinned linter with `python -m pip install -r requirements-lint.txt`, then run:

```text
ruff check packaging/collect-licenses.py scripts/dev/windows-vm-host.py tests/dev
ruff format --check packaging/collect-licenses.py scripts/dev/windows-vm-host.py tests/dev
python -m unittest discover -s tests/dev -v
```

Hosted Windows also compiles the FlaUI project, checks its formatting and runs it with native cases skipped. Actual desktop tests require the unlocked VM console, no exploratory FRITZ TV instance and exclusive ownership of desktop automation. Tests launch their own process with isolated temporary data, timeshift and recording folders through `FRITZTV_DATA_DIRECTORY`, preserve diagnostic logs in `FRITZTV_UI_ARTIFACTS` when set, and remove captured media afterward. They exercise window geometry against the Windows work area, visible controls, input validation, English/German labels, close-to-tray lifetime and second-launch activation.

From PowerShell 7 in the interactive VM checkout:

```powershell
dotnet restore tests/FritzTV.Windows.UiTests --locked-mode
dotnet publish src/FritzTV.Desktop -c Release -r win-x64 --self-contained true --no-restore -o artifacts/app
$env:FRITZTV_UI_APP = Join-Path $PWD 'artifacts/app/FritzTV.Desktop.exe'
$env:FRITZTV_UI_ARTIFACTS = Join-Path $PWD 'artifacts/ui-logs'
$env:FRITZTV_RTP_FIRST_PORT = '50000'
$env:FRITZTV_RTP_LAST_PORT = '50031'
dotnet test --project tests/FritzTV.Windows.UiTests/FritzTV.Windows.UiTests.csproj -c Release --report-trx --results-directory artifacts/test-results
```

Optionally set `FRITZTV_LIVE_DEVICE` to a reachable HTTP(S) descriptor URL before the test command. `FRITZTV_LIVE_CHANNEL` defaults to `ZDF HD`. This adds a real-device case requiring an advancing native playback clock, pause/resume/Go Live, capture that grows after viewing stops, and a finalized Matroska file. Descriptor/RTSP success alone does not satisfy that test. The trusted-main `Windows desktop acceptance` workflow accepts the optional descriptor as an input. Fork PRs never use the VM.

For local diagnosis of a failed live recording, media retention is an explicit opt-in. Before running the same test command, choose a private absolute artifact directory outside the isolated profile and checkout:

```powershell
$env:FRITZTV_UI_ARTIFACTS = Join-Path $env:LOCALAPPDATA 'FritzTVDevelopment/private-ui-diagnostics'
$env:FRITZTV_UI_RETAIN_MEDIA = '1'
```

After stopping the owned app, cleanup copies its entire isolated profile to `<artifacts>/<profile-name>/profile` before deleting the temporary profile. The default removes captured media; CI always disables this copy even if the opt-in is set. Retained profiles can contain broadcaster recordings, so keep them private and do not upload them as CI artifacts. Remove the opt-in environment variable after diagnosis.

Manual recurring timers retain their originally entered local date/time and timezone, including when the very first occurrence falls in a spring clock gap. Gap resolution shifts the interval by the clock gap while preserving its wall duration; repeated autumn times use the first occurrence. EPG presets retain the broadcaster's exact UTC instants, including the second autumn occurrence. Saved timer definitions remain visible and cancellable beyond the scheduler's upcoming-instance horizon.

Synthetic native remux/decode and power tests are documented in [runtime services](runtime-services.md). They do not establish physical GPU or physical sleep behavior. SSDP needs a separate reachable LAN multicast test. Record the exact SHA, UTC timestamp, tool versions, TRX, screenshots and logs for measured results; do not publish broadcaster captures or machine-local credentials.

Every hosted PR/push CI run builds both installers through the reusable package workflow. A semver release tag runs those same builds and creates a **draft** release with checksums. For local Linux packaging:

```sh
bash packaging/build-deb.sh 0.1.0
```

For local Windows packaging, with pinned Inno Setup 6.7.3 installed:

```powershell
dotnet publish src/FritzTV.Desktop -c Release -r win-x64 --self-contained true --no-restore -p:Version=0.1.0 -o artifacts/publish/win-x64
./packaging/Bundle-WindowsDependencies.ps1 -PublishDirectory artifacts/publish/win-x64
python packaging/collect-licenses.py src/FritzTV.Desktop/obj/project.assets.json artifacts/publish/win-x64/licenses --runtime win-x64
$iscc = @(
  "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe"
  "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe"
) | Where-Object {
  if (-not (Test-Path -LiteralPath $_)) { return $false }
  $version = (Get-Item -LiteralPath $_).VersionInfo
  $version.ProductMajorPart -eq 6 -and $version.ProductMinorPart -eq 7 -and
    $version.ProductBuildPart -eq 3 -and $version.ProductPrivatePart -eq 0
} | Select-Object -First 1
if (-not $iscc) { throw 'Install the pinned Inno Setup compiler first.' }
& $iscc '/DAppVersion=0.1.0' packaging/windows.iss
```

The bundle script verifies the pinned official MKVToolNix ZIP hash, retains its original DLLs/licences and verifies `mkvmerge --version`. The notice collector records restored package versions and attribution, retains original licence/notice files, includes the selected self-contained .NET runtime, and adds SPDX references from NuGet's official licence service. Linux packages depend on the maintained distribution LibVLC/plugins and MKVToolNix 96 or newer; configure the [official MKVToolNix Ubuntu repository](https://mkvtoolnix.download/downloads.html#ubuntu) when the distribution's version is older. Package validation results must be recorded separately from a successful publish/build.
