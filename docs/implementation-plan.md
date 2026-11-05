# FRITZ TV implementation and Windows testing plan

The first implementation action was to create root `AGENTS.md`. It is the contributor contract for the stack, architecture, quality rules, verification, dependency maintenance, and GitHub workflow below.

## Execution order and gates

1. Contributor instructions, reproducible VM bootstrap/documentation, repository foundations.
2. Windows tools, authenticated interactive MCP, trusted interactive runner, valid UDP MPEG-TS reception and TV/radio decoding.
3. Avalonia/LibVLC embedding, teletext, startup keyframe handling, and growing-buffer seeking on Linux and Windows.
4. Device/channel management and shared reception engine.
5. Player, EPG, timeshifting, and localization.
6. Recording, timers, conflicts, recovery, and power integration.
7. Installers, upgrades, autostart, and real-device acceptance.

Gates are accepted only with recorded evidence. A prototype or a unit test does not establish native-media acceptance. Windows bootstrap is complete. Application implementation and native acceptance follow the gates above; external hardware/LAN checks remain distinct from VM and synthetic evidence.

## Windows development environment

- Windows 11 Pro x64 VM, Guest Additions, 3D enabled, native Windows tooling.
- SSH: `ssh -p 2222 vboxuser@127.0.0.1`; key authentication, existing active console session.
- Checkout: `C:\src\fritz-tv-app`; identify tests and artifacts with a Git SHA.
- Idempotent bootstrap: `scripts/dev/Initialize-WindowsVm.ps1`, pinned `windows-toolchain.json`, and `docs/development/windows-vm.md`.
- Snapshot before installs. Install stable PowerShell 7, Git, .NET SDK, uv-managed Python, Windows-MCP, MKVToolNix, FFmpeg/ffprobe, Visual C++ runtime, and Inno Setup. Verify hashes/signatures where supplied and record versions.
- Configure PowerShell 7 as the SSH shell and Berlin timezone. Report timestamps in UTC.
- Run authenticated Windows-MCP as a per-user interactive login task bound to guest loopback8000. Tunnel host loopback18000 over SSH and register it in local Codex MCP configuration. Secrets never enter Git.
- Retain manual Windows login after reboot. Desktop automation needs an unlocked console; command/build access is independent.
- Reserve guest UDP50000-50031 for RTP/RTCP pairs, with matching VirtualBox NAT forwards from the host's FRITZ!Box-facing VPN source and scoped guest firewall rules. The application port range is configurable.
- Successful descriptor access and RTSP SETUP/PLAY are insufficient: require valid RTP-carried MPEG-TS and TV/radio decode evidence.
- GitHub runner label: `fritz-tv-win11`; run as an interactive login task, not a service. Serialize native UI tests and exploratory desktop control. Never execute public fork PR code on this VM.

## Product, stack, and architecture

GPL-3.0-or-later; Windows 11 x64 and Ubuntu24.04+ x64/TUXEDO OS; X11/XWayland initially. English and German, system language/theme.

Use C#14/.NET10 initially, Avalonia12 Fluent, CommunityToolkit.Mvvm, stable LibVLCSharp/LibVLC3, Microsoft.Data.Sqlite, .NET Generic Host, and MKVToolNix finalization. Tests use current stable xUnit, Avalonia headless, and Windows FlaUI UIA3. Revalidate official stable versions, pin them, and adopt subsequent stable major releases through validated PRs.

One application process has a tray icon and background services. Closing its window stops viewing and keeps recordings/timers alive; explicit Quit stops the process. Login autostart is configurable. Future timers need an awake machine and logged-in user. Inhibit automatic sleep during capture; no system service or wake timers initially.

Separate Core, Infrastructure, Desktop, and tests. Background services own discovery, channels, tuners, transport, DVB EPG, timeshifting, timers, and recordings. Use asynchronous cancellation-aware interfaces, immutable status snapshots, explicit native ownership, and TimeProvider. View models do not own database/network/native-player logic.

SQLite persists devices/channels/customizations, EPG, timer definitions/instances, recordings/recovery, and settings. Migrations are transactional/versioned with backup. Provide rotating local logs and actionable tuner/signal/packet-loss/dependency/recording diagnostics.

## Devices and media transport

- SSDP on selected interfaces; persisted manual HTTP(S) descriptors; deduplicate UDNs.
- Discover generic SAT>IP devices, guarantee FRITZ!Box DVB-C playback first. No RF channel scanning or XMLTV provider integration initially.
- Import advertised and AVM TV/radio playlists; preserve favourites/order using stable device+DVB network/transport/service identities. Search/filter channels and devices.
- Share sessions by device+tuning parameters and maintain the union of consumer PIDs. Watching and recording the same channel reuse reception.
- UDP RTP/RTCP: bounded reordering, wraparound/loss/discontinuity handling, keepalives, teardown, reconnection, and bounded consumer queues. Playback/EPG must not block recording. Account for external tuner clients.

