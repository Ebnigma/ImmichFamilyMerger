namespace ImmichFamilyMerger;

internal sealed class Album
{
    public required string Id { get; init; }
    public required string AlbumName { get; init; }
    public required string OwnerId { get; init; }
    public List<Asset> Assets { get; init; } = [];
}
