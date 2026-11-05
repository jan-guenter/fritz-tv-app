namespace FritzTV.Infrastructure.Dvb;

public sealed record ElementaryStreamInfo(int Pid, byte StreamType, string Language, bool IsTeletext, bool IsDvbSubtitle);
