using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

using ImmichFamilyMerger;
class Program
{
    static async Task Main(string[] args)
    {
        try
        {
            var config = ParseArguments(args);
            using var httpClient = new HttpClient();

            while (true)
            {
                var migrator = new AlbumAssetMigrator(httpClient, config);
                bool success = await migrator.MigrateAlbumAssetsAsync();
                if (!success)
                    break;

                if (config.SleepAfterSeconds > 0)
                {
                    Console.WriteLine($"Sleeping for {config.SleepAfterSeconds} seconds...");
                    await Task.Delay(config.SleepAfterSeconds * 1000);
                }
                else
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
        }
    }

    private static AppConfig ParseArguments(string[] args)
    {
        var argDict = args
            .Where(a => a.StartsWith("--") && a.Contains('='))
            .Select(a => a.Substring(2).Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1]);

        string[] requiredArgs = ["userApiKeys", "appApiKey", "baseUrl", "albumId"];
        var missing = requiredArgs.Where(r => !argDict.ContainsKey(r) || string.IsNullOrWhiteSpace(argDict[r])).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException("Missing required arguments: " + string.Join(", ", missing) +
                "\nUsage: ImmichFamilyMerger --userApiKeys=user1:apikey1,user2:apikey2 --appApiKey=appkey --baseUrl=https://example.com --albumId=albumid [--sleepTime=10]");
        }

        int sleepAfterSeconds = 0;
        if (argDict.TryGetValue("sleepTime", out var sleepStr) && int.TryParse(sleepStr, out var sleepVal) && sleepVal > 0)
        {
            sleepAfterSeconds = sleepVal;
        }

