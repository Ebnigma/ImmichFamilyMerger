using ImmichFamilyMerger;
using System.Runtime.InteropServices;

try
{
    var config = AppConfigParser.Parse(args);
    using var httpClient = new HttpClient
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    Directory.CreateDirectory(Path.GetDirectoryName(config.StatePath)!);
    using var instanceLock = InstanceLock.Acquire(config.StatePath);
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
