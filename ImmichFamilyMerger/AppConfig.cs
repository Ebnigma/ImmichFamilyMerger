using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImmichFamilyMerger
{
    internal class AppConfig
    {
        public string AppDeviceId { get; set; } = "ImmichFamilyMerger";
        public Dictionary<string, string> UserApiKeys { get; set; }
        public string AppApiKey { get; set; }
        public string BaseUrl { get; set; }
        public string AlbumId { get; set; }
        public int SleepAfterSeconds { get; set; }
    }
}
