# FRITZ TV

A native C#/Avalonia desktop TV and radio player for FRITZ!Box DVB-C SAT>IP, targeting Windows 11 and Ubuntu 24.04+/TUXEDO OS.

## Development status

Implementation is being delivered through the gates in [AGENTS.md](AGENTS.md). The desktop connects device/channel management, native LibVLC playback, indexed timeshifting, broadcast guide collection, recording and timer services. Automated fixtures and opt-in native/live checks cover these components; track the measured platform gates and remaining limitations in the development documentation before treating a feature as accepted.

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

## Licence

GPL-3.0-or-later. This is an independent application; FRITZ!Box and FRITZ! are trademarks of their respective owners.
