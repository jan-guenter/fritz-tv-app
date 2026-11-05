using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FritzTV.Infrastructure.Power;

public sealed class SystemPowerInhibitor : IPowerInhibitor
{
    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            return WindowsInhibition.Create();
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Recording sleep inhibition is supported on Windows and Linux.");
        }

        var start = new ProcessStartInfo("systemd-inhibit")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "--what=sleep", "--mode=block", "--who=FRITZ TV", "--why=Recording in progress", "/bin/sh", "-c", "printf 'ready\n'; exec cat >/dev/null" })
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new IOException("Could not start systemd sleep inhibition.");
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(ready, "ready", StringComparison.Ordinal) || process.HasExited)
            {
                throw new IOException("The desktop session did not grant sleep inhibition.");
            }

            return new LinuxInhibition(process);
        }
        catch (Exception exception)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            process.Dispose();
            if (exception is TimeoutException)
            {
                throw new IOException("Timed out waiting for the desktop sleep inhibitor.", exception);
            }

            throw;
        }
    }

    private sealed class LinuxInhibition(Process process) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            try
            {
                process.StandardInput.Close();
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsInhibition(SafeFileHandle handle) : IAsyncDisposable
    {
        private int disposed;

        public static WindowsInhibition Create()
        {
            var reason = Marshal.StringToHGlobalUni("FRITZ TV is recording a broadcast.");
            try
            {
                var context = new ReasonContext { Flags = 1, Reason = new ReasonUnion { SimpleReasonString = reason } };
                var request = PowerCreateRequest(ref context);
                if (request.IsInvalid)
                {
                    request.Dispose();
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows did not create a recording power request.");
                }

                if (!PowerSetRequest(request, 1))
                {
                    var error = Marshal.GetLastWin32Error();
                    request.Dispose();
                    throw new Win32Exception(error, "Windows did not grant recording sleep inhibition.");
                }

                return new WindowsInhibition(request);
            }
            finally
            {
                Marshal.FreeHGlobal(reason);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                PowerClearRequest(handle, 1);
                handle.Dispose();
            }

            return ValueTask.CompletedTask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PowerSetRequest(SafeFileHandle request, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PowerClearRequest(SafeFileHandle request, int requestType);

        [StructLayout(LayoutKind.Sequential)]
        private struct ReasonContext
        {
            public uint Version;
            public uint Flags;
            public ReasonUnion Reason;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct ReasonUnion
        {
            [FieldOffset(0)]
            public nint SimpleReasonString;
        }
    }
}
