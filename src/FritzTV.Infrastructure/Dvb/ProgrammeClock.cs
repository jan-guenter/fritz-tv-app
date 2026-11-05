namespace FritzTV.Infrastructure.Dvb;

public sealed class ProgrammeClock
{
    private const long Wrap = 1L << 33;
    private long? previous;
    private long unwrapped;
    private long anchor;
    private DateTimeOffset anchorTime;

    public DateTimeOffset Update(long pcr, DateTimeOffset observed, bool discontinuity = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pcr);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pcr, Wrap);
        var delta = previous is { } last ? (pcr - last + Wrap) % Wrap : 0;
        if (delta > Wrap / 2)
        {
            delta -= Wrap;
        }

        if (previous is null || discontinuity || Math.Abs(delta) > 30 * 90000)
        {
            unwrapped = pcr;
            anchor = pcr;
            anchorTime = observed;
        }
        else
        {
            unwrapped += delta;
        }

        previous = pcr;
        return anchorTime + TimeSpan.FromSeconds((unwrapped - anchor) / 90000d);
    }
}
