using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace FritzTV.Infrastructure.Transport;

internal sealed class RtspConnection(TcpClient client) : IAsyncDisposable
{
    private readonly NetworkStream stream = client.GetStream();
    private readonly SemaphoreSlim gate = new(1, 1);
    private int sequence;

    public async Task<RtspResponse> SendAsync(string method, Uri target, string? session = null, string? transport = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = new StringBuilder().Append(method).Append(' ').Append(target.AbsoluteUri).Append(" RTSP/1.0\r\nCSeq: ").Append(++sequence).Append("\r\nUser-Agent: FritzTV/0.1\r\n");
            if (session is not null)
            {
                request.Append("Session: ").Append(session).Append("\r\n");
            }

            if (transport is not null)
            {
                request.Append("Transport: ").Append(transport).Append("\r\n");
            }

            request.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancellationToken).ConfigureAwait(false);
            return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RtspResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var status = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var parts = status.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("RTSP/", StringComparison.Ordinal) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
        {
            throw new InvalidDataException("The device returned an invalid RTSP status line.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerBytes = status.Length;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            headerBytes += line.Length;
            if (headerBytes > 32768)
            {
                throw new InvalidDataException("RTSP response headers exceed 32 KiB.");
            }

            if (line.Length == 0)
            {
                break;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException("The device returned an invalid RTSP response header.");
            }

            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        if (headers.TryGetValue("Content-Length", out var lengthText))
        {
            if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < 0 or > 1024 * 1024)
            {
                throw new InvalidDataException("The RTSP response body exceeds its size limit.");
            }

            var buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        return new RtspResponse(statusCode, status, headers);
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        client.Dispose();
        gate.Dispose();
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var result = new List<byte>();
        var buffer = new byte[1];
        while (result.Count <= 8192)
        {
            if (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new EndOfStreamException("The SAT>IP server closed its RTSP connection.");
            }

            if (buffer[0] == 10)
            {
                if (result.Count > 0 && result[^1] == 13)
                {
                    result.RemoveAt(result.Count - 1);
                }

                return Encoding.ASCII.GetString(result.ToArray());
            }

            result.Add(buffer[0]);
        }

        throw new InvalidDataException("The RTSP response line exceeds 8 KiB.");
    }
}

