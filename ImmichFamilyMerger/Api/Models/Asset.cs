using System.Text.Json;

namespace ImmichFamilyMerger;

internal sealed class Asset
{
    public required string Id { get; init; }
    public required string OwnerId { get; init; }
    public string? DeviceAssetId { get; init; }
    public required string Type { get; init; }
    public required string OriginalFileName { get; init; }
    public string? OriginalMimeType { get; init; }
    public required string FileCreatedAt { get; init; }
    public required string FileModifiedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsEdited { get; init; }
    public bool IsTrashed { get; init; }
    public required string Visibility { get; init; }
    public JsonElement Duration { get; init; }
    public ExifInfo? ExifInfo { get; init; }
    public string? LivePhotoVideoId { get; init; }
    public JsonElement? Stack { get; init; }
    public required string Checksum { get; init; }
    public bool HasMetadata { get; init; }
}
