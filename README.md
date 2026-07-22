# Immich Family Merger

[![Test and publish container](https://github.com/Ebnigma/ImmichFamilyMerger/actions/workflows/container.yml/badge.svg)](https://github.com/Ebnigma/ImmichFamilyMerger/actions/workflows/container.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

Bring family photos and videos together under one Immich account without giving up the convenience of individual user accounts.

Immich Family Merger watches a shared album. When a source user adds a supported asset, the service creates a byte-identical copy owned by the family account, restores supported metadata, verifies the result, and only then moves the source asset to Immich's recoverable trash.

## Why use it?

Immich sharing makes it easy for several people to contribute to an album, but contributed assets remain owned by their original accounts. That can make a shared family library dependent on every contributor account remaining available.

This service turns a shared album into a controlled migration queue:

- family members keep their own Immich accounts;
- the family account becomes the long-term owner of migrated assets;
- original media bytes, embedded EXIF/XMP, timestamps, location, descriptions, ratings, favorite state, visibility, and custom metadata are preserved where the Immich API supports them;
- a persistent journal makes interrupted migrations resumable;
- no source is trashed unless the destination copy passes verification.

## Before you start

Immich Family Merger intentionally favors safety over coverage. It currently skips live photos, edited assets, and stacked assets because their relationships cannot yet be migrated atomically and idempotently. Skipped sources are left untouched and the reason is logged.

External `.xmp` sidecar files cannot currently be transferred safely between owners through the Immich API. Embedded EXIF/XMP remains part of the byte-identical original. If separate sidecars are important to your library, keep `TRASH_ORIGINALS=false`.

Start every new installation in verification-only mode. Review the copied assets before enabling source trashing.

## Quick start

### 1. Prepare Immich

1. Sign in as the family account and create the album that will act as the migration queue.
2. Share the album with each source user and allow them to contribute assets.
3. Create an API key for the family account with these permissions:
   `user.read`, `album.read`, `albumAsset.create`, `asset.upload`, `asset.read`, `asset.download`, and `asset.update`.
4. Create an API key for each source account with these permissions:
   `user.read`, `asset.read`, `asset.download`, and `asset.delete`.
5. Copy the album UUID and each source user's UUID from Immich.

The family account must be able to see contributed assets in the shared album. At startup, the service validates every source UUID/API-key pair against Immich and stops on a mismatch.

### 2. Configure the service

Clone the repository and create a local environment file:

```bash
git clone https://github.com/Ebnigma/ImmichFamilyMerger.git
cd ImmichFamilyMerger
cp .env.example .env
```

Edit `.env`:

```dotenv
BASE_URL=https://photos.example.com
ALBUM_ID=00000000-0000-4000-8000-000000000000
APP_API_KEY=family-account-api-key
USER_API_KEYS=source-user-uuid:source-api-key,second-user-uuid:second-source-api-key
TRASH_ORIGINALS=false
```

Never commit `.env` or paste API keys into an issue. The file is ignored by Git, but a container-platform secret store is preferable for production deployments.

### 3. Run a verification-only migration

```bash
docker compose pull
docker compose up -d
docker compose logs -f immich-family-merger
```

With `TRASH_ORIGINALS=false`, the service downloads, uploads, restores metadata, adds the destination to the album, and performs every verification step while leaving the source untouched.

Inspect the family-owned copies in Immich. When satisfied, set `TRASH_ORIGINALS=true` in `.env` and apply the change:

```bash
docker compose up -d
```

The service verifies each destination again immediately before moving its source to Immich's trash. It never performs a permanent delete.

## Configuration

| Variable | Required | Default | Description |
|---|---:|---:|---|
| `BASE_URL` | yes | — | Immich server URL, with or without a trailing `/api` |
| `ALBUM_ID` | yes | — | UUID of the shared migration album |
| `APP_API_KEY` | yes | — | API key belonging to the family destination account |
| `USER_API_KEYS` | yes | — | Comma-separated `user-uuid:api-key` mappings for source accounts |
| `TRASH_ORIGINALS` | no | `false` | Set `true` only after validating retained source assets |
| `SLEEP_TIME` | no | `300` with Compose | Seconds between scans; `0` runs one scan and exits |
| `METADATA_SETTLE_TIME` | no | `15` | Seconds to wait for Immich metadata extraction before applying copied values |
| `STATE_PATH` | no | `/data/state.json` | Path to the durable migration journal inside the container |
| `IMMICH_FAMILY_MERGER_IMAGE` | no | `ghcr.io/ebnigma/immichfamilymerger:latest` | Container image tag or digest used by Compose |

`USER_API_KEYS` supports multiple source accounts:

```dotenv
USER_API_KEYS=alice-user-uuid:alice-api-key,bob-user-uuid:bob-api-key
```

Run exactly one container per `STATE_PATH`. An exclusive lock prevents two processes from using the same journal at once.

## Safety model

For each supported asset, the service:

1. records the source asset and custom metadata in the durable journal;
2. streams the original to the persistent `/data` volume and verifies its SHA-1 checksum and reported size;
3. uploads it with a deterministic device identity so restarts remain idempotent;
4. applies supported user-visible and custom metadata;
5. adds the family-owned copy to the watched album;
6. downloads the destination original and verifies the bytes, ownership, album membership, and metadata;
7. re-reads the source and stops if it changed during the migration;
8. optionally moves the source to Immich's trash using `force: false`.

Journal updates are flushed to disk and atomically replaced after every completed phase. A restart resumes incomplete work. At the source-trash boundary, destination verification is always repeated instead of trusting an older journal result.

If Immich reports a duplicate during upload, the service adopts it only when ownership, bytes, and metadata match. An ambiguous or mismatched duplicate is left untouched for manual review.

No client using several HTTP requests can provide a database transaction across an entire migration. Avoid editing source assets while they are being processed; the final source comparison prevents trashing when a change is detected.

## Versioning and upgrades

Container images are published for `linux/amd64` and `linux/arm64`.

- `latest` follows the newest successful build on the default branch.
- A release such as `v1.2.3` publishes `1.2.3`, `1.2`, and `1` image tags.
- `sha-<commit>` identifies one exact build.

For predictable deployments, pin a full release version in `.env`:

```dotenv
IMMICH_FAMILY_MERGER_IMAGE=ghcr.io/ebnigma/immichfamilymerger:1.2.3
```

Upgrade without removing the state volume:

```bash
docker compose pull
docker compose up -d
```

The `merger-state` volume contains the migration journal and temporary originals needed for crash recovery. Do not delete it while migrations are incomplete.

## Operations and recovery

Follow the service logs:

```bash
docker compose logs -f immich-family-merger
```

Useful outcomes include:

- `MOVED`: the destination verified and the source is now in recoverable Immich trash;
- `VERIFIED`: the destination verified while the source was retained by configuration;
- `SKIP`: the asset is unsupported or its owner has no configured API key;
- `PAUSED` or `FAILED`: the migration stopped safely and the source was not deleted by that attempt.

If a migration pauses, fix the reported Immich access, configuration, storage, or metadata issue and restart the container. The journal resumes from its last durable phase. Do not edit `state.json` manually unless you have first stopped the service and made a backup of the entire state volume.

## Development

The application targets .NET 8. Production code is organized by responsibility under `Api`, `Configuration`, `Infrastructure`, and `Migration`; each source file contains one top-level type.

Run the safety suite and build the container locally:

```bash
dotnet test ImmichFamilyMerger.sln
docker build -f ImmichFamilyMerger/Dockerfile -t immich-family-merger .
```

The suite covers paginated album discovery, destination corruption, duplicate handling, unsupported live photos, source changes, trash ordering, and restart recovery.

This project is independent community software and is not affiliated with the Immich project. API behavior follows Immich's documented [search](https://api.immich.app/endpoints/search/searchAssets), [upload](https://api.immich.app/endpoints/assets/uploadAsset), [download](https://api.immich.app/endpoints/assets/downloadAsset), [album](https://api.immich.app/endpoints/albums/addAssetsToAlbum), and [delete](https://api.immich.app/endpoints/assets/deleteAssets) endpoints.

## License

Immich Family Merger is free software licensed under the [GNU General Public License version 3](LICENSE), without an option to use later GPL versions (GPL-3.0-only).
