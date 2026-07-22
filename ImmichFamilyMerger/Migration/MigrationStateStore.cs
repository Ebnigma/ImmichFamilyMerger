using System.Text.Json;

namespace ImmichFamilyMerger;

internal sealed class MigrationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
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
