using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ImmichFamilyMerger.Tests;

internal sealed class FakeImmichServer : HttpMessageHandler
{
    public const string AppKey = "app-key";
    public const string SourceKey = "source-key";
    public const string SourceUserId = "11111111-1111-4111-8111-111111111111";
    public const string AppUserId = "22222222-2222-4222-8222-222222222222";
    public const string AlbumId = "33333333-3333-4333-8333-333333333333";

    private const string SourceAssetId = "source";
    private const string DestinationAssetId = "destination";
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("the exact original photo bytes");
    private static readonly string Checksum = Convert.ToBase64String(SHA1.HashData(Original));

    public List<RequestLog> Requests { get; } = [];
    public bool DestinationInAlbum { get; set; }
    public bool SourceTrashed { get; set; }
    public bool CorruptDestinationDownload { get; init; }
    public bool IsLivePhoto { get; init; }
    public bool ChangeSourceAfterUpload { get; init; }
    public bool MismatchDestinationMetadata { get; init; }
    public bool NormalizeDestinationTimeZone { get; init; }
    public string UploadStatus { get; init; } = "created";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RequestLog(request.Method.Method, path, body));
        var key = request.Headers.GetValues("x-api-key").Single();

        if (request.Method == HttpMethod.Get && path == "/api/users/me")
        {
            return Json(new { id = key == AppKey ? AppUserId : SourceUserId, email = "test@example.com" });
        }

        if (request.Method == HttpMethod.Get && path == $"/api/albums/{AlbumId}")
        {
            return Json(new
            {
                id = AlbumId,
                albumName = "Family inbox",
                assetCount = DestinationInAlbum ? 2 : 1,
                albumUsers = Array.Empty<object>(),
            });
        }

        if (request.Method == HttpMethod.Post && path == "/api/search/metadata")
        {
            return SearchAssets(body);
        }

        if (request.Method == HttpMethod.Get && path == $"/api/assets/{SourceAssetId}")
        {
            return Json(Asset(SourceAssetId, SourceUserId, "source-device", SourceTrashed, IsLivePhoto));
        }

        if (request.Method == HttpMethod.Get && path == $"/api/assets/{DestinationAssetId}")
        {
            return Json(Asset(DestinationAssetId, AppUserId, DeviceAssetId));
        }

        if (request.Method == HttpMethod.Get && path.EndsWith("/metadata", StringComparison.Ordinal))
        {
            return Json(new[]
            {
                new { key = "family-merger-test", value = new { answer = 42 }, updatedAt = "2026-01-01T00:00:00Z" },
            });
        }

        if (request.Method == HttpMethod.Get && path == $"/api/assets/{SourceAssetId}/original")
        {
            return Bytes(Original);
        }

        if (request.Method == HttpMethod.Get && path == $"/api/assets/{DestinationAssetId}/original")
        {
            return Bytes(CorruptDestinationDownload ? Encoding.UTF8.GetBytes("corrupt") : Original);
        }

        if (request.Method == HttpMethod.Post && path == "/api/assets")
        {
            return Json(new { id = DestinationAssetId, status = UploadStatus }, HttpStatusCode.Created);
        }

        if (request.Method == HttpMethod.Put && path == $"/api/albums/{AlbumId}/assets")
        {
            DestinationInAlbum = true;
            return Json(new[] { new { id = DestinationAssetId, success = true } });
        }

        if (request.Method == HttpMethod.Delete && path == "/api/assets")
        {
            Assert.Contains("\"force\":false", body, StringComparison.OrdinalIgnoreCase);
            SourceTrashed = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (request.Method == HttpMethod.Put &&
            (path == $"/api/assets/{DestinationAssetId}" ||
             path == $"/api/assets/{DestinationAssetId}/metadata" ||
             path == "/api/assets"))
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        throw new InvalidOperationException($"Unexpected request: {request.Method} {path} (key {key})");
    }

    private string DeviceAssetId => $"ifm:{SourceUserId}:{SourceAssetId}";

    private HttpResponseMessage SearchAssets(string body)
    {
        var assets = new List<object> { Asset(SourceAssetId, SourceUserId, "source-device") };
        if (DestinationInAlbum)
        {
            assets.Add(Asset(DestinationAssetId, AppUserId, DeviceAssetId));
        }

        using var search = JsonDocument.Parse(body);
        if (search.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            assets = assets.Where(asset =>
                JsonSerializer.SerializeToElement(asset).GetProperty("id").GetString() == id.GetString()).ToList();
        }

        return Json(new
        {
            albums = new { total = 0, count = 0, items = Array.Empty<object>(), nextPage = (string?)null },
            assets = new { total = assets.Count, count = assets.Count, items = assets, nextPage = (string?)null },
        });
    }

    private object Asset(string id, string ownerId, string deviceAssetId, bool trashed = false, bool live = false) => new
    {
        id,
        ownerId,
        deviceAssetId,
        deviceId = "device",
        type = "IMAGE",
        originalFileName = "family.jpg",
        originalMimeType = "image/jpeg",
        fileCreatedAt = "2024-01-02T03:04:05Z",
        fileModifiedAt = "2024-01-02T03:04:06Z",
        updatedAt = id == SourceAssetId && DestinationInAlbum && ChangeSourceAfterUpload
            ? "2026-01-01T00:00:01Z"
            : "2026-01-01T00:00:00Z",
        isFavorite = id != DestinationAssetId || !MismatchDestinationMetadata,
        isEdited = false,
        isTrashed = trashed,
        visibility = "timeline",
        duration = (long?)null,
        exifInfo = new
        {
            fileSizeInByte = Original.LongLength,
            dateTimeOriginal = "2024-01-02T03:04:05Z",
            timeZone = id == DestinationAssetId && NormalizeDestinationTimeZone ? "Europe/Vienna" : "UTC+1",
            latitude = 48.1,
            longitude = 11.5,
            description = "Family photo",
            rating = 5,
        },
        livePhotoVideoId = live ? "44444444-4444-4444-8444-444444444444" : null,
        stack = (object?)null,
        checksum = Checksum,
        hasMetadata = true,
    };

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(value),
    };
}
