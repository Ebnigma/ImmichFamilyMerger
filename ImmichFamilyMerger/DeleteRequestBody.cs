using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImmichFamilyMerger
{
    internal class DeleteRequestBody
    {
        bool Force = true;
        public List<string> Ids { get; set; } = new List<string>();
    }
}
