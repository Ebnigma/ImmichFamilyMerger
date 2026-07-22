namespace ImmichFamilyMerger;

internal sealed class AssetSearchPage
{
    public List<AlbumAsset> Items { get; init; } = [];
    public int Total { get; init; }
    public string? NextPage { get; init; }
}
