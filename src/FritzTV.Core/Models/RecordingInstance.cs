namespace FritzTV.Core.Models;

public sealed record RecordingInstance(
    Guid Id,
    Guid DefinitionId,
    string ChannelId,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset Created,
    RecordingState State,
    string? SourcePath = null,
    string? OutputPath = null,
    string? Error = null);
