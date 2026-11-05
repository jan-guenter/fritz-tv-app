using System.Diagnostics;
using System.Text;

namespace FritzTV.Infrastructure.Recording;

public sealed class MediaProcessRunner : IMediaProcessRunner
{
    private const int MaximumOutputCharacters = 4 * 1024 * 1024;

    public async Task<MediaProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new IOException("Could not start the media tool.");
        }

        try
        {
            var output = ReadOutputAsync(process.StandardOutput, process, cancellationToken);
            var error = ReadOutputAsync(process.StandardError, process, cancellationToken);
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            return new MediaProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, Process process, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return text.ToString();
            }

            if (text.Length + count > MaximumOutputCharacters)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                throw new InvalidDataException("Media tool output exceeded the permitted size.");
            }

            text.Append(buffer, 0, count);
        }
    }
}
