using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImmichFamilyMerger;

internal sealed class ImmichApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _apiBaseUri;

    public ImmichApiClient(HttpClient httpClient, Uri apiBaseUri)
    {
        _httpClient = httpClient;
        _apiBaseUri = apiBaseUri;
    }

    public Task<UserInfo> GetMeAsync(string apiKey, CancellationToken cancellationToken) =>
        GetJsonAsync<UserInfo>(HttpMethod.Get, "users/me", apiKey, cancellationToken);

    public Task<Album> GetAlbumAsync(string albumId, string apiKey, CancellationToken cancellationToken) =>
        GetJsonAsync<Album>(HttpMethod.Get, $"albums/{Escape(albumId)}", apiKey, cancellationToken);

    public async Task<IReadOnlyList<AlbumAsset>> GetAlbumAssetsAsync(
        string albumId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        var assets = new List<AlbumAsset>();
        var assetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? expectedTotal = null;
        var page = 1;

        while (true)
        {
            var response = await SearchAssetsAsync(albumId, null, page, pageSize, apiKey, cancellationToken);
            expectedTotal ??= response.Assets.Total;
            if (response.Assets.Total != expectedTotal)
            {
                throw new InvalidOperationException(
                    "The album changed while its assets were being paginated; the scan will retry next cycle.");
            }

            foreach (var asset in response.Assets.Items)
            {
                if (!assetIds.Add(asset.Id))
                {
                    throw new InvalidDataException($"Immich returned album asset {asset.Id} more than once.");
                }

                assets.Add(asset);
            }

            if (response.Assets.NextPage is null)
            {
                if (assets.Count != expectedTotal)
                {
                    throw new InvalidDataException(
                        $"Immich reported {expectedTotal} album assets, but pagination returned {assets.Count}.");
                }

                return assets;
            }

            if (!int.TryParse(response.Assets.NextPage, NumberStyles.None, CultureInfo.InvariantCulture, out var nextPage) ||
                nextPage <= page)
            {
                throw new InvalidDataException(
                    $"Immich returned invalid asset-search page token '{response.Assets.NextPage}'.");
            }

            page = nextPage;
        }
    }

    public async Task<bool> IsAssetInAlbumAsync(
        string albumId,
        string assetId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var response = await SearchAssetsAsync(albumId, assetId, 1, 1, apiKey, cancellationToken);
        return response.Assets.Items.Any(asset => asset.Id.Equals(assetId, StringComparison.OrdinalIgnoreCase));
    }

    public Task<Asset> GetAssetAsync(string assetId, string apiKey, CancellationToken cancellationToken) =>
        GetJsonAsync<Asset>(HttpMethod.Get, $"assets/{Escape(assetId)}", apiKey, cancellationToken);

    public async Task<IReadOnlyList<AssetMetadata>> GetMetadataAsync(
        string assetId,
        string apiKey,
        CancellationToken cancellationToken) =>
        await GetJsonAsync<List<AssetMetadata>>(
            HttpMethod.Get,
            $"assets/{Escape(assetId)}/metadata",
            apiKey,
            cancellationToken);

    public async Task<FileDigest> DownloadOriginalAsync(
        string assetId,
        string apiKey,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var partialPath = destinationPath + ".part";
        Exception? lastException = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, $"assets/{Escape(assetId)}/original", apiKey);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (IsTransient(response.StatusCode) && attempt < 4)
                {
                    response.Dispose();
                    await DelayForRetryAsync(response, attempt, cancellationToken);
                    continue;
                }

                await EnsureSuccessAsync(response, $"download asset {assetId}", cancellationToken);
                string checksum;
                long length = 0;
                {
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var output = new FileStream(
                        partialPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hash.AppendData(buffer, 0, read);
                        length += read;
                    }

                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                    checksum = Convert.ToBase64String(hash.GetHashAndReset());
                }

                File.Move(partialPath, destinationPath, overwrite: true);
                return new FileDigest(checksum, length);
            }
            catch (Exception exception) when (IsRetryableException(exception, cancellationToken) && attempt < 4)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
            }
        }

        throw new IOException($"Unable to download asset {assetId} after four attempts.", lastException);
    }

    public async Task<UploadResponse> UploadAsync(
        Asset source,
        string filePath,
        string deviceId,
        string deviceAssetId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "assets", apiKey);
        var multipart = new MultipartFormDataContent();
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var media = new StreamContent(stream);
        if (!string.IsNullOrWhiteSpace(source.OriginalMimeType))
        {
            media.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(source.OriginalMimeType);
        }

        multipart.Add(media, "assetData", SafeFileName(source.OriginalFileName));
        multipart.Add(new StringContent(deviceId), "deviceId");
        multipart.Add(new StringContent(deviceAssetId), "deviceAssetId");
        multipart.Add(new StringContent(source.FileCreatedAt), "fileCreatedAt");
        multipart.Add(new StringContent(source.FileModifiedAt), "fileModifiedAt");
        multipart.Add(new StringContent(SafeFileName(source.OriginalFileName)), "filename");
        multipart.Add(new StringContent(source.IsFavorite ? "true" : "false"), "isFavorite");
        multipart.Add(new StringContent(source.Visibility), "visibility");
        if (source.Duration.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            var duration = source.Duration.ValueKind == JsonValueKind.String
                ? source.Duration.GetString()
                : source.Duration.GetRawText();
            if (!string.IsNullOrWhiteSpace(duration))
            {
                multipart.Add(new StringContent(duration), "duration");
            }
        }

        request.Content = multipart;
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, $"upload asset {source.Id}", cancellationToken);
        return await ReadJsonAsync<UploadResponse>(response, cancellationToken);
    }

    public async Task<BulkUploadCheckResult> CheckBulkUploadAsync(
        string requestId,
        string checksum,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var body = new BulkUploadCheckRequest
        {
            Assets =
            [
                new BulkUploadCheckItem
                {
                    Id = requestId,
                    Checksum = checksum,
                },
            ],
        };
        using var response = await SendJsonAsync(
            HttpMethod.Post,
            "assets/bulk-upload-check",
            apiKey,
            body,
            cancellationToken);
        await EnsureSuccessAsync(response, $"check upload checksum for {requestId}", cancellationToken);
        var result = await ReadJsonAsync<BulkUploadCheckResponse>(response, cancellationToken);
        if (result.Results.Count != 1 ||
            !result.Results[0].Id.Equals(requestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Immich returned an invalid bulk-upload-check response.");
        }

        return result.Results[0];
    }

    public async Task UpdateAssetMetadataAsync(
        string targetId,
        Asset source,
        IReadOnlyList<AssetMetadata> metadata,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var exif = source.ExifInfo;
        var update = new Dictionary<string, object?>
        {
            ["isFavorite"] = source.IsFavorite,
            ["visibility"] = source.Visibility,
            ["rating"] = exif?.Rating,
        };
        AddIfNotNull(update, "dateTimeOriginal", exif?.DateTimeOriginal);
        AddIfNotNull(update, "description", exif?.Description);
        AddIfNotNull(update, "latitude", exif?.Latitude);
        AddIfNotNull(update, "longitude", exif?.Longitude);

        await SendJsonNoContentAsync(HttpMethod.Put, $"assets/{Escape(targetId)}", apiKey, update, cancellationToken);

        if (!string.IsNullOrWhiteSpace(exif?.TimeZone))
        {
            await SendJsonNoContentAsync(HttpMethod.Put, "assets", apiKey, new
            {
                ids = new[] { targetId },
                timeZone = exif.TimeZone,
            }, cancellationToken);
        }

        if (metadata.Count > 0)
        {
            await SendJsonNoContentAsync(
                HttpMethod.Put,
                $"assets/{Escape(targetId)}/metadata",
                apiKey,
                new MetadataUpsertRequest { Items = metadata },
                cancellationToken);
        }
    }

    public async Task RemoveFromAlbumAsync(
        string albumId,
        string assetId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var response = await SendJsonAsync(
            HttpMethod.Delete,
            $"albums/{Escape(albumId)}/assets",
            apiKey,
            new IdsRequest { Ids = new[] { assetId } },
            cancellationToken);
        await EnsureSuccessAsync(response, $"remove asset {assetId} from album {albumId}", cancellationToken);
    }

    public Task TrashAssetAsync(string assetId, string apiKey, CancellationToken cancellationToken) =>
        SendJsonNoContentAsync(
            HttpMethod.Delete,
            "assets",
            apiKey,
            new DeleteAssetsRequest { Ids = new[] { assetId }, Force = false },
            cancellationToken);

    private async Task<T> GetJsonAsync<T>(HttpMethod method, string path, string apiKey, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, apiKey, cancellationToken);
        await EnsureSuccessAsync(response, $"{method} {path}", cancellationToken);
        return await ReadJsonAsync<T>(response, cancellationToken);
    }

    private async Task<AssetSearchResponse> SearchAssetsAsync(
        string albumId,
        string? assetId,
        int page,
        int size,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object>
        {
            ["albumIds"] = new[] { albumId },
            ["page"] = page,
            ["size"] = size,
        };
        if (assetId is not null)
        {
            request["id"] = assetId;
        }

        using var response = await SendJsonAsync(
            HttpMethod.Post,
            "search/metadata",
            apiKey,
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, $"search assets in album {albumId}", cancellationToken);
        return await ReadJsonAsync<AssetSearchResponse>(response, cancellationToken);
    }

    private async Task SendJsonNoContentAsync(
        HttpMethod method,
        string path,
        string apiKey,
        object body,
        CancellationToken cancellationToken)
    {
        using var response = await SendJsonAsync(method, path, apiKey, body, cancellationToken);
        await EnsureSuccessAsync(response, $"{method} {path}", cancellationToken);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string apiKey,
        CancellationToken cancellationToken) =>
        SendWithRetryAsync(
            _ => Task.FromResult(CreateRequest(method, path, apiKey)),
            response => Task.FromResult(response),
            cancellationToken,
            disposeSuccessfulResponse: false);

    private Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method,
        string path,
        string apiKey,
        object body,
        CancellationToken cancellationToken) =>
        SendWithRetryAsync(_ =>
        {
            var request = CreateRequest(method, path, apiKey);
            request.Content = JsonContent.Create(body, options: JsonOptions);
            return Task.FromResult(request);
        }, response => Task.FromResult(response), cancellationToken, disposeSuccessfulResponse: false);

    private async Task<T> SendWithRetryAsync<T>(
        Func<CancellationToken, Task<HttpRequestMessage>> requestFactory,
        Func<HttpResponseMessage, Task<T>> responseHandler,
        CancellationToken cancellationToken,
        bool disposeSuccessfulResponse = true)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var request = await requestFactory(cancellationToken);
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (IsTransient(response.StatusCode) && attempt < 4)
                {
                    await DelayForRetryAsync(response, attempt, cancellationToken);
                    response.Dispose();
                    continue;
                }

                try
                {
                    return await responseHandler(response);
                }
                finally
                {
                    if (disposeSuccessfulResponse)
                    {
                        response.Dispose();
                    }
                }
            }
            catch (Exception exception) when (IsRetryableException(exception, cancellationToken) && attempt < 4)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
            }
        }

        throw new HttpRequestException("Immich request failed after four attempts.", lastException);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string apiKey)
    {
        var request = new HttpRequestMessage(method, new Uri(_apiBaseUri, path));
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
        ?? throw new InvalidDataException("Immich returned an empty JSON response.");

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 2_000)
        {
            body = body[..2_000];
        }

        throw new ImmichApiException(response.StatusCode, $"Failed to {operation}: HTTP {(int)response.StatusCode} {body}");
    }

    private static async Task DelayForRetryAsync(HttpResponseMessage response, int attempt, CancellationToken cancellationToken)
    {
        var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        await Task.Delay(delay > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay, cancellationToken);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static bool IsRetryableException(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is HttpRequestException or IOException;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string SafeFileName(string value)
    {
        var result = Path.GetFileName(value.Replace('\\', '/')).Replace('\r', '_').Replace('\n', '_').Replace('"', '_');
        return string.IsNullOrWhiteSpace(result) ? "asset" : result;
    }

    private static void AddIfNotNull(IDictionary<string, object?> values, string key, object? value)
    {
        if (value is not null)
        {
            values[key] = value;
        }
    }
}
