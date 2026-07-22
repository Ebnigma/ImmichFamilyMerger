using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ImmichFamilyMerger;

internal sealed class AlbumAssetMigrator
{
    private readonly ImmichApiClient _api;
    private readonly MigrationStateStore _state;
    private readonly AppConfig _config;

    public AlbumAssetMigrator(ImmichApiClient api, MigrationStateStore state, AppConfig config)
    {
        _api = api;
        _state = state;
        _config = config;
    }

    public async Task MigrateAlbumAssetsAsync(CancellationToken cancellationToken)
    {
        var destinationUser = await _api.GetMeAsync(_config.AppApiKey, cancellationToken);
        await ValidateSourceKeysAsync(destinationUser.Id, cancellationToken);

        var album = await _api.GetAlbumAsync(_config.AlbumId, _config.AppApiKey, cancellationToken);
        var albumAssets = await _api.GetAlbumAssetsAsync(_config.AlbumId, _config.AppApiKey, cancellationToken);
        Console.WriteLine($"Scanning album '{album.AlbumName}' ({albumAssets.Count} assets).");

        foreach (var record in _state.IncompleteRecords)
        {
            await ProcessSafelyAsync(record, destinationUser.Id, cancellationToken);
        }

        var skipped = 0;
        foreach (var albumAsset in albumAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (albumAsset.OwnerId.Equals(destinationUser.Id, StringComparison.OrdinalIgnoreCase) || _state.Contains(albumAsset.Id))
            {
                continue;
            }

            if (!_config.UserApiKeys.TryGetValue(albumAsset.OwnerId, out var sourceApiKey))
            {
                Console.WriteLine($"SKIP {albumAsset.Id}: no API key configured for owner {albumAsset.OwnerId}.");
                skipped++;
                continue;
            }

            try
            {
                var source = await _api.GetAssetAsync(albumAsset.Id, sourceApiKey, cancellationToken);
                var unsupportedReason = GetUnsupportedReason(source);
                if (unsupportedReason is not null)
                {
                    Console.WriteLine($"SKIP {source.Id} ({source.OriginalFileName}): {unsupportedReason}. Source remains untouched.");
                    skipped++;
                    continue;
                }

                var metadata = await _api.GetMetadataAsync(source.Id, sourceApiKey, cancellationToken);
                var deviceAssetId = $"ifm:{source.OwnerId}:{source.Id}";
                var record = await _state.AddAsync(source, metadata, deviceAssetId, cancellationToken);
                await ProcessSafelyAsync(record, destinationUser.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"FAILED to prepare {albumAsset.Id}: {exception.Message}. Source remains untouched.");
            }
        }

        var outstanding = _state.IncompleteRecords.Count();
        Console.WriteLine($"Cycle finished. Outstanding migrations: {outstanding}; safely skipped assets: {skipped}.");
    }

