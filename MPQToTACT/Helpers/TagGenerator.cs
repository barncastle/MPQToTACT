using System;
using System.IO;
using System.Linq;
using TACT.Net.Tags;

namespace MPQToTACT.Helpers
{
    static class TagGenerator
    {
        private static string[] MacTags; // Mac
        private static string[] Win64Tags; // x64 Windows
        private static string[] Win32Tags; // x86 Windows
        private static string[] WinTags; // all Windows
        private static string[] LinuxTags; // custom type

        public static void Load(TACT.Net.TACTRepo tactRepo)
        {
            var tags = tactRepo.InstallFile.Tags.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            tags.Remove("Web"); // never needed for this use-case

            foreach (var type in new[] { "mac", "win64", "win32", "win", "linux" })
            {
                var temp = tags.ToList();

                switch (type)
                {
                    case "mac":
                        temp.Remove("Windows");
                        temp.Remove("x86_64");
                        MacTags = temp.ToArray();
                        break;
                    case "win64":
                        temp.Remove("OSX");
                        temp.Remove("x86_32");
                        Win64Tags = temp.ToArray();
                        break;
                    case "win32":
                        temp.Remove("OSX");
                        temp.Remove("x86_64");
                        Win32Tags = temp.ToArray();
                        break;
                    case "win":
                        temp.Remove("OSX");
                        WinTags = temp.ToArray();
                        break;
                    case "linux":
                        temp.Insert(0, "Linux");
                        temp.Remove("OSX");
                        temp.Remove("Windows");
                        temp.Remove("x86_64");
                        LinuxTags = temp.ToArray();
                        break;
                }
            }

            // add linux tag
            var linuxtag = new TagEntry()
            {
                Name = "Linux",
                TypeId = 3 // Platform
            };
            tactRepo.InstallFile.AddOrUpdate(linuxtag);
            tactRepo.DownloadFile.AddOrUpdate(linuxtag);
        }

        public static string[] GetTags(string filename, Stream filestream = null)
        {
            // Mac
            if (filename.Contains(".app", StringComparison.OrdinalIgnoreCase))
                return MacTags;

            // Linux
            if (filename.Contains(".so.", StringComparison.OrdinalIgnoreCase))
                return LinuxTags;

            var ext = Path.GetExtension(filename).ToLowerInvariant();

            if (ext == ".so")
                return LinuxTags;
            if (ext == ".pak")
                return WinTags;
            if (ext == ".dylib")
                return MacTags;

            if (ext == ".dll" || ext == ".exe" || ext == "")
            {
                if (filename.Contains("-64.", StringComparison.OrdinalIgnoreCase))
                    return Win64Tags;

                using var fs = filestream ?? File.OpenRead(filename);
                return GetTagsFromBinary(fs);
            }

            return null; // use all tags
        }

        /// <summary>
        /// Reads the machine code from the binary
        /// </summary>
        private static string[] GetTagsFromBinary(Stream fs)
        {
            if (fs.Length < 4)
                return null;
            
            using var br = new BinaryReader(fs);
            br.BaseStream.Position = 0;

            var magic = br.ReadUInt32();

            // linux - ELF or #!/b
            if (magic == 0x464C457F || magic == 0x622F2123)
                return LinuxTags;
            // mac 32
            if (magic == 0xFEEDFACE || magic == 0xCEFAEDFE)
                return MacTags;
            // mac 64
            if (magic == 0xFEEDFACF || magic == 0xCFFAEDFE)
                return MacTags;
            // is not windows - MZ
            if ((magic & 0xFFFF) != 0x5A4D)
                return null;

            fs.Position = 60; // PE Header offset
            fs.Position = br.ReadUInt32() + 4; // machine offset

            return br.ReadUInt16() switch // machine
            {
                0x8664 => Win64Tags, // x64
                0x200 => Win64Tags, // IA64
                0x14C => Win32Tags, // I386
                _ => null,
            };
        }
    }
}
