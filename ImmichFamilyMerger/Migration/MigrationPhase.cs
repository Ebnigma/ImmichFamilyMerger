namespace ImmichFamilyMerger;

internal enum MigrationPhase
{
    Discovered,
    Downloaded,
    UploadAttempted,
    Uploaded,
    // Retained so journals written by older releases remain readable.
    RelatedDataCopied,
    MetadataApplied,
    // Retained so journals written by older releases remain readable.
    AlbumAdded,
    Verified,
    SourceTrashed,
    QueueCleaned,
    Complete,
}
