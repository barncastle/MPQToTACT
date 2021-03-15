using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TACT.Net;
using TACT.Net.Cryptography;
using TACT.Net.Encoding;

namespace MPQToTACT
{
    public static class EncodingCache
    {
        private const string FileName = "encoding.cache";
        private static readonly object objLock = new();

        private static readonly string FullPath = Path.Combine(Program.OutputFolder, FileName);
        private static EncodingFile Instance;

        public static void Initialise()
        {
            if (File.Exists(FullPath))
                Instance = new EncodingFile(FullPath);
            else
                Instance = new EncodingFile();
        }

        public static void Save()
        {
            Instance.Write(Program.TempFolder);
            File.Copy(Instance.FilePath, FullPath, true);
            File.Copy(Instance.FilePath, Path.Combine(Program.OutputFolder, Program.BuildName, FileName), true);
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
