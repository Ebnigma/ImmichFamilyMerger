using System.Text.Json;

namespace ImmichFamilyMerger;

internal sealed class AssetMetadata
{
    public required string Key { get; init; }
    public JsonElement Value { get; init; }
}
