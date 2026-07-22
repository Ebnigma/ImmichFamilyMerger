using ImmichFamilyMerger;
using System.Runtime.InteropServices;

try
{
    var config = ParseArguments(args);
    using var httpClient = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    Directory.CreateDirectory(Path.GetDirectoryName(config.StatePath)!);
    using var instanceLock = AcquireInstanceLock(config.StatePath);
    var api = new ImmichApiClient(httpClient, config.ApiBaseUri);
    var state = await MigrationStateStore.OpenAsync(config.StatePath, CancellationToken.None);
    var migrator = new AlbumAssetMigrator(api, state, config);

    using var shutdown = new CancellationTokenSource();
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        shutdown.Cancel();
    });
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    do
    {
        await migrator.MigrateAlbumAssetsAsync(shutdown.Token);
        if (config.SleepAfterSeconds <= 0)
        {
            break;
        }

        Console.WriteLine($"Cycle complete. Sleeping for {config.SleepAfterSeconds} seconds.");
        await Task.Delay(TimeSpan.FromSeconds(config.SleepAfterSeconds), shutdown.Token);
    }
    while (!shutdown.IsCancellationRequested);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Shutdown requested.");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Fatal error: {exception.Message}");
    Environment.ExitCode = 1;
}

static AppConfig ParseArguments(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var argument in args.Where(value => value.StartsWith("--", StringComparison.Ordinal) && value.Contains('=')))
    {
        var parts = argument[2..].Split('=', 2);
        values[parts[0]] = parts[1];
    }

    AddEnvironmentFallback(values, "userApiKeys", "USER_API_KEYS");
    AddEnvironmentFallback(values, "appApiKey", "APP_API_KEY");
    AddEnvironmentFallback(values, "baseUrl", "BASE_URL");
    AddEnvironmentFallback(values, "albumId", "ALBUM_ID");
    AddEnvironmentFallback(values, "sleepTime", "SLEEP_TIME");
    AddEnvironmentFallback(values, "statePath", "STATE_PATH");
    AddEnvironmentFallback(values, "trashOriginals", "TRASH_ORIGINALS");
    AddEnvironmentFallback(values, "metadataSettleTime", "METADATA_SETTLE_TIME");

    var required = new[] { "userApiKeys", "appApiKey", "baseUrl", "albumId" };
    var missing = required.Where(key => !values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)).ToArray();
    if (missing.Length > 0)
    {
        throw new ArgumentException(
            $"Missing required settings: {string.Join(", ", missing)}. " +
            "Set USER_API_KEYS, APP_API_KEY, BASE_URL and ALBUM_ID, or pass the corresponding --name=value arguments.");
    }

    var userApiKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in values["userApiKeys"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var separator = pair.IndexOf(':');
        if (separator <= 0 || separator == pair.Length - 1)
        {
            throw new ArgumentException("USER_API_KEYS must use the form user-id:api-key,user-id:api-key.");
        }

        userApiKeys.Add(pair[..separator].Trim(), pair[(separator + 1)..].Trim());
    }

    if (!Uri.TryCreate(values["baseUrl"].TrimEnd('/') + "/", UriKind.Absolute, out var serverUri) ||
        (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
    {
        throw new ArgumentException("BASE_URL must be an absolute HTTP or HTTPS URL.");
    }

    var apiBase = serverUri.AbsolutePath.TrimEnd('/').EndsWith("/api", StringComparison.OrdinalIgnoreCase)
        ? serverUri
        : new Uri(serverUri, "api/");

    var sleepSeconds = ParseNonNegativeInt(values, "sleepTime", 0);
    var settleSeconds = ParseNonNegativeInt(values, "metadataSettleTime", 15);
    var statePath = values.GetValueOrDefault("statePath", "/data/state.json");
    if (!Path.IsPathFullyQualified(statePath))
    {
        throw new ArgumentException("STATE_PATH must be an absolute path inside the container.");
    }

    var trashOriginals = true;
    if (values.TryGetValue("trashOriginals", out var trashValue) && !bool.TryParse(trashValue, out trashOriginals))
    {
        throw new ArgumentException("trashOriginals must be true or false.");
    }

    Console.WriteLine("Configuration loaded:");
    Console.WriteLine($"  API: {apiBase}");
    Console.WriteLine($"  Album: {values["albumId"]}");
    Console.WriteLine($"  Source accounts: {userApiKeys.Count}");
    Console.WriteLine($"  State: {statePath}");
    Console.WriteLine($"  Trash originals after verification: {trashOriginals}");

    return new AppConfig
    {
        UserApiKeys = userApiKeys,
        AppApiKey = values["appApiKey"],
        ApiBaseUri = apiBase,
        AlbumId = values["albumId"],
        StatePath = statePath,
        SleepAfterSeconds = sleepSeconds,
        MetadataSettleSeconds = settleSeconds,
        TrashOriginals = trashOriginals,
    };
}

static void AddEnvironmentFallback(IDictionary<string, string> values, string key, string environmentVariable)
{
    var value = Environment.GetEnvironmentVariable(environmentVariable);
    if (!values.ContainsKey(key) && !string.IsNullOrWhiteSpace(value))
    {
        values[key] = value;
    }
}

static int ParseNonNegativeInt(IReadOnlyDictionary<string, string> values, string key, int defaultValue)
{
    if (!values.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
    {
        return defaultValue;
    }

    if (!int.TryParse(text, out var value) || value < 0)
    {
        throw new ArgumentException($"{key} must be a non-negative integer.");
    }

    return value;
}

static FileStream AcquireInstanceLock(string statePath)
{
    try
    {
        return new FileStream(statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException exception)
    {
        throw new InvalidOperationException(
            "Another Immich Family Merger process is already using this state path. Run exactly one replica per journal.",
            exception);
    }
}
