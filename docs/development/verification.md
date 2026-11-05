# Verification and packaging

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
& "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe" '/DAppVersion=0.1.0' packaging/windows.iss
```

The bundle script verifies the pinned official MKVToolNix ZIP hash, retains its original DLLs/licences and verifies `mkvmerge --version`. The notice collector records restored package versions and attribution, retains original licence/notice files, includes the selected self-contained .NET runtime, and adds SPDX references from NuGet's official licence service. Linux packages depend on the maintained distribution LibVLC/plugins and MKVToolNix 96 or newer; configure the [official MKVToolNix Ubuntu repository](https://mkvtoolnix.download/downloads.html#ubuntu) when the distribution's version is older. Package validation results must be recorded separately from a successful publish/build.
