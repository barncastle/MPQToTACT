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
        const string LISTFILE_NAME = "(listfile)";

        public readonly TACTRepo TACTRepo;
        public readonly ConcurrentDictionary<string, CASRecord> FileList;

        private const StringComparison _comparison = StringComparison.OrdinalIgnoreCase;
        private readonly Queue<string> _patchArchives;
        private readonly string _dataDirectory;


        public MPQReader(IEnumerable<string> patchArchives, TACTRepo tactrepo)
        {
            TACTRepo = tactrepo;
            FileList = new ConcurrentDictionary<string, CASRecord>();

            _patchArchives = new Queue<string>(patchArchives);
            _dataDirectory = Path.DirectorySeparatorChar + "DATA" + Path.DirectorySeparatorChar;
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
                    mpq.AddPatchArchives(_patchArchives);
                    ExportFiles(mpq, files, applyTags).Wait();
                }
                else if (!TryReadAlpha(mpq, archivename))
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
            if (_patchArchives.Count == 0)
                return;

            Log.WriteLine("Exporting Patch Archive files");

            while (_patchArchives.Count > 0)
            {
                using var mpq = new MpqArchive(_patchArchives.Dequeue(), FileAccess.Read);
                Log.WriteLine("    Exporting " + Path.GetFileName(mpq.FilePath));

                if (TryGetListFile(mpq, out var files))
                {
                    mpq.AddPatchArchives(_patchArchives);
                    ExportFiles(mpq, files).Wait();
                }
                else
                {
                    throw new Exception(Path.GetFileName(mpq.FilePath) + " MISSING LISTFILE");
                }
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
                int index = file.IndexOf(_dataDirectory, _comparison) + _dataDirectory.Length;
                string filename = file[index..].WoWNormalise();

                var record = BlockTableEncoder.EncodeAndExport(file, Program.TempFolder, filename);
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
            var type = typeof(T);
            switch (true)
            {
                case true when type == typeof(InstallFile):
                    TACTRepo.InstallFile?.SetTagsCapacity(FileList.Count);
                    break;
                case true when type == typeof(RootFile):
                    TACTRepo.DownloadFile?.SetTagsCapacity(FileList.Count);
                    TACTRepo.DownloadSizeFile?.SetTagsCapacity(FileList.Count);
                    break;
            }

            foreach (var file in FileList.OrderBy(x => x.Key))
                action.Invoke(file.Value);
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
                        record = BlockTableEncoder.EncodeAndExport(fs, map, Program.TempFolder, file);
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
            int index = archivename.IndexOf(_dataDirectory, _comparison) + _dataDirectory.Length;
            string file = Path.ChangeExtension(archivename[index..], null).WoWNormalise();

            if (FileList.ContainsKey(file))
                return true;

            // add the filename as the listfile
            string internalname = Path.GetFileName(file);
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
                        record = BlockTableEncoder.EncodeAndExport(fs, map, Program.TempFolder, file);
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
        private bool TryGetListFile(MpqArchive mpq, out List<string> filteredlist)
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
                filteredlist.RemoveAll(x => (x.StartsWith('(') && x.EndsWith(')')) || x.EndsWith("md5.lst") || x.StartsWith("component."));
                filteredlist.TrimExcess();

                return filteredlist.Count > 0;
            }

            return false;
        }

        #endregion
    }
}
