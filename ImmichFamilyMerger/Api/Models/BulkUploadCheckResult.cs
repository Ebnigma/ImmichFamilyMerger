namespace ImmichFamilyMerger;

internal sealed class BulkUploadCheckResult
{
    public required string Id { get; init; }
    public required string Action { get; init; }
    public string? AssetId { get; init; }
    public bool? IsTrashed { get; init; }
    public string? Reason { get; init; }
}
