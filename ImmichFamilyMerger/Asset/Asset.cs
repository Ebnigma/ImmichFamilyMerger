using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImmichFamilyMerger
{
    internal class Asset
    {
        public string Id { get; set; }
        public string DeviceAssetId { get; set; }
        public string OwnerId { get; set; }
        public string DeviceId { get; set; }
        public string? LibraryId { get; set; }
        public string Type { get; set; }
        public string OriginalPath { get; set; }
        public string OriginalFileName { get; set; }
        public string OriginalMimeType { get; set; }
        public string Thumbhash { get; set; }
        public DateTime FileCreatedAt { get; set; }
        public DateTime FileModifiedAt { get; set; }
        public DateTime LocalDateTime { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsFavorite { get; set; }
        public bool IsArchived { get; set; }
        public bool IsTrashed { get; set; }
        public string Visibility { get; set; }
        public string Duration { get; set; }
        public ExifInfo? ExifInfo { get; set; }
        public string? LivePhotoVideoId { get; set; }
        public List<object> People { get; set; }
        public string Checksum { get; set; }
        public bool IsOffline { get; set; }
        public bool HasMetadata { get; set; }
        public string? DuplicateId { get; set; }
        public bool Resized { get; set; }
    }
}
