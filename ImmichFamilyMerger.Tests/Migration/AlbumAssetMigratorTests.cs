using System.Text.Json;
using ImmichFamilyMerger;
using Xunit;

namespace ImmichFamilyMerger.Tests;

public sealed class AlbumAssetMigratorTests
{
    [Fact]
    public void ConfigurationDefaultsToRetainingOriginals()
    {
        var config = new AppConfig
        {
            AppApiKey = "app-key",
            UserApiKeys = new Dictionary<string, string>(),
            ApiBaseUri = new Uri("http://immich.test/api/"),
            AlbumId = "album-id",
        };

        Assert.False(config.TrashOriginals);
    }

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

        var delete = Assert.Single(server.Requests, request => request.Method == "DELETE");
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

        server.SourceTrashed = true;
        await RunMigrationAsync(server, temporary.Path, trashOriginals: true);

        Assert.DoesNotContain(server.Requests, request => request.Method == "DELETE");
        Assert.Empty((await MigrationStateStore.OpenAsync(
            Path.Combine(temporary.Path, "state.json"),
            default)).IncompleteRecords);
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

    private static async Task RunMigrationAsync(
        FakeImmichServer server,
        string stateDirectory,
        bool trashOriginals = true)
    {
        using var client = new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan };
        var config = new AppConfig
        {
            AppApiKey = FakeImmichServer.AppKey,
            UserApiKeys = new Dictionary<string, string>
            {
                [FakeImmichServer.SourceUserId] = FakeImmichServer.SourceKey,
            },
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
}