    private async Task ValidateSourceKeysAsync(string destinationUserId, CancellationToken cancellationToken)
    {
        foreach (var (configuredUserId, apiKey) in _config.UserApiKeys)
        {
            var user = await _api.GetMeAsync(apiKey, cancellationToken);
            if (!user.Id.Equals(configuredUserId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A source API key is mapped to user {configuredUserId}, but Immich reports that it belongs to {user.Id}.");
            }

            if (user.Id.Equals(destinationUserId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A source API key points to the family destination account.");
            }
        }
    }

    private async Task ProcessSafelyAsync(
        MigrationRecord record,
        string destinationUserId,
        CancellationToken cancellationToken)
    {
        if (!_config.UserApiKeys.TryGetValue(record.SourceOwnerId, out var sourceApiKey))
        {
            Console.Error.WriteLine($"PAUSED {record.SourceAssetId}: its source API key is no longer configured.");
            return;
        }

        try
        {
            await ProcessAsync(record, sourceApiKey, destinationUserId, cancellationToken);
            record.LastError = null;
            await _state.SaveAsync(record, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            record.LastError = exception.Message;
            await _state.SaveAsync(record, cancellationToken);
            Console.Error.WriteLine(
                $"PAUSED {record.SourceAssetId} at {record.Phase}: {exception.Message}. The source was not deleted by this attempt.");
        }
    }

    private async Task ProcessAsync(
        MigrationRecord record,
        string sourceApiKey,
        string destinationUserId,
        CancellationToken cancellationToken)
    {
        var mediaPath = GetMediaPath(record.SourceAssetId, ".original");
        if (record.Phase == MigrationPhase.Downloaded && !File.Exists(mediaPath))
        {
            record.Phase = MigrationPhase.Discovered;
            await _state.SaveAsync(record, cancellationToken);
        }

        if (record.Phase < MigrationPhase.Downloaded)
        {
            Console.WriteLine($"Downloading {record.SourceAssetId} ({record.Source.OriginalFileName}).");
            var digest = await _api.DownloadOriginalAsync(record.SourceAssetId, sourceApiKey, mediaPath, cancellationToken);
            RequireMatchingSourceDigest(record.Source, digest);
            record.VerifiedChecksum = digest.Sha1Base64;
            record.VerifiedLength = digest.Length;
            record.Phase = MigrationPhase.Downloaded;
            await _state.SaveAsync(record, cancellationToken);
        }

        if (record.Phase < MigrationPhase.Uploaded)
        {
            var localDigest = await ComputeFileDigestAsync(mediaPath, cancellationToken);
            RequireMatchingJournalDigest(record, localDigest, "local spool file");
            Console.WriteLine($"Uploading {record.SourceAssetId} to the family account.");
            var upload = await _api.UploadAsync(
                record.Source,
                mediaPath,
                _config.AppDeviceId,
                record.DeviceAssetId,
                _config.AppApiKey,
                cancellationToken);
            var destination = await _api.GetAssetAsync(upload.Id, _config.AppApiKey, cancellationToken);
            RequireDestinationIdentity(record, destination, destinationUserId);
            if (upload.Status.Equals("duplicate", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    RequireMatchingUserMetadata(record.Source, destination);
                    await RequireMatchingCustomMetadataAsync(record, destination.Id, cancellationToken);
                }
                catch (InvalidDataException exception)
                {
                    throw new InvalidOperationException(
                        $"Immich matched the upload to pre-existing asset {destination.Id}, but its metadata differs. " +
                        "It will not be adopted or modified automatically.",
                        exception);
                }
            }

            record.DestinationAssetId = destination.Id;
            record.Phase = MigrationPhase.Uploaded;
            await _state.SaveAsync(record, cancellationToken);
        }

        var destinationId = record.DestinationAssetId
                            ?? throw new InvalidDataException("The migration journal has no destination asset ID.");

        if (record.Phase < MigrationPhase.MetadataApplied)
        {
            if (_config.MetadataSettleSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.MetadataSettleSeconds), cancellationToken);
            }

            await _api.UpdateAssetMetadataAsync(
                destinationId,
                record.Source,
                record.Metadata,
                _config.AppApiKey,
                cancellationToken);
            record.Phase = MigrationPhase.MetadataApplied;
            await _state.SaveAsync(record, cancellationToken);
        }

        if (record.Phase < MigrationPhase.AlbumAdded)
        {
            await _api.AddToAlbumAsync(_config.AlbumId, destinationId, _config.AppApiKey, cancellationToken);
            await RequireAlbumMembershipAsync(destinationId, cancellationToken);
            record.Phase = MigrationPhase.AlbumAdded;
            await _state.SaveAsync(record, cancellationToken);
        }

        if (record.Phase < MigrationPhase.Verified)
        {
            await VerifyDestinationAsync(record, destinationUserId, cancellationToken);
            record.Phase = MigrationPhase.Verified;
            await _state.SaveAsync(record, cancellationToken);
        }

        if (!_config.TrashOriginals)
        {
            DeleteSpoolFiles(record.SourceAssetId);
            Console.WriteLine($"VERIFIED {record.SourceAssetId} -> {destinationId}; original retained by configuration.");
            return;
        }

        if (record.Phase < MigrationPhase.SourceTrashed)
        {
            // Never trust an old journal assertion at the destructive boundary: verify the destination again now.
            await VerifyDestinationAsync(record, destinationUserId, cancellationToken);
            var alreadyTrashed = await RequireSourceUnchangedOrTrashedAsync(record, sourceApiKey, cancellationToken);
            if (!alreadyTrashed)
            {
                await _api.TrashAssetAsync(record.SourceAssetId, sourceApiKey, cancellationToken);
                await RequireSourceTrashedAsync(record.SourceAssetId, sourceApiKey, cancellationToken);
            }

            record.Phase = MigrationPhase.SourceTrashed;
            await _state.SaveAsync(record, cancellationToken);
        }

        DeleteSpoolFiles(record.SourceAssetId);
        record.Phase = MigrationPhase.Complete;
        await _state.SaveAsync(record, cancellationToken);
        Console.WriteLine($"MOVED {record.SourceAssetId} -> {destinationId}; original is recoverable in Immich trash.");
    }

    private async Task VerifyDestinationAsync(
        MigrationRecord record,
        string destinationUserId,
        CancellationToken cancellationToken)
    {
        var destinationId = record.DestinationAssetId!;
        var destination = await _api.GetAssetAsync(destinationId, _config.AppApiKey, cancellationToken);
        RequireDestinationIdentity(record, destination, destinationUserId);
        if (destination.IsTrashed)
        {
            throw new InvalidOperationException($"Destination asset {destinationId} is in the trash.");
        }

        RequireMatchingUserMetadata(record.Source, destination);

        var verificationPath = GetMediaPath(record.SourceAssetId, ".verification");
        var digest = await _api.DownloadOriginalAsync(destinationId, _config.AppApiKey, verificationPath, cancellationToken);
        RequireMatchingJournalDigest(record, digest, "destination re-download");
        File.Delete(verificationPath);

        await RequireMatchingCustomMetadataAsync(record, destinationId, cancellationToken);
        await RequireAlbumMembershipAsync(destinationId, cancellationToken);
    }

    private async Task RequireMatchingCustomMetadataAsync(
        MigrationRecord record,
        string destinationId,
        CancellationToken cancellationToken)
    {
        var destinationMetadata = await _api.GetMetadataAsync(destinationId, _config.AppApiKey, cancellationToken);
        foreach (var sourceItem in record.Metadata)
        {
            var targetItem = destinationMetadata.FirstOrDefault(item => item.Key.Equals(sourceItem.Key, StringComparison.Ordinal));
            if (targetItem is null || !JsonNode.DeepEquals(
                    JsonNode.Parse(sourceItem.Value.GetRawText()),
                    JsonNode.Parse(targetItem.Value.GetRawText())))
            {
                throw new InvalidDataException($"Destination custom metadata key '{sourceItem.Key}' did not verify.");
            }
        }
    }

    private async Task RequireAlbumMembershipAsync(string destinationId, CancellationToken cancellationToken)
    {
        if (!await _api.IsAssetInAlbumAsync(
                _config.AlbumId,
                destinationId,
                _config.AppApiKey,
                cancellationToken))
        {
            throw new InvalidOperationException($"Destination asset {destinationId} is not present in the family album.");
        }
    }

    private async Task RequireSourceTrashedAsync(string sourceId, string sourceApiKey, CancellationToken cancellationToken)
    {
        try
        {
            var source = await _api.GetAssetAsync(sourceId, sourceApiKey, cancellationToken);
            if (!source.IsTrashed)
            {
                throw new InvalidOperationException($"Immich accepted the trash request, but source asset {sourceId} is not trashed.");
            }
        }
        catch (ImmichApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Some Immich versions hide trashed assets from this endpoint. A missing source is an acceptable postcondition
            // only because the destination was byte-verified immediately before the trash request.
        }
    }

    private async Task<bool> RequireSourceUnchangedOrTrashedAsync(
        MigrationRecord record,
        string sourceApiKey,
        CancellationToken cancellationToken)
    {
        Asset current;
        try
        {
            current = await _api.GetAssetAsync(record.SourceAssetId, sourceApiKey, cancellationToken);
        }
        catch (ImmichApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Covers a crash after Immich accepted the trash call but before the journal was advanced.
            return true;
        }

        if (current.IsTrashed)
        {
            return true;
        }

        if (!ChecksumsEqual(record.Source.Checksum, current.Checksum) ||
            !string.Equals(record.Source.OriginalFileName, current.OriginalFileName, StringComparison.Ordinal) ||
            !string.Equals(record.Source.FileCreatedAt, current.FileCreatedAt, StringComparison.Ordinal) ||
            !string.Equals(record.Source.FileModifiedAt, current.FileModifiedAt, StringComparison.Ordinal) ||
            !string.Equals(record.Source.UpdatedAt, current.UpdatedAt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The source asset changed during migration; it will not be trashed.");
        }

        RequireMatchingUserMetadata(record.Source, current);
        var currentMetadata = await _api.GetMetadataAsync(record.SourceAssetId, sourceApiKey, cancellationToken);
        foreach (var sourceItem in record.Metadata)
        {
            var currentItem = currentMetadata.FirstOrDefault(item => item.Key.Equals(sourceItem.Key, StringComparison.Ordinal));
            if (currentItem is null || !JsonNode.DeepEquals(
                    JsonNode.Parse(sourceItem.Value.GetRawText()),
                    JsonNode.Parse(currentItem.Value.GetRawText())))
            {
                throw new InvalidOperationException("The source custom metadata changed during migration; it will not be trashed.");
            }
        }

        return false;
    }

    private static void RequireMatchingSourceDigest(Asset source, FileDigest digest)
    {
        if (!ChecksumsEqual(source.Checksum, digest.Sha1Base64))
        {
            throw new InvalidDataException(
                $"Downloaded checksum {digest.Sha1Base64} does not match Immich source checksum {source.Checksum}.");
        }

        if (source.ExifInfo?.FileSizeInByte is long expectedLength && expectedLength != digest.Length)
        {
            throw new InvalidDataException($"Downloaded length {digest.Length} does not match source length {expectedLength}.");
        }
    }

    private static void RequireMatchingJournalDigest(MigrationRecord record, FileDigest digest, string description)
    {
        if (record.VerifiedChecksum is null || !ChecksumsEqual(record.VerifiedChecksum, digest.Sha1Base64) ||
            record.VerifiedLength != digest.Length)
        {
            throw new InvalidDataException($"The {description} does not byte-match the verified source original.");
        }
    }

    private static void RequireDestinationIdentity(MigrationRecord record, Asset destination, string destinationUserId)
    {
        if (!destination.OwnerId.Equals(destinationUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Destination asset {destination.Id} is not owned by the family account.");
        }

        if (!ChecksumsEqual(record.VerifiedChecksum ?? record.Source.Checksum, destination.Checksum))
        {
            throw new InvalidDataException($"Destination asset {destination.Id} reports a different SHA-1 checksum.");
        }
    }

    private static void RequireMatchingUserMetadata(Asset source, Asset destination)
    {
        if (source.IsFavorite != destination.IsFavorite ||
            !source.Visibility.Equals(destination.Visibility, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Destination favorite or visibility state does not match the source.");
        }

        var expected = source.ExifInfo;
        if (expected is null)
        {
            return;
        }

        var actual = destination.ExifInfo
                     ?? throw new InvalidDataException("Destination EXIF metadata is not available for verification.");
        if (!EqualOptionalText(expected.Description, actual.Description) ||
            expected.Rating != actual.Rating ||
            !EqualOptionalNumber(expected.Latitude, actual.Latitude) ||
            !EqualOptionalNumber(expected.Longitude, actual.Longitude) ||
            !EqualOptionalDate(expected.DateTimeOriginal, actual.DateTimeOriginal) ||
            !EqualOptionalTimeZone(expected.TimeZone, actual.TimeZone, expected.DateTimeOriginal))
        {
            throw new InvalidDataException("Destination user-visible EXIF metadata does not match the source.");
        }
    }

    private static bool EqualOptionalText(string? expected, string? actual) =>
        expected is null || string.Equals(expected, actual, StringComparison.Ordinal);

    private static bool EqualOptionalNumber(double? expected, double? actual) =>
        expected is null || actual is not null && Math.Abs(expected.Value - actual.Value) < 0.0000001;

    private static bool EqualOptionalDate(string? expected, string? actual)
    {
        if (expected is null)
        {
            return true;
        }

        return DateTimeOffset.TryParse(expected, out var expectedDate) && DateTimeOffset.TryParse(actual, out var actualDate)
            ? expectedDate.EqualsExact(actualDate)
            : string.Equals(expected, actual, StringComparison.Ordinal);
    }

    private static bool EqualOptionalTimeZone(string? expected, string? actual, string? dateTimeOriginal)
    {
        if (expected is null || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (actual is null || !DateTimeOffset.TryParse(
                dateTimeOriginal,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var capturedAt))
        {
            return false;
        }

        return TryGetUtcOffset(expected, capturedAt, out var expectedOffset) &&
               TryGetUtcOffset(actual, capturedAt, out var actualOffset) &&
               expectedOffset == actualOffset;
    }

    private static bool TryGetUtcOffset(string timeZone, DateTimeOffset capturedAt, out TimeSpan offset)
    {
        var fixedOffset = Regex.Match(
            timeZone,
            "^(?:UTC|GMT)(?<sign>[+-])(?<hours>\\d{1,2})(?::?(?<minutes>\\d{2}))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (fixedOffset.Success &&
            int.TryParse(fixedOffset.Groups["hours"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) &&
            hours <= 14)
        {
            var minutesText = fixedOffset.Groups["minutes"].Value;
            var minutes = minutesText.Length == 0
                ? 0
                : int.Parse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture);
            if (minutes < 60)
            {
                offset = new TimeSpan(hours, minutes, 0);
                if (fixedOffset.Groups["sign"].Value == "-")
                {
                    offset = -offset;
                }

                return true;
            }
        }

        try
        {
            offset = TimeZoneInfo.FindSystemTimeZoneById(timeZone).GetUtcOffset(capturedAt.UtcDateTime);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            offset = default;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            offset = default;
            return false;
        }
    }

    private static bool ChecksumsEqual(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(left), Convert.FromBase64String(right));
        }
        catch (FormatException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? GetUnsupportedReason(Asset source)
    {
        if (source.IsEdited)
        {
            return "it has an Immich edit history that cannot yet be transferred losslessly";
        }

        if (!string.IsNullOrWhiteSpace(source.LivePhotoVideoId))
        {
            return "it is a live photo and both components must be migrated atomically";
        }

        if (source.Stack is { } stack && stack.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return "it belongs to a stack whose relationship cannot yet be transferred idempotently";
        }

        return null;
    }

    private string GetMediaPath(string sourceAssetId, string suffix)
    {
        var safeName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceAssetId)));
        return Path.Combine(_state.MediaDirectory, safeName + suffix);
    }

    private void DeleteSpoolFiles(string sourceAssetId)
    {
        foreach (var suffix in new[] { ".original", ".original.part", ".verification", ".verification.part" })
        {
            var path = GetMediaPath(sourceAssetId, suffix);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<FileDigest> ComputeFileDigestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1024 * 1024];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        return new FileDigest(Convert.ToBase64String(hash.GetHashAndReset()), length);
    }
}
