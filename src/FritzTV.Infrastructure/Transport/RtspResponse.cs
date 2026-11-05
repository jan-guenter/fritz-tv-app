namespace FritzTV.Infrastructure.Transport;

internal sealed record RtspResponse(int StatusCode, string Status, IReadOnlyDictionary<string, string> Headers)
{
    public void EnsureSuccess()
    {
        if (StatusCode != 200)
        {
            throw new IOException($"The SAT>IP server rejected the request: {Status}");
        }
    }
}
