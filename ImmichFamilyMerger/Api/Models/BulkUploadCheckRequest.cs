namespace ImmichFamilyMerger;

internal sealed class BulkUploadCheckRequest
{
    public required IReadOnlyList<BulkUploadCheckItem> Assets { get; init; }
}
