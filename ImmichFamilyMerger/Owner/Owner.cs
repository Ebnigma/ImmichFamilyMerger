using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImmichFamilyMerger
{
    internal class Owner
    {
        public string Id { get; set; }
        public string Email { get; set; }
        public string Name { get; set; }
        public string ProfileImagePath { get; set; }
        public string AvatarColor { get; set; }
        public DateTime ProfileChangedAt { get; set; }
    }
}
