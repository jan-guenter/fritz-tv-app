namespace FritzTV.Core.Models;

public sealed record TunerStatus(int Number, bool InUse, bool HasLock, string SignalPower, string SignalNoiseRatio, string ChannelName, int ClientCount);