Live investigation: FRITZ!Box6660 advertises four DVB-C tuners; lists33 HD,58 SD,109 radio entries. UDP works on Linux; TCP-interleaved RTSP is rejected461. Dynamic addpids/delpids worked. Broadcast EPG and teletext exist; a short capture contained EPG for77 services and events six days ahead. These are observations, not guarantees of ongoing availability or guide depth.

## Playback, guide, and timeshifting

Provide TV/radio, fullscreen, volume/mute, audio/subtitle selection, automatic deinterlacing, hardware decode with software fallback, completed-recording playback, and seeking.

Teletext uses LibVLC page numbers, index, colour keys, and transparency.

Parse required DVB tables with bounded assembly, CRC/version handling, discontinuities, DVB encodings, extended descriptions, and programme times. Collect EPG from active streams and scan distinct frequencies with at most one extra idle tuner. Refresh every six hours by default. Scans yield to recordings and viewing. UI exposes now/next, details, timelines, search, timers, coverage, and freshness.

Timeshift the current TV/radio channel in indexed disk segments. Default retention is60minutes or10GiB, whichever is reached first; limits/folder configurable. Index programme timestamps and decodable points; application-controlled seeking restarts decoding with programme tables. Provide pause/resume, rewind, retained range, and Go Live. Capture continues while paused. If a paused point expires, remain paused, advance to the oldest retained point, and notify. Channel switching discards old viewing content except segments retained for a recording. Enforce a free-space reserve, bounded retention, and startup cleanup.

## Recording and scheduling

Capture recoverable MPEG-TS independently of playback. Finalize TV to.mkv and radio to.mka without audio/video re-encoding, selecting tracks by DVB programme identity. Preserve all programme audio, DVB subtitles, and teletext subtitles converted to text; optionally keep source TS for complete teletext and broadcast data. Validate temporary output before atomic publication and source deletion. Keep recoverable source after failure or insufficient finalization space.

Instant recording offers now, retained programme start, or earliest retained content. Support EPG/manual timers, daily and selected-weekday recurrence, and global/per-timer padding (2minutes before/5after defaults).

EPG-linked timers follow identifiable event changes until capture starts; freeze planned end afterward unless manually extended. Absolute times are UTC; recurrence stores timezone. Advance nonexistent local DST times by the gap and choose the first occurrence of ambiguous times.

Priority: active recordings, due recordings, viewing, scanning. Never preempt an active recording for another timer. Break recording ties by scheduled start then creation order; display conflicts. On restart/resume, capture remaining intervals as partial and mark elapsed intervals missed. Persist scheduled, recording, finalizing, completed, partial, failed, missed, and conflict states.

## Quality and GitHub delivery

Nullable, compiler warnings-as-errors, .NET analyzers, Meziantou/framework analyzers, enforced formatting, justified narrow suppressions. Central dependency versions, locked restores, SDK/tool pins, safe XML, bounded parsing/buffering, deterministic disposal, and structured subprocess arguments.

GitHub Actions runs Windows/Linux build, analysis, unit/headless tests, script/workflow lint, packaging, dependency checks, and releases. Renovate opens update PRs. CodeRabbit adds automatic PR reviews. Require green CI and resolved actionable review threads. Bootstrap main, then use small codex/ branches and PRs. Semver tags create draft GitHub Releases with Windows installers, Linux.deb, checksums, and license notices.

Hosted Windows runners perform clean builds/unit/headless/installer work. SSH supports diagnosis. Windows-MCP supports exploratory desktop/media inspection. Interactive VM runner+FlaUI provides native regressions; only trusted protected-main or explicitly dispatched trusted workflows may execute there. Upload reports, screenshots, logs, versions, and SHA. Use stable UI automation IDs.

## Acceptance coverage

Malformed descriptors/playlists; SSDP deduplication; RTP loss/reordering/wrap; TS discontinuity and DVB text; shared frequencies and tuner contention; scan preemption; buffer expiry/seek/GoLive/storage/channel changes; recording buffered content; Matroska tracks/duration/seek/startup decode; capture/finalization failure and recovery; recurrence/DST/EPG changes/restart; window closure/tray/autostart/sleep inhibition; installers/upgrades.

Hosted CI uses synthetic fixtures with original media. Live suites opt in to the supplied descriptor. SSDP requires LAN multicast. VM results do not prove representative physical-GPU decode or physical-machine power behavior.

## Current execution evidence

