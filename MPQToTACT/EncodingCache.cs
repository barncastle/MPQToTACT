using System.IO;
using TACT.Net;
using TACT.Net.Cryptography;
using TACT.Net.Encoding;

namespace MPQToTACT
{
    /// <summary>
    /// This is a custom Encoding File that is used globally to cache all files being passed
    /// through. This file should only be deleted if you are not producing an incremental CDN
    /// </summary>
    static class EncodingCache
    {
        private const string FileName = "encoding.cache";
        private static readonly object objLock = new();

        private static Options Options;
        private static string FullPath;
        private static EncodingFile Instance;

        public static void Initialise(Options options)
        {
            Options = options;
            FullPath = Path.Combine(options.OutputFolder, FileName);

            if (File.Exists(FullPath))
                Instance = new EncodingFile(FullPath);
            else
                Instance = new EncodingFile();
        }

        public static void Save()
        {
            // save the encoding file and clone it to both
            // the output directory and the build specific directory
            // the latter can be used as a restore point
            Instance.Write(Options.TempDirectory);
            File.Copy(Instance.FilePath, FullPath, true);
            File.Copy(Instance.FilePath, Path.Combine(Options.OutputFolder, Options.BuildName, FileName), true);
        }

        public static void AddOrUpdate(CASRecord record)
        {
            lock (objLock)
                Instance.AddOrUpdate(record);
        }

        public static bool ContainsCKey(MD5Hash ckey) => Instance.ContainsCKey(ckey);

        public static bool ContainsEKey(MD5Hash ekey) => Instance.ContainsEKey(ekey);

        public static bool TryGetRecord(MD5Hash ckey, string file, out CASRecord record)
        {
            record = null;

            if (!Instance.TryGetCKeyEntry(ckey, out var centry))
                return false;
            if (!Instance.TryGetEKeyEntry(centry.EKeys[0], out var eentry))
                return false;
            if (eentry.ESpecIndex >= Instance.ESpecStringTable.Count)
                return false;

            record = new CASRecord()
            {
                CKey = ckey,
                EBlock = new TACT.Net.BlockTable.EBlock()
                {
                    CompressedSize = (uint)eentry.CompressedSize,
                    DecompressedSize = (uint)centry.DecompressedSize,
                    EKey = centry.EKeys[0],
                },
                FileName = file
            };

            return true;
        }
    }
}
