using FritzTV.Core.Models;

namespace FritzTV.Infrastructure.Persistence;

public sealed record RecordingDefinitionSnapshot(IReadOnlyList<RecordingDefinition> Definitions, IReadOnlyList<StoredDefinitionFailure> Failures);

