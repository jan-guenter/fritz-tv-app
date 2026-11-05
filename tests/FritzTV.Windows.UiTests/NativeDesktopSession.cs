using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using FlaUI.UIA3;

namespace FritzTV.Windows.UiTests;

[SupportedOSPlatform("windows")]
internal sealed class NativeDesktopSession : IAsyncDisposable
{
    private readonly UIA3Automation automation = new();

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "fritztv-ui-" + Guid.NewGuid().ToString("N"));

    public Application Application { get; }

    public Window Window { get; }

    public NativeDesktopSession()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("FRITZTV_UI_APP")!) { UseShellExecute = false };
        start.Environment["FRITZTV_DATA_DIRECTORY"] = DataDirectory;
        start.Environment["FRITZTV_LOG_DIRECTORY"] = Path.Combine(DataDirectory, "logs");
        Application? launched = null;
        try
        {
            launched = Application.Launch(start);
            Application = launched;
            Window = Application.GetMainWindow(automation, TimeSpan.FromSeconds(30)) ?? throw new InvalidOperationException("The isolated desktop did not start. Quit any exploratory FRITZ TV instance before UI tests.");
        }
        catch
        {
            Cleanup(launched);
            throw;
        }
    }

    public AutomationElement Find(string id) => TryFind(id)
        ?? throw new InvalidOperationException("UI automation element not found: " + id);

    public AutomationElement? TryFind(string id)
    {
        // UIA FindFirst stops at Avalonia's embedded native-video HWND. The control
        // walker still exposes its siblings, including the same stable identifiers.
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var pending = new Queue<AutomationElement>();
        pending.Enqueue(Window);
        int examined = 0;
        while (pending.Count > 0 && examined++ < 2048)
        {
            AutomationElement current = pending.Dequeue();
            if (current.Properties.AutomationId.ValueOrDefault is { } actual && string.Equals(actual, id, StringComparison.Ordinal))
            {
                return current;
            }

            AutomationElement? child = walker.GetFirstChild(current);
            while (child is not null && pending.Count + examined < 2048)
            {
                pending.Enqueue(child);
                child = walker.GetNextSibling(child);
            }
        }

        return null;
    }

    public static async Task WaitAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The expected desktop state did not appear before the deadline.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!Application.HasExited)
            {
                if (!Window.IsOffscreen)
                {
                    Find("Quit").AsButton().Invoke();
                    await WaitAsync(() => Application.HasExited, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (IsExpectedCleanupFailure(exception))
        {
            Trace.TraceWarning("Graceful native test shutdown failed; terminating its owned process: " + exception.Message);
        }
        finally
        {
            Cleanup(Application);
        }
    }

    private void Cleanup(Application? launched)
    {
        _ = TryCleanup(() =>
        {
            if (launched is not null && !launched.HasExited)
            {
                launched.Kill();
            }
        });
        _ = TryCleanup(() => launched?.Dispose());
        _ = TryCleanup(automation.Dispose);
        if (TryCleanup(PreserveLogs))
        {
            _ = TryCleanup(() =>
            {
                if (Directory.Exists(DataDirectory))
                {
                    Directory.Delete(DataDirectory, recursive: true);
                }
            });
        }
    }

    private static bool TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
            return true;
        }
        catch (Exception exception) when (IsExpectedCleanupFailure(exception))
        {
            Trace.TraceWarning("Native test cleanup retained its original failure: " + exception.Message);
            return false;
        }
    }

    private static bool IsExpectedCleanupFailure(Exception exception) => exception is COMException or FlaUIException
        or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException;

    private void PreserveLogs()
    {
        string? destination = Environment.GetEnvironmentVariable("FRITZTV_UI_ARTIFACTS");
        string logs = Path.Combine(DataDirectory, "logs");
        if (destination is not null && Directory.Exists(logs))
        {
            destination = Path.Combine(destination, Path.GetFileName(DataDirectory));
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(logs))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }
        }
    }
}
