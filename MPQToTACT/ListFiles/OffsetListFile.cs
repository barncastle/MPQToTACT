using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MPQToTACT.Helpers;

namespace MPQToTACT.ListFiles
{
    /// <summary>
    /// This file lookup uses a base value as the minium filedata id.
    /// </summary>
    /// <remarks>
    /// The idea is to offset the offical Blizzard Ids with a large number so we can differentiate office from unoffical.<br/>
    /// FYI new filenames are allocated in alphabetical order by MPQReader to give some semblance of logic and conformity
    /// </remarks>
    class OffsetListFile : BaseListFileLookup
    {
        private uint CurrentId;

        public OffsetListFile(uint startId)
        {
            CurrentId = startId;
        }

        public override void Open()
        {
            base.Open();

            CurrentId = Math.Max(CurrentId, FileLookup.Values.Max());
            Log.WriteLine($"FileDataIds starting from {CurrentId}");
        }

        public override uint GetOrCreateFileId(string filename)
        {
            // check the filename exists
            if (!FileLookup.TryGetValue(filename, out var id))
            {
                id = ++CurrentId;
                FileLookup.Add(filename, id);
                return id;
            }

            return id;
        }
    }
}
