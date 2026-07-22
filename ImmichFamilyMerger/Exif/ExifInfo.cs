namespace ImmichFamilyMerger;

internal sealed class ExifInfo
{
    public long? FileSizeInByte { get; init; }
    public string? DateTimeOriginal { get; init; }
    public string? TimeZone { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Description { get; init; }
    public int? Rating { get; init; }
}
