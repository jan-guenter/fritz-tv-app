# Runtime diagnostics and instance ownership

The desktop host writes structured JSON lines to the current user's local application data directory, `FritzTV/logs/fritz-tv.log`. Set `FRITZTV_LOG_DIRECTORY` before launch to choose a different local directory. The default limits are five files of at most 4 MiB each, including the active file. UTC timestamps, log levels, categories, event IDs, messages, and exception details are recorded. A bounded 1,024-entry queue keeps slow diagnostic storage from blocking reception or recording; overflow drops diagnostic messages and increments `DroppedMessages`.

`RotatingFileLoggerProvider.FlushAsync` drains earlier entries and flushes them to disk. It reports write failures; `LastError` retains the most recent storage failure. Disposing the provider drains accepted entries and closes its file. Expected disk errors remain diagnostic failures rather than preventing application shutdown. New Linux log directories/files are restricted to the current user; Windows files inherit the user profile's permissions.

Callers must never log credentials. The provider also redacts common bearer tokens, credential query parameters, password/API-key assignments, and URL user information before storing text. This defense does not make arbitrary sensitive programme or user text safe to publish. Review logs before attaching them to a public GitHub issue.

`SingleInstanceActivation` uses an application/user-derived name and a named pipe restricted to the current user. On Windows its mutex uses the global kernel namespace so one user cannot accidentally start separate recording engines from SSH and the console session; the per-user name keeps other users independent. See [Microsoft kernel object namespaces](https://learn.microsoft.com/windows/win32/termserv/kernel-object-namespaces). One dedicated thread acquires and releases the instance mutex on that same thread. It holds no UI resources. A second launch sends a one-byte activation request and requires an acknowledgement within 30 seconds; it exits only after that acknowledgement, or reports an error if the existing instance does not respond. Incomplete secondary requests expire after five seconds. An abandoned mutex permits startup after a crashed instance.

The application attaches the activation event before calling `StartListening`. That event runs on the pipe listener, so handlers must post window changes to Avalonia's UI dispatcher and return promptly. Ordinary Quit stops the host before releasing instance ownership. OS shutdown starts capture cancellation immediately and gives `PrepareShutdownAsync` a short caller-supplied deadline to flush transport and persist partial state without starting remux. If the OS terminates the process before cleanup completes, persisted recording/source metadata remains available for recovery on the next launch; shutdown preparation cannot guarantee time that the OS does not grant.

Interrupted captures persist `Partial` with a `NeedsRecovery` marker. Startup resumes remaining finite intervals as partial captures and finalizes elapsed marked intervals. Unbounded manual instant recordings finalize their retained source on restart; they never automatically reacquire a tuner, including requests interrupted before initial acquisition. Explicit Stop clears recovery eligibility even when the result is partial and the original finite end is still in the future.

The [Windows native dependency audit](windows-native-dependencies.md) records the concrete runtime import checks used when deciding installer prerequisites.

Run the service tests with:

```text
dotnet run --project tests/FritzTV.Infrastructure.Tests -c Release -- --filter-class FritzTV.Infrastructure.Tests.DiagnosticsTests
dotnet run --project tests/FritzTV.Desktop.Tests -c Release -- --filter-class FritzTV.Desktop.Tests.SingleInstanceActivationTests
```

Native synthetic remux/decode tests require FFmpeg and MKVToolNix on `PATH`. Set `FRITZTV_NATIVE_MEDIA_TESTS=1`; set `FRITZTV_NATIVE_POWER_TESTS=1` in a supported interactive desktop to verify scoped sleep-inhibitor acquisition and release. These opt-in tests generate their own short media and remove it afterward. They do not validate a physical GPU, sleep behavior on physical hardware, or broadcast teletext coverage.
