# FRITZ TV contributor instructions

## Product and scope

Build a GPL-3.0-or-later TV/radio player and recorder for FRITZ!Box DVB-C SAT>IP devices. Support Windows 11 x64 and Ubuntu 24.04+ x64 (including TUXEDO OS); use X11/XWayland initially on Linux. Discover generic SAT>IP servers but guarantee playback/channel import for FRITZ!Box devices first. Ship English and German UI with system-selected language and theme.

Implement and validate the Windows development VM first. The VM is reachable as `ssh -p 2222 vboxuser@127.0.0.1`. Use native Windows tooling and its logged-in VirtualBox console. Do not assume successful HTTP or RTSP control proves UDP reception.

## Stack and dependency policy

- Start with .NET 10 / C# 14, Avalonia 12 with Fluent theme, CommunityToolkit.Mvvm, stable LibVLCSharp / LibVLC 3, Microsoft.Data.Sqlite, and .NET Generic Host.
- Use MKVToolNix `mkvmerge` for lossless Matroska finalization. FFmpeg/ffprobe are development/diagnostic tools.
- Use current stable xUnit, Avalonia headless tests, and FlaUI UIA3 for Windows desktop regression tests.
- Recheck official upstream stable releases before pinning versions. Avoid previews and floating package versions. Keep SDK/tool pins, central NuGet versions, and locked restores reproducible.
- Adopt subsequent stable .NET majors through tested upgrade PRs; configure Renovate for dependency and GitHub Actions updates.
- Keep Linux native dependencies current through maintained distribution/vendor repositories. Bundle .NET and Windows native media dependencies in Windows distribution.
- Verify official installer signatures/checksums where provided. Never commit credentials, tokens, SSH keys, machine-specific settings, or captured broadcaster content.

## Architecture and ownership

- Separate Core (UI-independent models/policies/interfaces), Infrastructure (network/media/database/OS adapters), Desktop (Avalonia/MVVM), and tests.
- Hosted background services own discovery, channels, tuner allocation, RTSP/RTP/RTCP, DVB guide collection, timeshifting, scheduling, and recording.
- Use cancellation-aware asynchronous interfaces and immutable status snapshots. Keep database access, native player objects, and transport ownership out of view models.
- One application process continues in the tray after its window closes. Closing the window stops viewing; recordings and timers continue. Provide configurable login autostart and explicit Quit.
- Share tuner sessions by device and tuning parameters, maintaining consumer PID unions. Use bounded queues so playback/guide consumers cannot block recording.
- Use TimeProvider for time-dependent behavior. Dispose streams, sockets, native objects, subprocesses, and subscriptions deterministically.
- SQLite migrations are versioned and transactional with backups. Persist channels/preferences, guide data, schedule definitions, recording instances, settings, and recovery metadata.
- Provide rotating structured local logs and actionable dependency/reception/tuner/recording diagnostics.

## Required behavior

### Devices, guide, and playback

- Support SSDP on selected network interfaces and persisted manual HTTP(S) descriptor URLs. Deduplicate devices by UDN.
- Resolve advertised/AVM playlists and stable channel identities using device and DVB network/transport/service identifiers. Preserve favourites and ordering across refreshes.
- Implement RTSP keepalives/teardown/reconnection and UDP RTP/RTCP reception with bounded reordering, sequence-wrap handling, and loss diagnostics. Make the local RTP/RTCP port range configurable.
- Assemble DVB PSI/SI sections with bounds, CRC/version/discontinuity handling, correct DVB encodings, extended descriptions, and programme times.
- Collect EPG from existing sessions; use at most one additional idle tuner for frequency scans. Default refresh is six hours. Scans yield to viewing and recordings.
- Provide TV/radio playback, fullscreen, volume/mute, audio/subtitle selection, automatic deinterlacing, hardware decode with software fallback, and recorded-file seeking.
- Teletext uses LibVLC page entry, index, colour keys, and transparency.

### Timeshifting

- Buffer only the currently viewed channel on disk, defaulting to 60 minutes or 10 GiB, whichever is reached first; limits/folder are configurable.
- Index programme timestamps and decodable entry points. Seek through an application-controlled cursor and restart decoding with required programme tables.
- Support pause/resume, rewind, retained-range timeline, and Go Live. Continue capture while paused.
- If a paused point expires, remain paused, advance its resume point to the oldest retained content, and notify the user.
- Switching channels discards the old viewing buffer except data retained for an initiated recording. Enforce a free-space reserve, bounded retention, and startup cleanup.

### Recording and scheduling

