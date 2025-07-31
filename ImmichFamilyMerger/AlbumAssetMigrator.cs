using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ImmichFamilyMerger
{
    internal class AlbumAssetMigrator
    {
        private readonly HttpClient _httpClient;
        private readonly AppConfig _config;
        private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly string _appDeviceId;

        public AlbumAssetMigrator(HttpClient httpClient, AppConfig config)
        {
            _httpClient = httpClient;
            _config = config;
            _appDeviceId = config.AppDeviceId ?? "ImmichFamilyMerger";
        }

        public async Task<bool> MigrateAlbumAssetsAsync()
        {
            var album = await GetAlbumInfoAsync();
            if (album == null)
                return false;

            if (album.Assets == null || album.Assets.Count == 0)
            {
                Console.WriteLine("No assets found in the album.");
                return true;
            }

            foreach (var asset in album.Assets)
            {
                if (!_config.UserApiKeys.TryGetValue(asset.OwnerId, out string? sourceApiKey) || string.IsNullOrWhiteSpace(sourceApiKey))
                {
                    Console.WriteLine($"No API key found for user {asset.OwnerId}. Skipping asset {asset.Id}.");
                    continue;
                }

                var assetBytes = await DownloadAssetAsync(asset.Id, sourceApiKey);
                if (assetBytes == null)
                    return false;

                if (!await UploadAssetAsync(asset, assetBytes))
                    return false;

                if (!await DeleteOriginalAssetAsync(asset.Id, sourceApiKey))
                    return false;

                Console.WriteLine($"Asset {asset.Id} migrated successfully.");
            }

            Console.WriteLine("Migration process completed.");
            return true;
        }

        private async Task<Album?> GetAlbumInfoAsync()
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _config.AppApiKey);
            var response = await _httpClient.GetAsync($"{_config.BaseUrl}/api/albums/{_config.AlbumId}");
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Failed to fetch album info. Status: {response.StatusCode}, Error: {errorContent}");
                return null;
            }
            var albumJson = await response.Content.ReadAsStringAsync();
            var album = JsonSerializer.Deserialize<Album>(albumJson, _jsonOptions);
            if (album == null)
                Console.WriteLine("Failed to deserialize album info.");
            return album;
        }

        private async Task<byte[]?> DownloadAssetAsync(string assetId, string apiKey)
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
            var response = await _httpClient.GetAsync($"{_config.BaseUrl}/api/assets/{assetId}/original");
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to download asset {assetId}.");
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync();
        }

        private async Task<bool> UploadAssetAsync(Asset asset, byte[] assetBytes)
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _config.AppApiKey);

            var content = new MultipartFormDataContent
            {
                { new ByteArrayContent(assetBytes), "assetData", asset.OriginalFileName },
                { new StringContent(_appDeviceId), "deviceId" },
                { new StringContent(Guid.NewGuid().ToString()), "deviceAssetId" },
                { new StringContent(asset.Duration), "duration" },
                { new StringContent(asset.FileCreatedAt.ToString("o", CultureInfo.InvariantCulture)), "fileCreatedAt" },
                { new StringContent(asset.FileModifiedAt.ToString("o", CultureInfo.InvariantCulture)), "fileModifiedAt" }
            };

            var response = await _httpClient.PostAsync($"{_config.BaseUrl}/api/assets", content);
            Console.WriteLine($"Assets uploaded. Status: {response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Failed to upload asset. Status: {response.StatusCode}, Error: {errorContent}");
                return false;
            }
            return true;
        }

        private async Task<bool> DeleteOriginalAssetAsync(string assetId, string apiKey)
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
            var deleteRequestBody = new DeleteRequestBody()
            {
                Ids = [assetId]
            };
            var deleteBody = new StringContent(JsonSerializer.Serialize(deleteRequestBody, _jsonOptions), System.Text.Encoding.UTF8, "application/json");
            var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{_config.BaseUrl}/api/assets")
            {
                Content = deleteBody
            };
            var response = await _httpClient.SendAsync(deleteRequest);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Failed to delete asset {assetId}. Status: {response.StatusCode}, Error: {errorContent}");
                return false;
            }
            return true;
        }
    }
}