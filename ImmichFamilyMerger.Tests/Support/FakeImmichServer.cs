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
    public bool SourceInAlbum { get; set; } = true;
    public bool DestinationInAlbum { get; set; }
    public bool SourceTrashed { get; set; }
    public bool CorruptDestinationDownload { get; init; }
    public bool IsLivePhoto { get; init; }
    public bool ChangeSourceAfterUpload { get; init; }
    public bool MismatchDestinationMetadata { get; init; }
    public bool NormalizeDestinationTimeZone { get; init; }
    public bool FailUploadAfterCreation { get; init; }
    public bool FailUploadWithoutCreation { get; init; }
    public int TrashFailuresRemaining { get; set; }
    public string UploadStatus { get; init; } = "created";
    public bool DestinationCreated { get; set; }

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
                assetCount = Convert.ToInt32(SourceInAlbum) + Convert.ToInt32(DestinationInAlbum),
                albumUsers = Array.Empty<object>(),
            });
        }

        if (request.Method == HttpMethod.Post && path == "/api/search/metadata")
        {
            return SearchAssets(body);
        }

        if (request.Method == HttpMethod.Post && path == "/api/assets/bulk-upload-check")
        {
            using var check = JsonDocument.Parse(body);
            var item = check.RootElement.GetProperty("assets").EnumerateArray().Single();
            Assert.Equal(Checksum, item.GetProperty("checksum").GetString());
            var requestId = item.GetProperty("id").GetString();
            return Json(new
            {
                results = new[]
                {
                    new
                    {
                        id = requestId,
                        action = DestinationCreated ? "reject" : "accept",
                        assetId = DestinationCreated ? DestinationAssetId : null,
                        isTrashed = false,
                        reason = DestinationCreated ? "duplicate" : null,
                    },
                },
            });
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
            if (FailUploadWithoutCreation)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            DestinationCreated = true;
            if (FailUploadAfterCreation)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return Json(new { id = DestinationAssetId, status = UploadStatus }, HttpStatusCode.Created);
        }

        if (request.Method == HttpMethod.Delete && path == $"/api/albums/{AlbumId}/assets")
        {
            using var removal = JsonDocument.Parse(body);
            var ids = removal.RootElement.GetProperty("ids").EnumerateArray()
                .Select(id => id.GetString()!)
                .ToArray();
            foreach (var id in ids)
            {
                if (id == SourceAssetId)
                {
                    SourceInAlbum = false;
                }

                if (id == DestinationAssetId)
                {
                    DestinationInAlbum = false;
                }
            }

            return Json(ids.Select(id => new { id, success = true }).ToArray());
        }

        if (request.Method == HttpMethod.Delete && path == "/api/assets")
        {
            Assert.Contains("\"force\":false", body, StringComparison.OrdinalIgnoreCase);
            if (TrashFailuresRemaining > 0)
            {
                TrashFailuresRemaining--;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            SourceTrashed = true;
            SourceInAlbum = false;
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
        var assets = new List<object>();
        if (SourceInAlbum && !SourceTrashed)
        {
            assets.Add(Asset(SourceAssetId, SourceUserId, "source-device"));
        }

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
        updatedAt = id == SourceAssetId && DestinationCreated && ChangeSourceAfterUpload
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
