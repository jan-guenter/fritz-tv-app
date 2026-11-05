# FRITZ TV

A native C#/Avalonia desktop TV and radio player for FRITZ!Box DVB-C SAT>IP, targeting Windows 11 and Ubuntu 24.04+/TUXEDO OS.

## Application and acceptance

The application connects device/channel import, native TV/radio and recorded-file playback, broadcast EPG, disk timeshifting, and background recording/timer services. It includes favourites and channel ordering, audio/subtitle selection, fullscreen and teletext controls, English/German interfaces, and system-selected theme.

Closing the window stops viewing and leaves recordings and timers running in the tray. Quit exits the application. Future timers require an awake computer and a logged-in user.

[Hosted CI passed at `556d878`](https://github.com/jan-guenter/fritz-tv-app/actions/runs/41980712825): both platforms built with strict analysis, 130 tests passed with three native opt-in skips, and both installers built. Actual Linux embedded TV, teletext and timeshift controls passed from a clean `ebccec4` checkout. Windows VM access/networking and installer install/upgrade/removal are verified separately; see [recorded acceptance](docs/development/verification.md#recorded-acceptance) and the [implementation gates](docs/implementation-plan.md). Reachable-LAN SSDP, representative physical Windows GPU decoding and physical-machine sleep remain external acceptance checks.

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

GitHub Actions builds, tests, analyzes and packages Windows/Linux changes. Protected `main` requires a PR, current passing build/lint/installer checks and resolved review conversations; administrators are included and force pushes are disabled. CodeRabbit is installed for PR review; automatic review starts when a draft PR becomes ready. Renovate configuration is committed; its GitHub App installation is not yet verified and must be enabled for this repository before expecting update PRs. Release tags create draft releases with installers, checksums and dependency notices.

## Licence

GPL-3.0-or-later. This is an independent application; FRITZ!Box and FRITZ! are trademarks of their respective owners.
