namespace ImmichFamilyMerger;

internal sealed class MigrationState
{
    public int Version { get; init; } = 1;
    public Dictionary<string, MigrationRecord> Assets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
