using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImmichFamilyMerger;
using Xunit;

namespace ImmichFamilyMerger.Tests;

public sealed class MigrationSafetyTests
{
    [Fact]
    public void CurrentAlbumResponseCanBeDeserialized()
    {
        const string response = """
            {
              "id": "33333333-3333-4333-8333-333333333333",
              "albumName": "Family inbox",
              "assetCount": 1,
              "albumUsers": []
            }
            """;

        var album = JsonSerializer.Deserialize<Album>(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(album);
        Assert.Equal("Family inbox", album.AlbumName);
        Assert.Equal(1, album.AssetCount);
    }

    [Fact]
    public async Task DiscoversAssetsAcrossAllSearchPages()
    {
        var server = new PagedAlbumSearchServer();
        using var client = new HttpClient(server);
        var api = new ImmichApiClient(client, new Uri("http://immich.test/api/"));

        var assets = await api.GetAlbumAssetsAsync(FakeImmichServer.AlbumId, FakeImmichServer.AppKey, default);

        Assert.Equal(new[] { "asset-1", "asset-2", "asset-3" }, assets.Select(asset => asset.Id));
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task TrashesSourceOnlyAfterDestinationBytesMetadataAndAlbumVerify()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer();

        await RunMigrationAsync(server, temporary.Path);

        var delete = Assert.Single(server.Requests.Where(request => request.Method == "DELETE"));
        Assert.Contains("\"force\":false", delete.Body, StringComparison.OrdinalIgnoreCase);
        Assert.True(server.DestinationInAlbum);
        Assert.True(server.SourceTrashed);
        Assert.DoesNotContain(server.Requests, request => request.Path == "/api/assets/copy");
        Assert.True(server.Requests.FindIndex(request => request.Method == "DELETE") >
                    server.Requests.FindIndex(request => request.Path.EndsWith("/assets/destination/original")));

        var state = await MigrationStateStore.OpenAsync(Path.Combine(temporary.Path, "state.json"), default);
        Assert.Empty(state.IncompleteRecords);
        Assert.Empty(Directory.EnumerateFiles(state.MediaDirectory));
    }

    [Fact]
    public async Task CorruptDestinationNeverTrashesSource()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer { CorruptDestinationDownload = true };

        await RunMigrationAsync(server, temporary.Path);

        Assert.DoesNotContain(server.Requests, request => request.Method == "DELETE");
        Assert.False(server.SourceTrashed);
        var state = await MigrationStateStore.OpenAsync(Path.Combine(temporary.Path, "state.json"), default);
        var record = Assert.Single(state.IncompleteRecords);
        Assert.Equal(MigrationPhase.AlbumAdded, record.Phase);
        Assert.Contains("does not byte-match", record.LastError);
    }

    [Fact]
    public async Task DuplicateWithDifferentMetadataIsNotAdoptedAndSourceIsUntouched()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer
        {
            UploadStatus = "duplicate",
            MismatchDestinationMetadata = true,
        };

        await RunMigrationAsync(server, temporary.Path);

