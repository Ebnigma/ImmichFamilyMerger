namespace ImmichFamilyMerger;

internal static class AppConfigParser
{
    public static AppConfig Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in args.Where(value =>
                     value.StartsWith("--", StringComparison.Ordinal) && value.Contains('=')))
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
        var missing = required
            .Where(key => !values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"Missing required settings: {string.Join(", ", missing)}. " +
                "Set USER_API_KEYS, APP_API_KEY, BASE_URL and ALBUM_ID, or pass the corresponding --name=value arguments.");
        }

        var userApiKeys = ParseUserApiKeys(values["userApiKeys"]);
        var apiBaseUri = ParseApiBaseUri(values["baseUrl"]);
        var statePath = values.GetValueOrDefault("statePath", "/data/state.json");
        if (!Path.IsPathFullyQualified(statePath))
        {
            throw new ArgumentException("STATE_PATH must be an absolute path inside the container.");
        }

        var trashOriginals = false;
        if (values.TryGetValue("trashOriginals", out var trashValue) &&
            !bool.TryParse(trashValue, out trashOriginals))
        {
            throw new ArgumentException("trashOriginals must be true or false.");
        }

        var config = new AppConfig
        {
            UserApiKeys = userApiKeys,
            AppApiKey = values["appApiKey"],
            ApiBaseUri = apiBaseUri,
            AlbumId = values["albumId"],
            StatePath = statePath,
            SleepAfterSeconds = ParseNonNegativeInt(values, "sleepTime", 0),
            MetadataSettleSeconds = ParseNonNegativeInt(values, "metadataSettleTime", 15),
            TrashOriginals = trashOriginals,
        };

        PrintSummary(config);
        return config;
    }

    private static Dictionary<string, string> ParseUserApiKeys(string value)
    {
        var apiKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf(':');
            if (separator <= 0 || separator == pair.Length - 1)
            {
                throw new ArgumentException("USER_API_KEYS must use the form user-id:api-key,user-id:api-key.");
            }

            apiKeys.Add(pair[..separator].Trim(), pair[(separator + 1)..].Trim());
        }

        return apiKeys;
    }

    private static Uri ParseApiBaseUri(string value)
    {
        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var serverUri) ||
            (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("BASE_URL must be an absolute HTTP or HTTPS URL.");
        }

        return serverUri.AbsolutePath.TrimEnd('/').EndsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? serverUri
            : new Uri(serverUri, "api/");
    }

    private static void AddEnvironmentFallback(
        IDictionary<string, string> values,
        string key,
        string environmentVariable)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (!values.ContainsKey(key) && !string.IsNullOrWhiteSpace(value))
        {
            values[key] = value;
        }
    }

    private static int ParseNonNegativeInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int defaultValue)
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

    private static void PrintSummary(AppConfig config)
    {
        Console.WriteLine("Configuration loaded:");
        Console.WriteLine($"  API: {config.ApiBaseUri}");
        Console.WriteLine($"  Album: {config.AlbumId}");
        Console.WriteLine($"  Source accounts: {config.UserApiKeys.Count}");
        Console.WriteLine($"  State: {config.StatePath}");
        Console.WriteLine($"  Trash originals after verification: {config.TrashOriginals}");
    }
}
