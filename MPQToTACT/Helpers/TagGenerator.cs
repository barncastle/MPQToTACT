using System;
using System.IO;
using System.Linq;

namespace MPQToTACT.Helpers
{
    static class TagGenerator
    {
        private static string[] MacTags; // Mac
        private static string[] Win64Tags; // x64 Windows
        private static string[] Win32Tags; // x86 Windows
        private static string[] WinTags; // all Windows

        public static void Load(TACT.Net.TACTRepo tactRepo)
        {
            var tags = tactRepo.InstallFile.Tags.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            tags.Remove("Web"); // never needed for this use-case

            foreach (var type in new[] { "mac", "win64", "win32", "win" })
            {
                var temp = tags.ToHashSet();

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
                }
            }
        }

        public static string[] GetTags(string filename, Stream filestream = null)
        {
            // Mac
            if (filename.Contains(".app", StringComparison.OrdinalIgnoreCase))
                return MacTags;

            switch (Path.GetExtension(filename).ToLowerInvariant())
            {
                case ".dll":
                case ".exe":
                    {
                        // x64 shortcut
                        if (filename.Contains("-64.", StringComparison.OrdinalIgnoreCase))
                            return Win64Tags;

                        using (var fs = filestream ?? File.OpenRead(filename))
                            return GetTagsFromBinary(fs);
                    }
                case ".pak":
                    return WinTags;
                default:
                    return null; // use all tags
            }
        }

        /// <summary>
        /// Reads the machine code from the binary
        /// </summary>
        /// <param name="fs"></param>
        /// <returns></returns>
        private static string[] GetTagsFromBinary(Stream fs)
        {
            fs.Position = 0;

            using (var br = new BinaryReader(fs))
            {
                // not a valid win binary, use all tags
                if (br.ReadUInt16() != 0x5A4D)
                    return null;

                fs.Position = 60; // PE Header offset
                fs.Position = br.ReadUInt32() + 4; // machine offset

                switch (br.ReadUInt16()) // machine
                {
                    case 0x8664: // x64
                    case 0x200: // IA64
                        return Win64Tags;
                    case 0x14C: // I386
                        return Win32Tags;
                    default:
                        return null;
                }
            }
        }
    }
}
