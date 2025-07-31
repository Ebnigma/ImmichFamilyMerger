using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImmichFamilyMerger
{
    internal class ExifInfo
    {
        public string? Make { get; set; }
        public string? Model { get; set; }
        public int? ExifImageWidth { get; set; }
        public int? ExifImageHeight { get; set; }
        public long? FileSizeInByte { get; set; }
        public string? Orientation { get; set; }
        public DateTime? DateTimeOriginal { get; set; }
        public DateTime? ModifyDate { get; set; }
        public string? TimeZone { get; set; }
        public string? LensModel { get; set; }
        public double? FNumber { get; set; }
        public double? FocalLength { get; set; }
        public int? Iso { get; set; }
        public string? ExposureTime { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? Country { get; set; }
        public string? Description { get; set; }
        public string? ProjectionType { get; set; }
        public int? Rating { get; set; }
    }
}
