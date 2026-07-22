namespace ImmichFamilyMerger;

internal sealed class BulkUploadCheckResponse
{
    public required IReadOnlyList<BulkUploadCheckResult> Results { get; init; }
}
