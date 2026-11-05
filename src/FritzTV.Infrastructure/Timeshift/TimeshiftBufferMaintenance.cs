using System.Diagnostics;
using System.Text.Json;

namespace FritzTV.Infrastructure.Timeshift;

public static class TimeshiftBufferMaintenance
{
    private const string MarkerName = ".fritztv-buffer";

    public static int CleanAbandoned(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var candidate in Directory.EnumerateDirectories(directory))
        {
            try
            {
                if (TryDelete(candidate))
                {
                    removed++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Active, inaccessible, or unrecognized data must remain untouched.
            }
        }

        return removed;
    }

    internal static FileStream CreateOwnership(string directory)
    {
        using var process = Process.GetCurrentProcess();
        var owner = new BufferOwner(1, process.Id, process.StartTime.ToUniversalTime().Ticks);
        var stream = new FileStream(Path.Combine(directory, MarkerName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            JsonSerializer.Serialize(stream, owner);
            stream.Flush();
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool TryDelete(string candidate)
    {
        if (!Guid.TryParseExact(Path.GetFileName(candidate), "N", out _) || File.GetAttributes(candidate).HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        var marker = Path.Combine(candidate, MarkerName);
        if (!File.Exists(marker) || File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        using (var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (stream.Length > 1024 || JsonSerializer.Deserialize<BufferOwner>(stream) is not { Version: 1 } owner || OwnerIsAlive(owner))
            {
                return false;
            }
        }

        if (Directory.EnumerateDirectories(candidate).Any() || Directory.EnumerateFiles(candidate).Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || !IsBufferFile(Path.GetFileName(path))))
        {
            return false;
        }

        Directory.Delete(candidate, recursive: true);
        return true;
    }

    private static bool IsBufferFile(string name) => string.Equals(name, MarkerName, StringComparison.Ordinal)
        || (name.Length == 15 && name.EndsWith(".ts", StringComparison.Ordinal) && name.AsSpan(0, 12).IndexOfAnyExceptInRange('0', '9') < 0);

    private static bool OwnerIsAlive(BufferOwner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == owner.ProcessStartTicks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private sealed record BufferOwner(int Version, int ProcessId, long ProcessStartTicks);
}
