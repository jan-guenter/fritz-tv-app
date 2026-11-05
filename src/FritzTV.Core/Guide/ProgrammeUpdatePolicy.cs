using FritzTV.Core.Models;

namespace FritzTV.Core.Guide;

public static class ProgrammeUpdatePolicy
{
    public static bool ShouldReplace(Programme previous, Programme incoming, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(incoming);
        if (previous == incoming || Rank(previous, now) > Rank(incoming, now))
        {
            return false;
        }

        if (previous.End <= now && incoming.Start >= now)
        {
            return true;
        }

        return previous.TableId != incoming.TableId || previous.Version == incoming.Version || ((incoming.Version - previous.Version + 32) % 32) < 16;
    }

    private static int Rank(Programme programme, DateTimeOffset now) => (programme.IsActualTransport ? 2 : 0) + (programme.TableId == 0x4e && programme.Start <= now && programme.End > now ? 2 : 0);
}
