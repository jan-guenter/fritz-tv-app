namespace FritzTV.Core.Models;

public sealed record ReceptionStatus(int Frontend, int SignalLevel, bool HasLock, int SignalQuality, decimal? FrequencyMhz, string Parameters);
