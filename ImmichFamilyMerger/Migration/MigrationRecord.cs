namespace ImmichFamilyMerger;

internal sealed class MigrationRecord
{
    public required string SourceAssetId { get; init; }
    public required string SourceOwnerId { get; init; }
    public required string DeviceAssetId { get; init; }
    public required Asset Source { get; init; }
    public List<AssetMetadata> Metadata { get; init; } = [];
    public MigrationPhase Phase { get; set; }
    public string? DestinationAssetId { get; set; }
    public string? VerifiedChecksum { get; set; }
    public long? VerifiedLength { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
