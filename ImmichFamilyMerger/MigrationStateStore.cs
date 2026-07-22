using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImmichFamilyMerger;

internal enum MigrationPhase
{
    Discovered,
    Downloaded,
    Uploaded,
    RelatedDataCopied,
    MetadataApplied,
    AlbumAdded,
    Verified,
    SourceTrashed,
    Complete,
}

internal sealed class MigrationRecord
{
    public required string SourceAssetId { get; init; }
    public required string SourceOwnerId { get; init; }
    public required string DeviceAssetId { get; init; }
    public required Asset Source { get; init; }
    public List<AssetMetadata> Metadata { get; init; } = [];
    public MigrationPhase Phase { get; set; }
    public string? DestinationAssetId { get; set; }
    public string? VerifiedChecksum { get; set; }
    public long? VerifiedLength { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

internal sealed class MigrationState
{
    public int Version { get; init; } = 1;
    public Dictionary<string, MigrationRecord> Assets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class MigrationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MigrationState _state;

    private MigrationStateStore(string path, MigrationState state)
    {
        _path = path;
        _state = state;
        MediaDirectory = Path.Combine(Path.GetDirectoryName(path)!, "media");
    }

    public string MediaDirectory { get; }

    public IEnumerable<MigrationRecord> IncompleteRecords =>
        _state.Assets.Values.Where(record => record.Phase != MigrationPhase.Complete).OrderBy(record => record.UpdatedAt).ToArray();

    public static async Task<MigrationStateStore> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The migration state path must have a parent directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "media"));

        MigrationState state;
        if (!File.Exists(path))
        {
            state = new MigrationState();
        }
        else
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                state = await JsonSerializer.DeserializeAsync<MigrationState>(stream, JsonOptions, cancellationToken)
                        ?? throw new InvalidDataException("The migration state file is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Migration state '{path}' is corrupt. It was left untouched; restore it from backup before continuing.", exception);
            }
        }

        if (state.Version != 1)
        {
            throw new InvalidDataException($"Unsupported migration state version {state.Version}.");
        }

        var store = new MigrationStateStore(path, state);
        if (!File.Exists(path))
        {
            await store.SaveAsync(cancellationToken);
        }

        return store;
    }

    public bool Contains(string sourceAssetId) => _state.Assets.ContainsKey(sourceAssetId);

    public async Task<MigrationRecord> AddAsync(
        Asset source,
        IReadOnlyList<AssetMetadata> metadata,
        string deviceAssetId,
        CancellationToken cancellationToken)
    {
        if (_state.Assets.TryGetValue(source.Id, out var existing))
        {
            return existing;
        }

        var record = new MigrationRecord
        {
            SourceAssetId = source.Id,
            SourceOwnerId = source.OwnerId,
            DeviceAssetId = deviceAssetId,
            Source = source,
            Metadata = metadata.ToList(),
            Phase = MigrationPhase.Discovered,
        };
        _state.Assets.Add(source.Id, record);
        await SaveAsync(cancellationToken);
        return record;
    }

    public async Task SaveAsync(MigrationRecord record, CancellationToken cancellationToken)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(cancellationToken);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var temporaryPath = _path + ".tmp";
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, _state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
