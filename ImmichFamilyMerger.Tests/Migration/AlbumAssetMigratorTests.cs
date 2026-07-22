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
    public async Task RemovesQueueEntryAndTrashesSourceOnlyAfterDestinationVerification()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer();

        await RunMigrationAsync(server, temporary.Path);

        var delete = Assert.Single(
            server.Requests,
            request => request.Method == "DELETE" && request.Path == "/api/assets");
        Assert.Contains("\"force\":false", delete.Body, StringComparison.OrdinalIgnoreCase);
        Assert.False(server.SourceInAlbum);
        Assert.False(server.DestinationInAlbum);
        Assert.True(server.SourceTrashed);
        Assert.DoesNotContain(server.Requests, request => request.Path == "/api/assets/copy");
        Assert.DoesNotContain(
            server.Requests,
            request => request.Method == "PUT" &&
                       request.Path == $"/api/albums/{FakeImmichServer.AlbumId}/assets");
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
        Assert.True(server.SourceInAlbum);
        var state = await MigrationStateStore.OpenAsync(Path.Combine(temporary.Path, "state.json"), default);
        var record = Assert.Single(state.IncompleteRecords);
        Assert.Equal(MigrationPhase.MetadataApplied, record.Phase);
        Assert.Contains("does not byte-match", record.LastError);
    }

    [Fact]
    public async Task DuplicateWithDifferentMetadataIsNotAdoptedAndSourceIsUntouched()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer
        {
            DestinationCreated = true,
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
            DestinationCreated = true,
            NormalizeDestinationTimeZone = true,
        };

        await RunMigrationAsync(server, temporary.Path);

        Assert.False(server.SourceInAlbum);
        Assert.False(server.DestinationInAlbum);
        Assert.True(server.SourceTrashed);
        Assert.DoesNotContain(
            server.Requests,
            request => request.Method == "POST" && request.Path == "/api/assets");
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
        Assert.True(server.SourceInAlbum);
        Assert.False(server.DestinationInAlbum);

        server.SourceTrashed = true;
        server.SourceInAlbum = false;
        await RunMigrationAsync(server, temporary.Path, trashOriginals: true);

        Assert.DoesNotContain(
            server.Requests,
            request => request.Method == "DELETE" && request.Path == "/api/assets");
        Assert.False(server.SourceInAlbum);
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
        Assert.True(server.SourceInAlbum);
        var state = await MigrationStateStore.OpenAsync(Path.Combine(temporary.Path, "state.json"), default);
        var record = Assert.Single(state.IncompleteRecords);
        Assert.Equal(MigrationPhase.Verified, record.Phase);
        Assert.Contains("changed during migration", record.LastError);
    }

    [Fact]
    public async Task RemovesFamilyOwnedAssetsLeftInQueueByOlderReleases()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer
        {
            SourceInAlbum = false,
            DestinationInAlbum = true,
        };

        await RunMigrationAsync(server, temporary.Path);

        Assert.False(server.DestinationInAlbum);
        Assert.Contains(
            server.Requests,
            request => request.Method == "DELETE" && request.Path.EndsWith("/albums/" + FakeImmichServer.AlbumId + "/assets"));
        Assert.DoesNotContain(server.Requests, request => request.Method == "POST" && request.Path == "/api/assets");
        Assert.DoesNotContain(server.Requests, request => request.Path == "/api/assets" && request.Method == "DELETE");
    }

    [Fact]
    public async Task TrashFailureKeepsSourceQueuedAndRestartContinues()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer { TrashFailuresRemaining = 4 };

        await RunMigrationAsync(server, temporary.Path);

        Assert.True(server.SourceInAlbum);
        Assert.False(server.SourceTrashed);
        Assert.Equal(MigrationPhase.Verified, Assert.Single(
            (await MigrationStateStore.OpenAsync(Path.Combine(temporary.Path, "state.json"), default)).IncompleteRecords).Phase);

        await RunMigrationAsync(server, temporary.Path);

        Assert.False(server.SourceInAlbum);
        Assert.True(server.SourceTrashed);
        Assert.Empty((await MigrationStateStore.OpenAsync(
            Path.Combine(temporary.Path, "state.json"),
            default)).IncompleteRecords);
    }

    [Fact]
    public async Task AmbiguousUploadIsReconciledWithoutASecondMediaPost()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer { FailUploadAfterCreation = true };

        await RunMigrationAsync(server, temporary.Path);

        Assert.Single(
            server.Requests,
            request => request.Method == "POST" && request.Path == "/api/assets");
        Assert.True(server.SourceTrashed);
        Assert.Empty((await MigrationStateStore.OpenAsync(
            Path.Combine(temporary.Path, "state.json"),
            default)).IncompleteRecords);
    }

    [Fact]
    public async Task UnconfirmedUploadIsNeverPostedAgainAutomatically()
    {
        using var temporary = new TemporaryDirectory();
        var server = new FakeImmichServer { FailUploadWithoutCreation = true };

        await RunMigrationAsync(server, temporary.Path);
        await RunMigrationAsync(server, temporary.Path);

        Assert.Single(
            server.Requests,
            request => request.Method == "POST" && request.Path == "/api/assets");
        Assert.False(server.SourceTrashed);
        Assert.True(server.SourceInAlbum);
        var record = Assert.Single((await MigrationStateStore.OpenAsync(
            Path.Combine(temporary.Path, "state.json"),
            default)).IncompleteRecords);
        Assert.Equal(MigrationPhase.UploadAttempted, record.Phase);
        Assert.Contains("will not be repeated automatically", record.LastError);
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
