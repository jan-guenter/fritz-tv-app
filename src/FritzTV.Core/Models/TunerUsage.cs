namespace FritzTV.Core.Models;

public sealed record TunerUsage(Guid DeviceId, int ActiveSessions, int TunerCount, int Consumers, int RecordingConsumers, int ViewingConsumers, int GuideConsumers);