- Capture recoverable MPEG-TS independently of playback. Finalize TV to .mkv and radio to .mka without audio/video re-encoding.
- Select tracks by programme identity. Preserve all programme audio, DVB subtitles, and available teletext subtitles converted to text. Offer source-TS retention for complete teletext/broadcast data.
- Validate temporary output before atomic publication/source deletion. Preserve recoverable source after failures or insufficient space.
- Instant recording can start now, at retained programme start, or at the earliest retained point.
- Support EPG/manual timers, daily/selected-weekday repetition, and configurable padding (two minutes before/five minutes after by default).
- EPG-linked timers follow identifiable event changes until capture begins; freeze planned end afterward unless manually extended.
- Store absolute instants in UTC and recurring definitions with their timezone. Advance nonexistent local DST times by the gap and select the first occurrence of ambiguous times.
- Priority: active recordings, due recordings, viewing, guide scanning. Never preempt an active recording for another timer. Break recording ties by scheduled start then creation order; display conflicts.
- On restart/resume, capture remaining intervals as partial and mark elapsed intervals missed. Persist scheduled/recording/finalizing/completed/partial/failed/missed/conflict states.
- Inhibit automatic sleep during capture. Future timers require an awake computer and logged-in user; no system service or wake timers initially.

## Strict engineering rules

- Enable nullable reference types, compiler warnings as errors, built-in .NET analyzers, Meziantou.Analyzer, and framework analyzers. Enforce formatting and naming through .editorconfig and build checks.
- No unexplained blanket warning suppressions. Apply narrow, justified exceptions to generated/platform-specific code only where necessary.
- Use bounded parsing/buffering, safe XML (no DTD/entity resolution), cancellation, explicit ownership, and structured subprocess argument lists. Do not build shell command strings from channel names/URLs or user text.
- Keep changes focused and reviewable. Do not claim an unfinished feature or verification as complete. Track gate results and material limitations in documentation.
- Use synthetic protocol/media fixtures in committed tests, with original test content. Live-device tests are opt-in; SSDP needs a reachable LAN multicast environment.
- Run appropriate tests/checks after changes. Add meaningful behavior tests rather than tests that duplicate implementation details.

## Verification commands

Once solution bootstrap exists, keep these commands working on Linux and Windows:

```text
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet test -c Release --no-build
dotnet format --verify-no-changes --no-restore
```

The bootstrap/development documentation must provide explicit commands for VM initialization, native UI tests, live SAT>IP tests, packaging, and recovery. Keep commands synchronized with actual tool/test-runner versions.

## Windows VM and desktop automation

- Maintain idempotent `scripts/dev/Initialize-WindowsVm.ps1` and `docs/development/windows-vm.md`.
- Snapshot the VM before installing tools. Use C:\src\fritz-tv-app for its checkout; record tested commit SHA and dependency versions.
- Install PowerShell 7, Git, .NET SDK, uv/Python, Windows-MCP, MKVToolNix, FFmpeg/ffprobe, Visual C++ runtime, and Inno Setup. Set Berlin timezone; report test timestamps in UTC.
- Run Windows-MCP as an authenticated per-user interactive login task on guest 127.0.0.1:8000. Tunnel host 127.0.0.1:18000 over SSH and register it in local Codex MCP configuration. Keep secrets outside Git.
- Reserve guest UDP 50000-50031 for VM RTP/RTCP tests. Configure corresponding VirtualBox NAT forwards and scoped guest firewall rules. Require valid MPEG-TS reception plus TV/radio decoding before passing the live-stream gate.
- Use an interactive GitHub runner labelled fritz-tv-win11, not a Windows service. Serialize desktop jobs and exploratory automation; require an unlocked console session. Do not enable automatic Windows login without a separate explicit request.
- Public fork PRs use hosted runners only. VM execution is restricted to trusted protected-main commits or explicitly dispatched trusted workflows.
- Upload test reports, screenshots, logs, installed versions, and commit SHA as CI artifacts. Use stable UI automation identifiers.
- VM checks do not establish representative physical-GPU decoding or physical-machine power behavior.

## GitHub workflow and release policy

- Bootstrap the empty repository, then use small codex/ branches and PRs.
- Use GitHub Actions for Linux/Windows build, analysis, tests, workflow/script linting, packaging, dependency checks, and releases.
- Configure CodeRabbit automatic PR review with repository instructions. Run CodeRabbit CLI reviews when performing requested code review/fix cycles and follow the installed CodeRabbit skill. Resolve actionable review threads; require passing CI.
- Configure Renovate update PRs. Protect main after required checks exist.
- Semver tags create draft GitHub Releases containing Windows installers, Linux .deb packages, checksums, and dependency/license notices.

## Implementation gates

1. Contributor instructions, VM bootstrap/documentation, repository foundations.
2. Windows tools, authenticated MCP, interactive runner, and UDP reception/decoding.
3. Avalonia/LibVLC embedding, teletext, startup keyframe handling, growing-buffer seeking on both platforms.
4. Device/channel management and shared reception engine.
5. Player, broadcast guide, timeshifting, localization.
6. Recording/scheduler/conflicts/recovery/power integration.
7. Installers, upgrades, autostart, and real-device acceptance.