Verified on 2026-11-05; machine reports use UTC. Implementation is in [PR #1](https://github.com/jan-guenter/fritz-tv-app/pull/1). Root `AGENTS.md` was the first implementation action (commit `429f54c`). The integrated application checkpoint is `3209b173fb3d247591ec9e24638f9a4d163e3e1a`; activation/native-test corrections follow in `dea0b1b`.

- The Windows VM has all pinned tools installed. Native PowerShell 5.1 verification reports `AllRequiredToolsInstalled=True`; both interactive MCP and GitHub runner processes run in console session 1 and restart after login.
- Authenticated MCP discovery, screenshots, accessibility inspection, application launch, mouse/keyboard input, save, and independent file readback passed. Its endpoint and credentials are registered privately outside Git.
- The runner has a private admission guard outside the checkout. All 13 allowed/rejected policy cases pass. It admits only this repository's dispatched main desktop workflow; hosted runners handle PR code.
- The initial sandboxed VirtualBox call created a disconnected management context. Its reported snapshot was rejected as evidence. Recovery used normal guest shutdown, a reflink backup of the actual runtime disk with verified header, and a genuine cold snapshot `fritz-tv-toolchain-ready-20261105` (`80a59afa-472b-4b1e-9433-c453588d349c`). The host helper now refuses stale IPC contexts. All 32 reserved UDP forwards are present.
- Exact Windows checkpoint `556d878` built with zero warnings/errors, passed 130 unit/headless tests (three native opt-ins skipped), and published a self-contained app. All three Windows native synthetic TV/radio remux/decode and scoped power-request tests also passed separately. All five required hosted CI checks, including both installers, passed at this revision.
- Windows live TV/radio UDP and FFmpeg decoding passed. The audited standard-SAT>IP ZDF probe received 11,450 RTP packets / 15,068,200 TS bytes in ten seconds with zero detected loss. The short standalone probe reports no RTCP, but the actual Windows application subsequently displayed parsed RTCP signal level 135/255 and quality 14/15 through NAT. Linux actual SES1 parsing also passes; the probe/application discrepancy remains a scoped diagnostic item.
- Real Linux four-tuner reception passed on four distinct frequencies with zero detected loss; a fifth frequency was rejected without interrupting recordings, shared-frequency consumers reused a tuner, and teardown released all four. FRITZ playlist `avm=1` was found to force frontend 1 and is removed from standard shared sessions.
- Real Linux LibVLC live/retained-buffer playback, pause expiry, rewind, Go Live, and dynamic PMT-driven PID updates decoded and displayed video/audio with zero native errors or demux corruption/discontinuities. Hidden preroll precedes the visible cursor after expiry/seek.
- Clean committed Linux GUI checkpoint `ebccec4` passed actual whole-display TV, opaque/transparent teletext page 100, Close, pause, paused rewind, resume, Go Live, expanded guide with continuing video, and Quit (exit 0). Windows direct MCP inspection also showed live TV, opaque/transparent teletext and responsive pause/Go Live. Native FlaUI media acceptance is tracked separately while its asynchronous-control synchronization is corrected.
- Native synthetic TV/radio remux/decode and scoped sleep-inhibitor tests pass. A private 30.76-second ZDF capture finalized without AV re-encoding, preserving H.264, three AC3 tracks, DVB subtitles and eight teletext-derived SRT cues. Raw mid-GOP broadcast captures can emit decoder reference warnings; no warning-free raw-start claim is made.
- Linux packages at `3209b17` passed actual dpkg install 0.1.0, upgrade 0.1.1, removal and purge inside a private user namespace/root. Desktop entries and 66 package license records passed validation. Host desktop cache triggers were deliberately excluded; no host installation/profile was changed.
- Main is protected, including administrators: strict up-to-date Windows/Linux build and installer checks, workflow lint, PR-based changes, resolved review conversations, linear history, and no force pushes/deletion. CodeRabbit is confirmed installed and currently skips the draft PR; Renovate configuration is present, but browser access to installation settings was declined, so installation is unverified.
- Source includes transactional SQLite schema-3 migrations/backups, persisted ordering and DVB identities, bounded guide/timeshift storage, dynamic PID unions/reconnection, timer/conflict/recovery policies, rotating logs, single-instance activation, EN/DE UI, and reproducible Windows/Linux installers. See [verification commands](development/verification.md) and [runtime behavior](development/runtime-services.md).

Native UI acceptance, final hosted CI, package results, and CodeRabbit review must be verified against their recorded final revisions before claiming all gates passed. SSDP needs reachable LAN multicast. Representative physical Windows GPU decoding and actual physical-machine sleep behavior remain separate external checks. A configured Renovate file does not by itself prove the GitHub App is installed.
