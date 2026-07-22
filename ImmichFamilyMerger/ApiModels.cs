using System.Text.Json;

namespace ImmichFamilyMerger;

internal sealed class UserInfo
{
    public required string Id { get; init; }
    public string? Email { get; init; }
}

internal sealed class AlbumAsset
{
    public required string Id { get; init; }
    public required string OwnerId { get; init; }
}

internal sealed class AssetSearchResponse
{
    public required AssetSearchPage Assets { get; init; }
}

internal sealed class AssetSearchPage
{
    public List<AlbumAsset> Items { get; init; } = [];
    public int Total { get; init; }
    public string? NextPage { get; init; }
}

internal sealed class UploadResponse
{
    public required string Id { get; init; }
    public required string Status { get; init; }
}

internal sealed class BulkIdResponse
{
    public required string Id { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? ErrorMessage { get; init; }
}

internal sealed class AssetMetadata
{
    public required string Key { get; init; }
    public JsonElement Value { get; init; }
}

internal sealed class MetadataUpsertRequest
{
    public required IReadOnlyList<AssetMetadata> Items { get; init; }
}

internal sealed class IdsRequest
{
    public required IReadOnlyList<string> Ids { get; init; }
}

internal sealed class DeleteAssetsRequest
{
    public required IReadOnlyList<string> Ids { get; init; }
    public bool Force { get; init; }
}
