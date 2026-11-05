using FritzTV.Core.Exceptions;

namespace FritzTV.Infrastructure.Transport;

internal sealed record RtspResponse(int StatusCode, string Status, IReadOnlyDictionary<string, string> Headers)
{
    public void EnsureSuccess()
    {
        if (StatusCode == 453)
        {
            throw new TunerUnavailableException($"The SAT>IP device has no free tuner: {Status}");
        }

        if (StatusCode != 200)
        {
            throw new IOException($"The SAT>IP server rejected the request: {Status}");
        }
    }
}
