namespace ImmichFamilyMerger;

internal sealed class AppConfig
{
    public required IReadOnlyDictionary<string, string> UserApiKeys { get; init; }
    public required string AppApiKey { get; init; }
    public required Uri ApiBaseUri { get; init; }
    public required string AlbumId { get; init; }
    public string AppDeviceId { get; init; } = "ImmichFamilyMerger";
    public string StatePath { get; init; } = "/data/state.json";
    public int SleepAfterSeconds { get; init; }
    public int MetadataSettleSeconds { get; init; } = 15;
    public bool TrashOriginals { get; init; } = true;
}
