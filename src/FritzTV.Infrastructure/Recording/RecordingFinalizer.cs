using System.Globalization;
using System.Text.Json;
using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Recording;

public sealed class RecordingFinalizer(string? executable = null, IMediaProcessRunner? processRunner = null, long freeSpaceReserveBytes = 1024L * 1024 * 1024) : IRecordingFinalizer
{
    private readonly string tool = executable ?? ResolveExecutable();
    private readonly IMediaProcessRunner runner = processRunner ?? new MediaProcessRunner();

    public static string ResolveExecutable()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "mkvtoolnix", OperatingSystem.IsWindows() ? "mkvmerge.exe" : "mkvmerge");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        if (OperatingSystem.IsWindows())
        {
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKVToolNix", "mkvmerge.exe");
            if (File.Exists(installed))
            {
                return installed;
            }
        }

        return "mkvmerge";
    }

    public async Task<RecordingFinalizationResult> FinalizeAsync(string sourcePath, string outputPath, TvChannel channel, bool retainSource, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(channel);
        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourcePath, outputPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("Source transport and final recording must use different paths.", nameof(outputPath));
        }

        if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0)
        {
            throw new InvalidDataException("The transport capture is missing or empty.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        var token = timeout.Token;
        var source = await IdentifyAsync(sourcePath, token).ConfigureAwait(false);
        var selected = SelectProgrammeTracks(source, channel.ServiceId, channel.Kind);
        if (File.Exists(outputPath))
        {
            var warning = await ValidateOutputAsync(outputPath, channel.Kind, selected, token).ConfigureAwait(false);
            var keepSource = retainSource || warning is not null;
            if (!keepSource)
            {
                File.Delete(sourcePath);
            }

            return new RecordingFinalizationResult(outputPath, keepSource, warning);
        }

        return await RemuxAsync(sourcePath, outputPath, channel, selected, retainSource, token).ConfigureAwait(false);
    }

    private async Task<RecordingFinalizationResult> RemuxAsync(string sourcePath, string outputPath, TvChannel channel, IReadOnlyList<MatroskaTrack> selected, bool retainSource, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var available = new DriveInfo(Path.GetPathRoot(outputPath)!).AvailableFreeSpace;
        if (available - new FileInfo(sourcePath).Length < freeSpaceReserveBytes)
        {
            throw new IOException("There is not enough free space to finalize the recording; the transport capture has been retained.");
        }

        var temporary = outputPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".partial";
        try
        {
            var arguments = BuildRemuxArguments(sourcePath, temporary, channel.Name, selected);
            var remux = await runner.RunAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
            if (remux.ExitCode is not (0 or 1))
            {
                throw new IOException("Matroska finalization failed: " + Diagnostic(remux));
            }

            var validationWarning = await ValidateOutputAsync(temporary, channel.Kind, selected, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, outputPath, overwrite: false);
            var toolWarning = remux.ExitCode == 1 ? "MKVToolNix completed with warnings: " + Diagnostic(remux) : null;
            var warning = toolWarning is null ? validationWarning : toolWarning + (validationWarning is null ? string.Empty : " " + validationWarning);
            var keepSource = retainSource || warning is not null;
            if (!keepSource)
            {
                File.Delete(sourcePath);
            }

            return new RecordingFinalizationResult(outputPath, keepSource, warning);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static List<string> BuildRemuxArguments(string sourcePath, string temporary, string name, IReadOnlyList<MatroskaTrack> selected)
    {
        var arguments = new List<string> { "--output", temporary, "--title", name };
        foreach (var (type, include, exclude) in new[] { ("video", "--video-tracks", "--no-video"), ("audio", "--audio-tracks", "--no-audio"), ("subtitles", "--subtitle-tracks", "--no-subtitles") })
        {
            var ids = selected.Where(track => string.Equals(track.Type, type, StringComparison.Ordinal)).Select(track => track.Id.ToString(CultureInfo.InvariantCulture)).ToArray();
            if (ids.Length == 0)
            {
                arguments.Add(exclude);
            }
            else
            {
                arguments.Add(include);
                arguments.Add(string.Join(',', ids));
            }
        }

        arguments.Add(sourcePath);
        return arguments;
    }

    public async Task ValidateAsync(string path, ChannelKind kind, CancellationToken cancellationToken = default)
    {
        _ = await ValidateOutputAsync(path, kind, null, cancellationToken).ConfigureAwait(false);
    }

    public static IReadOnlyList<MatroskaTrack> SelectProgrammeTracks(string identification, int? serviceId, ChannelKind kind)
    {
        var tracks = ParseTracks(identification);
        var programmes = tracks.Where(track => track.Type is "audio" or "video").Select(track => track.ProgrammeNumber).Where(number => number.HasValue).Distinct().ToArray();
        var selectedId = serviceId;
        if (selectedId is null)
        {
            if (programmes.Length != 1)
            {
                throw new InvalidDataException("The captured programme could not be identified uniquely; no cross-programme remux was attempted.");
            }

            selectedId = programmes[0];
        }

        var selected = tracks.Where(track => track.ProgrammeNumber == selectedId && track.Type is "audio" or "video" or "subtitles")
            .Where(track => kind != ChannelKind.Radio || !string.Equals(track.Type, "video", StringComparison.Ordinal)).ToArray();
        if ((kind == ChannelKind.Television && !selected.Any(track => string.Equals(track.Type, "video", StringComparison.Ordinal)))
            || (kind == ChannelKind.Radio && !selected.Any(track => string.Equals(track.Type, "audio", StringComparison.Ordinal))))
        {
            throw new InvalidDataException("The requested programme has no playable tracks of the expected kind.");
        }

        return selected;
    }

    private async Task<string> IdentifyAsync(string path, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(tool, ["--identification-format", "json", "--identify", Path.GetFullPath(path)], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 1))
        {
            throw new IOException("Could not inspect the recording: " + Diagnostic(result));
        }

        using var document = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() != 0)
        {
            throw new InvalidDataException("MKVToolNix could not identify the recording.");
        }

        return result.StandardOutput;
    }

    private async Task<string?> ValidateOutputAsync(string path, ChannelKind kind, IReadOnlyList<MatroskaTrack>? expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidDataException("Matroska output is missing or empty.");
        }

        var identification = await IdentifyAsync(path, cancellationToken).ConfigureAwait(false);
        var tracks = ParseTracks(identification);
        using var document = JsonDocument.Parse(identification);
        if (!document.RootElement.TryGetProperty("container", out var container)
            || !container.TryGetProperty("type", out var containerType) || !string.Equals(containerType.GetString(), "Matroska", StringComparison.Ordinal)
            || !container.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty("duration", out var duration)
            || !duration.TryGetInt64(out var durationNanoseconds) || durationNanoseconds <= 0)
        {
            throw new InvalidDataException("The output is not a valid Matroska recording with a positive duration.");
        }

        if ((kind == ChannelKind.Television && !tracks.Any(track => string.Equals(track.Type, "video", StringComparison.Ordinal)))
            || (kind == ChannelKind.Radio && !tracks.Any(track => string.Equals(track.Type, "audio", StringComparison.Ordinal))))
        {
            throw new InvalidDataException("Matroska output has no playable tracks.");
        }

        if (expected is not null)
        {
            foreach (var type in new[] { "video", "audio" })
            {
                if (expected.Count(track => string.Equals(track.Type, type, StringComparison.Ordinal)) != tracks.Count(track => string.Equals(track.Type, type, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException("Matroska output did not preserve every selected programme track.");
                }
            }
        }

        if (expected is not null && expected.Count(track => string.Equals(track.Type, "subtitles", StringComparison.Ordinal)) > tracks.Count(track => string.Equals(track.Type, "subtitles", StringComparison.Ordinal)))
        {
            return "Some advertised subtitle tracks produced no Matroska output (for example, no cues during a short recording). The complete transport source was retained for verification.";
        }

        return null;
    }

    private static List<MatroskaTrack> ParseTracks(string identification)
    {
        using var document = JsonDocument.Parse(identification, new JsonDocumentOptions { MaxDepth = 32 });
        if (!document.RootElement.TryGetProperty("tracks", out var array))
        {
            throw new InvalidDataException("Recording identification contains no tracks.");
        }

        var tracks = new List<MatroskaTrack>();
        foreach (var track in array.EnumerateArray())
        {
            int? number = null;
            if (track.TryGetProperty("properties", out var properties)
                && (properties.TryGetProperty("program_number", out var value) || properties.TryGetProperty("programme_number", out value)))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
                {
                    number = parsed;
                }
            }

            tracks.Add(new MatroskaTrack(track.GetProperty("id").GetInt32(), track.GetProperty("type").GetString() ?? string.Empty, track.GetProperty("codec").GetString() ?? string.Empty, number));
        }

        return tracks;
    }

    private static string Diagnostic(MediaProcessResult result)
    {
        var diagnostic = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return diagnostic.Length <= 2000 ? diagnostic.Trim() : diagnostic[..2000].Trim();
    }
}
