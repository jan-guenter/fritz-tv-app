# FRITZ TV

A native C#/Avalonia desktop TV and radio player for FRITZ!Box DVB-C SAT>IP, targeting Windows 11 and Ubuntu 24.04+/TUXEDO OS.

## Application and acceptance

The application connects device/channel import, native TV/radio and recorded-file playback, broadcast EPG, disk timeshifting, and background recording/timer services. It includes favourites and channel ordering, audio/subtitle selection, fullscreen and teletext controls, English/German interfaces, and system-selected theme.

Closing the window stops viewing and leaves recordings and timers running in the tray. Quit exits the application. Future timers require an awake computer and a logged-in user.

[Hosted CI passed at `e063016`](https://github.com/jan-guenter/fritz-tv-app/actions/runs/41995058270): both platforms built with strict analysis, 150 tests passed with four native opt-in skips, and both installers built. The separate Windows UI project passed five fixture tests with three interactive cases skipped. Actual Linux embedded TV, teletext and timeshift controls passed from a clean `ebccec4` checkout. Windows background TV scheduling and radio capture/finalization passed at `cd207dc`; very short buffered TV finalization remains under investigation. See [recorded acceptance](docs/development/verification.md#recorded-acceptance) for revision-specific application, VM and installer results. Reachable-LAN SSDP, representative physical Windows GPU decoding and physical-machine sleep remain external acceptance checks.

## Build and verify

Install the SDK selected by `global.json`. On Linux install the maintained LibVLC runtime and plugins (`libvlc5`, `vlc-plugin-base`, `vlc-plugin-video-output`) plus MKVToolNix for recording finalization. Windows publish output includes the LibVLC runtime; installer packaging also bundles the verified MKVToolNix distribution and dependency notices. Ubuntu systems whose default repositories provide MKVToolNix older than 96 need the [maintained vendor repository](https://mkvtoolnix.download/downloads.html#ubuntu).

```sh
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
dotnet run --project src/FritzTV.Desktop
```

The first developer restore after deliberately changing package references uses `dotnet restore --force-evaluate`, then commits regenerated lock files. The desktop keeps platform-specific Windows/Linux locks; generate each with its native OS graph. See [verification and packaging commands](docs/development/verification.md).

See [Windows VM setup](docs/development/windows-vm.md) for remote access, desktop control, live-stream networking, and test artifact collection. Live-device checks are opt-in and use `http://192.168.0.1:49000/satipdesc.xml` in the current development environment; ordinary CI uses synthetic fixtures.

## GitHub workflow

GitHub Actions builds, tests, analyzes and packages Windows/Linux changes. Protected `main` requires a PR, current passing build/lint/installer checks and resolved review conversations; administrators are included and force pushes are disabled. CodeRabbit is installed and configured for automatic PR review. Its review of the initial PR was [skipped](https://github.com/jan-guenter/fritz-tv-app/pull/1#issuecomment-6647891537) because 232 selected files exceed the account's 150-file limit and review capacity is unavailable. Three completed CLI reviews produced 38 reports; corrections and validation are recorded in the [review log](docs/development/code-review.md). Renovate configuration is committed; its GitHub App installation is not yet verified and must be enabled for this repository before expecting update PRs. Release tags create draft releases with installers, checksums and dependency notices.

## Licence

GPL-3.0-or-later. This is an independent application; FRITZ!Box and FRITZ! are trademarks of their respective owners.