        string userApiKeysArg = argDict["userApiKeys"];
        var userApiKey = new Dictionary<string, string>();
        foreach (var pair in userApiKeysArg.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new ArgumentException($"Invalid user API key format: {pair}");
            }
            userApiKey[parts[0]] = parts[1];
        }

        string appApiKey = argDict["appApiKey"];
        string baseUrl = argDict["baseUrl"].TrimEnd('/');
        string albumId = argDict["albumId"];

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Invalid baseUrl. Must be a valid absolute URL.");
        }
        if (string.IsNullOrWhiteSpace(albumId))
        {
            throw new ArgumentException("albumId cannot be empty.");
        }

        // Write config to console for debugging
        Console.WriteLine("Parsed configuration:");
        Console.WriteLine($"  BaseUrl: {baseUrl}");
        Console.WriteLine($"  AlbumId: {albumId}");
        Console.WriteLine($"  AppApiKey: {appApiKey}");
        Console.WriteLine($"  SleepAfterSeconds: {sleepAfterSeconds}");
        Console.WriteLine("  UserApiKeys:");
        foreach (var kvp in userApiKey)
        {
            Console.WriteLine($"    {kvp.Key}: {new string('*', Math.Max(4, kvp.Value.Length))}");
        }

        return new AppConfig
        {
            UserApiKeys = userApiKey,
            AppApiKey = appApiKey,
            BaseUrl = baseUrl,
            AlbumId = albumId,
            SleepAfterSeconds = sleepAfterSeconds
        };

    }

    //private static async Task<bool> MigrateAlbumAssetsAsync(HttpClient httpClient, AppConfig config)
    //{
    //    const string AppDeviceId = "ImmichFamilyMerger";
    //    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    //    // 1. Get Album Info
    //    httpClient.DefaultRequestHeaders.Clear();
    //    httpClient.DefaultRequestHeaders.Add("x-api-key", config.AppApiKey);
    //    var albumResponse = await httpClient.GetAsync($"{config.BaseUrl}/api/albums/{config.AlbumId}");
    //    if (!albumResponse.IsSuccessStatusCode)
    //    {
    //        var errorContent = await albumResponse.Content.ReadAsStringAsync();
    //        Console.WriteLine($"Failed to fetch album info. Status: {albumResponse.StatusCode}, Error: {errorContent}");
    //        return false;
    //    }
    //    var albumJson = await albumResponse.Content.ReadAsStringAsync();
    //    var album = JsonSerializer.Deserialize<Album>(albumJson, jsonOptions);

    //    if (album == null)
    //    {
    //        Console.WriteLine("Failed to deserialize album info.");
    //        return false;
    //    }

    //    if (album.Assets == null || album.Assets.Count == 0)
    //    {
    //        Console.WriteLine("No assets found in the album.");
    //        return true;
    //    }

    //    foreach (var asset in album.Assets)
    //    {
    //        config.UserApiKeys.TryGetValue(asset.OwnerId, out string? sourceApiKey);
    //        if (string.IsNullOrWhiteSpace(sourceApiKey))
    //        {
    //            Console.WriteLine($"No API key found for user {asset.OwnerId}. Skipping asset {asset.Id}.");
    //            continue;
    //        }

    //        // 2. Download Asset
    //        httpClient.DefaultRequestHeaders.Clear();
    //        httpClient.DefaultRequestHeaders.Add("x-api-key", sourceApiKey);
    //        var assetDownloadResponse = await httpClient.GetAsync($"{config.BaseUrl}/api/assets/{asset.Id}/original");
    //        if (!assetDownloadResponse.IsSuccessStatusCode)
    //        {
    //            Console.WriteLine($"Failed to download asset {asset.Id}.");
    //            return false;
    //        }
    //        var assetBytes = await assetDownloadResponse.Content.ReadAsByteArrayAsync();

    //        // 3. Upload Asset to Target
    //        httpClient.DefaultRequestHeaders.Clear();
    //        httpClient.DefaultRequestHeaders.Add("x-api-key", config.AppApiKey);

    //        using var content = new MultipartFormDataContent
    //        {
    //            { new ByteArrayContent(assetBytes), "assetData", asset.OriginalFileName },
    //            { new StringContent(AppDeviceId), "deviceId" },
    //            { new StringContent(Guid.NewGuid().ToString()), "deviceAssetId" },
    //            { new StringContent(asset.Duration), "duration" },
    //            { new StringContent(asset.FileCreatedAt.ToString("o", CultureInfo.InvariantCulture)), "fileCreatedAt" },
    //            { new StringContent(asset.FileModifiedAt.ToString("o", CultureInfo.InvariantCulture)), "fileModifiedAt" }
    //        };

    //        var uploadResponse = await httpClient.PostAsync($"{config.BaseUrl}/api/assets", content);
    //        if (!uploadResponse.IsSuccessStatusCode)
    //        {
    //            var errorContent = await uploadResponse.Content.ReadAsStringAsync();
    //            Console.WriteLine($"Failed to upload asset. Status: {uploadResponse.StatusCode}, Error: {errorContent}");
    //            return false;
    //        }

    //        // 4. Delete Original Asset
    //        httpClient.DefaultRequestHeaders.Clear();
    //        httpClient.DefaultRequestHeaders.Add("x-api-key", sourceApiKey);
    //        var deleteRequestBody = new DeleteRequestBody()
    //        {
    //            Ids = [asset.Id]
    //        };
    //        var deleteBody = new StringContent(JsonSerializer.Serialize(deleteRequestBody, jsonOptions), System.Text.Encoding.UTF8, "application/json");
    //        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{config.BaseUrl}/api/assets")
    //        {
    //            Content = deleteBody
    //        };
    //        var deleteResponse = await httpClient.SendAsync(deleteRequest);
    //        if (!deleteResponse.IsSuccessStatusCode)
    //        {
    //            var errorContent = await deleteResponse.Content.ReadAsStringAsync();
    //            Console.WriteLine($"Failed to delete asset {asset.Id}. Status: {deleteResponse.StatusCode}, Error: {errorContent}");
    //            return false;
    //        }

    //        Console.WriteLine($"Asset {asset.Id} migrated successfully.");
    //    }

    //    Console.WriteLine("Migration process completed.");
    //    return true;
    //}
}