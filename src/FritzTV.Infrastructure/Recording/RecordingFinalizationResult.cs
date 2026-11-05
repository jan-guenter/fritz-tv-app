namespace FritzTV.Infrastructure.Recording;

public sealed record RecordingFinalizationResult(string OutputPath, bool SourceRetained, string? Warning = null);
