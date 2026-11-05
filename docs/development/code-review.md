# CodeRabbit implementation review

The first complete implementation review finished on **2026-11-05 at 05:39:19 UTC** with CodeRabbit CLI **0.9.1**. It compared `codex/windows-bootstrap-and-player` at `3209b17`, including its working-tree changes, against `main`. Both `AGENTS.md` and `.coderabbit.yaml` supplied review instructions. Binary application icons were excluded by the reviewer.

CodeRabbit reported **18 issues**. Four follow-up reports repeated existing locations, leaving **14 distinct issues: four major, nine minor, and one trivial**. It reported no critical issues. The table consolidates duplicates; it does not treat duplicate reports as additional defects.

The review command was:

```bash
coderabbit review --agent --fresh --deep --base main --include-untracked \
  -c AGENTS.md .coderabbit.yaml
```

Initial authentication failed despite a locally authenticated status. The official browser sign-in completed, authentication was verified again, and the subsequent review completed successfully. Review credentials and raw review output remain outside the repository.

## Issues and validation

“Implemented” means the correction is present and its stated focused validation passed. “Pending” means implementation or validation has not yet been reported complete. The final incremental CodeRabbit review remains pending until all fixes are ready.

| Severity | File | Impact and correction | Focused validation / status |
| --- | --- | --- | --- |
| ⚠️ Major | [RecordingSchedulerService.cs](../../src/FritzTV.Infrastructure/Scheduling/RecordingSchedulerService.cs) | An exception omitted by the scheduler's filtered catch could stop the host or all future timer ticks. Keep the outer loop alive for non-shutdown failures, with structured error reporting. | Pending recording/scheduler fix and failure-isolation regression. |
| ⚠️ Major | [SatIpRecordingInputFactory.cs](../../src/FritzTV.Infrastructure/Recording/SatIpRecordingInputFactory.cs) | After a shared session failed, buffered recording could retain only a dead recording-priority lease while replacement viewing was preemptible. Surface loss of the protective lease through the recording reconnect path. | Pending buffered-priority failure/recovery regression. |
| ⚠️ Major | [RtspConnection.cs](../../src/FritzTV.Infrastructure/Transport/RtspConnection.cs) | A canceled or malformed response left the TCP connection reusable, allowing a late reply to acknowledge a later request. Fault and close the connection after write/read failure; fail the owning session and validate response sequence numbers. Preserve the experimentally verified FRITZ!Box `CSeq: 0` compatibility exception. | Implemented. Late-response cancellation, matching nonzero sequences, mismatched sequences, and existing zero-sequence cases pass. |
| ⚠️ Major | [RecordingCoordinator.cs](../../src/FritzTV.Infrastructure/Recording/RecordingCoordinator.cs) | Retry counts accumulated across otherwise healthy recording intervals, eventually stopping a long recording. Count consecutive failures and reset the budget after successful input delivery. | Pending repeated-outage/successful-data regression. |
| ℹ️ Minor | [NativeDesktopSession.cs](../../tests/FritzTV.Windows.UiTests/NativeDesktopSession.cs) | Constructor failure could leave the launched process, UI automation objects, and isolated profile alive, poisoning later desktop tests. Clean up partially constructed sessions. | Pending Windows test-harness cleanup validation. |
| ℹ️ Minor | [build-deb.sh](../../packaging/build-deb.sh) | A semver prerelease such as `1.0.0-rc.1` sorts after stable `1.0.0` in Debian version ordering. Use a Debian `~` prerelease separator in package metadata. | Pending version-order and shell checks. |
| ℹ️ Minor | [windows-ui.yml](../../.github/workflows/windows-ui.yml) | Verification could succeed with missing tools, and its report could name the manual checkout rather than the workflow checkout. Pass the actual workspace and enforce the report's toolchain result. | Pending workflow/verification checks. |
| ℹ️ Minor | [release.yml](../../.github/workflows/release.yml) | A manually dispatched draft could create a tag on a different commit from its packaged artifacts. Set `target_commitish` to `github.sha`. | Implemented; diff/whitespace check passed. Full workflow lint remains part of integrated checks. |
| ℹ️ Minor | [RecordingCoordinator.cs](../../src/FritzTV.Infrastructure/Recording/RecordingCoordinator.cs) | Unavailable sleep inhibition canceled an otherwise receivable recording. Continue capture with a visible, separately persisted warning so healthy data is not mislabeled partial. | `RecordingInstance.Warning` added; coordinator fallback and regression pending. |
| ℹ️ Minor | [Strings.de.resx](../../src/FritzTV.Desktop/Resources/Strings.de.resx) | The German expander header omitted its recordings tab. Include “Aufnahmen” in the label. | Implemented; resource XML and the exact revised label verified. |
| ℹ️ Minor | [Program.cs](../../src/FritzTV.Desktop/Program.cs) | Failure to activate an existing instance could escape the second process's entry point. Handle the failed activation and return an actionable nonzero exit. | Pending activation-failure regression/strict build. |
| ℹ️ Minor | [EitParser.cs](../../src/FritzTV.Infrastructure/Dvb/EitParser.cs) | Decoding extended-event fragments separately corrupted characters split across descriptor boundaries. Join ordered raw text bytes, remove repeated matching encoding selectors, and decode once. | Implemented. ISO6937 diacritic and UTF-8 multibyte split regressions pass alongside existing extended-description tests. |
| ℹ️ Minor | [SsdpDiscovery.cs](../../src/FritzTV.Infrastructure/Devices/SsdpDiscovery.cs) | Binding a local address did not explicitly select the outbound multicast interface. Set `IP_MULTICAST_IF` for the selected interface. | Implemented; strict compilation passed. Actual SSDP discovery still requires the separate reachable-LAN validation described in the implementation plan. |
| ℹ️ Trivial | [GuideService.cs](../../src/FritzTV.Infrastructure/Guide/GuideService.cs) | The scan deadline used system time while other guide timing used the injected clock. Create its timeout through the injected `TimeProvider`. | Implemented; strict compilation and existing guide regressions are the focused checks. |

The owned RTSP and DVB regression run passed **15/15 tests**. Networking body-deadline, caller-cancellation, malformed-control, and guide error-boundary checks also passed **17/17** before these review fixes. Native desktop and media acceptance results are tracked separately in [verification.md](verification.md); source-level review does not substitute for those checks.

## Incremental review

After all corrections and integrated checks are complete, rerun the same comparison without `--fresh` so CodeRabbit can use its completed local review checkpoint while including committed, staged, unstaged, and untracked follow-up changes:

```bash
coderabbit review --agent --deep --base main --include-untracked \
  -c AGENTS.md .coderabbit.yaml
```

Status: **pending**. Record the actual result here after the command completes; do not infer a clean review from successful compilation or unit tests.