        Assert.DoesNotContain(server.Requests, request => request.Method == "DELETE");
        Assert.DoesNotContain(server.Requests, request => request.Path.EndsWith("/assets/copy"));
        var state = await MigrationStateStore.OpenAsync(Path.Combine(temporary.Path, "state.json"), default);
        Assert.Equal(MigrationPhase.Downloaded, Assert.Single(state.IncompleteRecords).Phase);
    }

    [Fact]
    public async Task MatchingDuplicateIsAdoptedOnlyAfterFullVerification()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer
        {
            UploadStatus = "duplicate",
            NormalizeDestinationTimeZone = true,
        };

        await RunMigrationAsync(server, temporary.Path);

        Assert.True(server.DestinationInAlbum);
        Assert.True(server.SourceTrashed);
        Assert.Contains(server.Requests, request => request.Path.EndsWith("/assets/destination/original"));
        Assert.Empty((await MigrationStateStore.OpenAsync(
            Path.Combine(temporary.Path, "state.json"),
            default)).IncompleteRecords);
    }

    [Fact]
    public async Task LivePhotoIsSkippedWithoutUploadingOrDeleting()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer { IsLivePhoto = true };

        await RunMigrationAsync(server, temporary.Path);

        Assert.DoesNotContain(server.Requests, request => request.Method == "POST" && request.Path == "/api/assets");
        Assert.DoesNotContain(server.Requests, request => request.Method == "DELETE");
        var state = await MigrationStateStore.OpenAsync(Path.Combine(temporary.Path, "state.json"), default);
        Assert.Empty(state.IncompleteRecords);
    }

    [Fact]
    public async Task RestartAfterSuccessfulTrashCompletesWithoutASecondDelete()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer();
        await RunMigrationAsync(server, temporary.Path, trashOriginals: false);
        Assert.Equal(MigrationPhase.Verified, Assert.Single(
            (await MigrationStateStore.OpenAsync(Path.Combine(temporary.Path, "state.json"), default)).IncompleteRecords).Phase);

        server.SourceTrashed = true; // Simulate: DELETE succeeded, then the process stopped before saving the phase.
        await RunMigrationAsync(server, temporary.Path, trashOriginals: true);

        Assert.DoesNotContain(server.Requests, request => request.Method == "DELETE");
        Assert.Empty((await MigrationStateStore.OpenAsync(Path.Combine(temporary.Path, "state.json"), default)).IncompleteRecords);
    }

    [Fact]
    public async Task SourceChangedDuringMigrationIsNotTrashed()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer { ChangeSourceAfterUpload = true };

        await RunMigrationAsync(server, temporary.Path);

        Assert.DoesNotContain(server.Requests, request => request.Method == "DELETE");
        var state = await MigrationStateStore.OpenAsync(Path.Combine(temporary.Path, "state.json"), default);
        var record = Assert.Single(state.IncompleteRecords);
        Assert.Equal(MigrationPhase.Verified, record.Phase);
        Assert.Contains("changed during migration", record.LastError);
    }

    private static async Task RunMigrationAsync(FakeImmichServer server, string stateDirectory, bool trashOriginals = true)
    {
        using var client = new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan };
        var config = new AppConfig
        {
            AppApiKey = FakeImmichServer.AppKey,
            UserApiKeys = new Dictionary<string, string> { [FakeImmichServer.SourceUserId] = FakeImmichServer.SourceKey },
            ApiBaseUri = new Uri("http://immich.test/api/"),
            AlbumId = FakeImmichServer.AlbumId,
            StatePath = Path.Combine(stateDirectory, "state.json"),
            MetadataSettleSeconds = 0,
            TrashOriginals = trashOriginals,
        };
        var state = await MigrationStateStore.OpenAsync(config.StatePath, default);
        var migrator = new AlbumAssetMigrator(new ImmichApiClient(client, config.ApiBaseUri), state, config);
        await migrator.MigrateAlbumAssetsAsync(default);
    }

    private sealed class FakeImmichServer : HttpMessageHandler
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
                (path == $"/api/assets/{DestinationAssetId}" || path == $"/api/assets/{DestinationAssetId}/metadata" ||
                 path == "/api/assets"))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path} (key {key})");
        }

        private string DeviceAssetId => $"ifm:{SourceUserId}:{SourceAssetId}";

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

    private sealed class PagedAlbumSearchServer : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/search/metadata", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var page = body.RootElement.GetProperty("page").GetInt32();
            RequestCount++;

            var items = page switch
            {
                1 => new[]
                {
                    new { id = "asset-1", ownerId = "owner" },
                    new { id = "asset-2", ownerId = "owner" },
                },
                2 => new[] { new { id = "asset-3", ownerId = "owner" } },
                _ => throw new InvalidOperationException($"Unexpected page {page}."),
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    assets = new
                    {
                        total = 3,
                        count = items.Length,
                        items,
                        nextPage = page == 1 ? "2" : null,
                    },
                }), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RequestLog(string Method, string Path, string Body);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ifm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
