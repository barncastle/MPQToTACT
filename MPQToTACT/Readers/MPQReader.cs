using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using MPQToTACT.Helpers;
using MPQToTACT.MPQ;
using TACT.Net;
using TACT.Net.BlockTable;
using TACT.Net.Cryptography;
using TACT.Net.Install;
using TACT.Net.Root;

namespace MPQToTACT.Readers
{
    internal class MPQReader
    {
        private const string LISTFILE_NAME = "(listfile)";

        public readonly Options Options;
        public readonly TACTRepo TACTRepo;
        public readonly ConcurrentDictionary<string, CASRecord> FileList;

        private readonly Queue<string> PatchArchives;
        private readonly string DataDirectory;

        public MPQReader(Options options, TACTRepo tactrepo, IList<string> patchArchives)
        {
            Options = options;
            TACTRepo = tactrepo;
            FileList = new ConcurrentDictionary<string, CASRecord>(StringComparer.OrdinalIgnoreCase);

            PatchArchives = new Queue<string>(patchArchives);
            DataDirectory = Path.DirectorySeparatorChar + "DATA" + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Iterates all the data archives, extracting and BLT encoding all files
        /// <para>Patches are applied where applicable to get the most up-to-date variant of each file.</para>
        /// </summary>
        /// <param name="archives"></param>
        public void EnumerateDataArchives(IEnumerable<string> archives, bool applyTags = false)
        {
            Log.WriteLine("Exporting Data Archive files");

            foreach (var archivename in archives)
            {
                using var mpq = new MpqArchive(archivename, FileAccess.Read);
                Log.WriteLine("   Exporting " + Path.GetFileName(mpq.FilePath));

                if (TryGetListFile(mpq, out var files))
                {
                    mpq.AddPatchArchives(PatchArchives);
                    ExportFiles(mpq, files, applyTags).Wait();
                }
                else if (TryReadAlpha(mpq, archivename))
                {
                    continue;
                }
                else
                {
                    throw new FormatException(Path.GetFileName(archivename) + " HAS NO LISTFILE!");
                }
            }
        }

        /// <summary>
        /// Iterates all the patch archives, extracting and BLT encoding all new files
        /// </summary>
        public void EnumeratePatchArchives()
        {
            if (PatchArchives.Count == 0)
                return;

            Log.WriteLine("Exporting Patch Archive files");

            while (PatchArchives.Count > 0)
            {
                using var mpq = new MpqArchive(PatchArchives.Dequeue(), FileAccess.Read);
                Log.WriteLine("    Exporting " + Path.GetFileName(mpq.FilePath));

                if (!TryGetListFile(mpq, out var files))
                    throw new Exception(Path.GetFileName(mpq.FilePath) + " MISSING LISTFILE");

                mpq.AddPatchArchives(PatchArchives);
                ExportFiles(mpq, files).Wait();
            }
        }

        /// <summary>
        /// Iterates all loose files within the data directory and BLT encodes them
        /// </summary>
        /// <param name="filenames"></param>
        public void EnumerateLooseDataFiles(IEnumerable<string> filenames)
        {
            if (!filenames.Any())
                return;

            Log.WriteLine("Exporting loose Data files");

            var block = new ActionBlock<string>(file =>
            {
                var filename = GetInternalPath(file);

                var record = BlockTableEncoder.EncodeAndExport(file, Options.TempDirectory, filename);
                record.Tags = TagGenerator.GetTags(file);

                if (!EncodingCache.ContainsEKey(record.EKey))
                    EncodingCache.AddOrUpdate(record);
                else
                    record.BLTEPath = "";

                FileList.TryAdd(filename, record);
            },
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 150 });

            foreach (var f in filenames)
                block.Post(f);

            block.Complete();
            block.Completion.Wait();
        }


        /// <summary>
        /// Adds the CASRecords to the appropiate SystemFile
        /// </summary>
        /// <param name="action"></param>
        public void Process<T>(Action<CASRecord> action) where T : ISystemFile
        {
            // set tag capacity
            if (typeof(T) == typeof(InstallFile))
            {
                TACTRepo.InstallFile?.SetTagsCapacity(FileList.Count);
            }
            else if (typeof(T) == typeof(RootFile))
            {
                TACTRepo.DownloadFile?.SetTagsCapacity(FileList.Count);
                TACTRepo.DownloadSizeFile?.SetTagsCapacity(FileList.Count);
            }

            foreach (var file in FileList.OrderBy(x => x.Key))
                action(file.Value);
        }

