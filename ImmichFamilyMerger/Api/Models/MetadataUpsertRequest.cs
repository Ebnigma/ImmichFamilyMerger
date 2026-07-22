namespace ImmichFamilyMerger;

internal sealed class MetadataUpsertRequest
{
    public required IReadOnlyList<AssetMetadata> Items { get; init; }
}
