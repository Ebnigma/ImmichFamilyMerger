namespace ImmichFamilyMerger;

internal sealed class DeleteAssetsRequest
{
    public required IReadOnlyList<string> Ids { get; init; }
    public bool Force { get; init; }
}