        #region Helpers

        /// <summary>
        /// Extracts a collection of files from an archive and BLTE encodes them
        /// </summary>
        /// <param name="mpq"></param>
        /// <param name="filenames"></param>
        /// <param name="maxDegreeOfParallelism"></param>
        /// <returns></returns>
        private async Task ExportFiles(MpqArchive mpq, IEnumerable<string> filenames, bool applyTags = false, int maxDegreeOfParallelism = 150)
        {
            var block = new ActionBlock<string>(file =>
            {
                using var fs = mpq.OpenFile(file);

                // ignore PTCH files
                if ((fs.Flags & 0x100000u) == 0x100000u)
                    return;

                if (fs.CanRead && fs.Length > 0)
                {
                    var map = BlockTableEncoder.GetEMapFromExtension(file, fs.Length);

                    if (!EncodingCache.TryGetRecord(MD5Hash.Parse(fs.GetMD5Hash()), file, out var record))
                    {
                        record = BlockTableEncoder.EncodeAndExport(fs, map, Options.TempDirectory, file);
                        EncodingCache.AddOrUpdate(record);
                    }

                    if (applyTags)
                        record.Tags = TagGenerator.GetTags(file, fs);

                    record.EBlock.EncodingMap = map;
                    FileList.TryAdd(file, record);
                }
            },
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism });

            foreach (var file in filenames)
                if (!FileList.ContainsKey(file))
                    block.Post(file);

            block.Complete();
            await block.Completion;
        }

        /// <summary>
        /// Some alpha MPQs are hotswappable and only contain a single file and it's checksum
        /// </summary>
        /// <param name="mpq"></param>
        /// <param name="archivename"></param>
        private bool TryReadAlpha(MpqArchive mpq, string archivename)
        {
            // strip the local path and extension to get the filename
            var file = Path.ChangeExtension(GetInternalPath(archivename), null).WoWNormalise();

            if (FileList.ContainsKey(file))
                return true;

            // add the filename as the listfile
            var internalname = Path.GetFileName(file);
            mpq.AddListFile(internalname);

            // read file if known
            if (mpq.HasFile(internalname))
            {
                using var fs = mpq.OpenFile(internalname);

                if (fs.CanRead && fs.Length > 0)
                {
                    var map = BlockTableEncoder.GetEMapFromExtension(file, fs.Length);

                    if (!EncodingCache.TryGetRecord(MD5Hash.Parse(fs.GetMD5Hash()), file, out var record))
                    {
                        record = BlockTableEncoder.EncodeAndExport(fs, map, Options.TempDirectory, file);
                        EncodingCache.AddOrUpdate(record);
                    }

                    record.EBlock.EncodingMap = map;
                    FileList.TryAdd(file, record);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Attempts to read the listfile if present
        /// </summary>
        /// <param name="mpq"></param>
        /// <param name="filteredlist"></param>
        /// <returns></returns>
        private static bool TryGetListFile(MpqArchive mpq, out List<string> filteredlist)
        {
            filteredlist = new List<string>();

            if (mpq.HasFile(LISTFILE_NAME))
            {
                using (var file = mpq.OpenFile(LISTFILE_NAME))
                using (var sr = new StreamReader(file))
                {
                    if (!file.CanRead || file.Length <= 1)
                        return false;

                    while (!sr.EndOfStream)
                        filteredlist.Add(sr.ReadLine().WoWNormalise());
                }

                // remove the MPQ documentation files
                filteredlist.RemoveAll(RemoveSpecialFiles);
                filteredlist.TrimExcess();

                return filteredlist.Count > 0;
            }

            return false;
        }

        private static bool RemoveSpecialFiles(string value)
        {
            return (value.StartsWith('(') && value.EndsWith(')')) ||
                    value.EndsWith("md5.lst") ||
                    value.StartsWith("component.");
        }

        private string GetInternalPath(string value)
        {
            var index = value.IndexOf(DataDirectory, StringComparison.OrdinalIgnoreCase);
            index += DataDirectory.Length;
            return value[index..].WoWNormalise();
        }

        #endregion
    }
}
