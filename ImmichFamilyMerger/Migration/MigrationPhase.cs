namespace ImmichFamilyMerger;

internal enum MigrationPhase
{
    Discovered,
    Downloaded,
    Uploaded,
    // Retained so journals written by older releases remain readable.
    RelatedDataCopied,
    MetadataApplied,
    AlbumAdded,
    Verified,
    SourceTrashed,
    Complete,
}
