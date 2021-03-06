﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks.Dataflow;
using MPQToTACT.Helpers;
using TACT.Net;
using TACT.Net.BlockTable;

namespace MPQToTACT.Readers
{
    internal class DirectoryReader
    {
        private const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

        public readonly string BaseDirectory;
        public readonly Options Options;
        public readonly TACTRepo TACTRepo;

        public readonly List<string> DataArchives;
        public readonly List<string> BaseArchives;
        public readonly List<string> PatchArchives;

        public DirectoryReader(Options options, TACTRepo tactrepo)
        {
            DataArchives = new List<string>(0x100);
            BaseArchives = new List<string>(0x40);
            PatchArchives = new List<string>(0x40);

            Options = options;
            TACTRepo = tactrepo;
            BaseDirectory = options.WoWDirectory;

            PopulateCollections();
        }

        #region Methods

        /// <summary>
        /// Returns loose files not inside an MPQ but in the Data directory
        /// </summary>
        /// <returns></returns>
        public IEnumerable<string> GetLooseDataFiles()
        {
            var localFiles = Directory.EnumerateFiles(BaseDirectory, "*", SearchOption.AllDirectories).ToList();
            localFiles.RemoveAll(x => x.EndsWith(".mpq", Comparison) || !x.Contains("\\data\\"));
            localFiles.TrimExcess();
            return localFiles;
        }

        /// <summary>
        /// Enumerates all non-archive local files, BLT encodes them and adds them to the InstallFile
        /// </summary>
        /// <param name="maxDegreeOfParallelism"></param>
        /// <returns></returns>
        public void ExportFiles(int maxDegreeOfParallelism = 15)
        {
            var results = new ConcurrentBag<CASRecord>();

            var block = new ActionBlock<string>(file =>
            {
                // strip the local path and normalise
                var name = file[(file.IndexOf(BaseDirectory, Comparison) + BaseDirectory.Length)..].WoWNormalise();

                // block table encode and export to the temp folder
                // then add appropiate tags
                var record = BlockTableEncoder.EncodeAndExport(file, Options.TempDirectory, name);
                record.Tags = TagGenerator.GetTags(file);

                if (!EncodingCache.ContainsEKey(record.EKey))
                    EncodingCache.AddOrUpdate(record);
                else
                    record.BLTEPath = "";

                results.Add(record);
            },
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism });

            // get local files minus exclusions          
            var localFiles = Directory.EnumerateFiles(BaseDirectory, "*", SearchOption.AllDirectories).ToList();
            localFiles.RemoveAll(x => HasDirectory(x) || HasExtension(x));
            localFiles.TrimExcess();

            // BLT encode everything
            localFiles.ForEach(x => block.Post(x));
            block.Complete();
            block.Completion.Wait();

            // set all tag's mask capacity to prevent lots of resize calls
            TACTRepo.InstallFile.SetTagsCapacity(results.Count);

            // add all results to the install file 
            // - this will distribute information to all other system files
            foreach (var res in results.OrderBy(x => x.FileName))
                TACTRepo.InstallFile.AddOrUpdate(res, TACTRepo);

            results.Clear();

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Finds all MPQ files in BaseDirectory and allocates them to the appropiate collection
        /// </summary>
        private void PopulateCollections()
        {
            var files = Directory.EnumerateFiles(BaseDirectory, "*.mpq", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                var filename = Path.GetFileName(file);

                // skip installation tomes and backups
                if (filename.Contains("tome", Comparison) ||
                    filename.Contains("backup", Comparison))
                    continue;

                // filter into the right collection
                if (filename.StartsWith("wow-update", Comparison))
                    PatchArchives.Add(file);
                else if (filename.StartsWith("base", Comparison))
                    BaseArchives.Add(file);
                else
                    DataArchives.Add(file);
            }

            PatchArchives.TrimExcess();
            BaseArchives.TrimExcess();
            DataArchives.TrimExcess();

            // sort them
            PatchArchives.Sort(MPQSorter.Sort);
            BaseArchives.Sort(MPQSorter.Sort);
            DataArchives.Sort(MPQSorter.Sort);
        }

        private bool HasDirectory(string path)
        {
            return Options.ExcludedDirectories.Overlaps(path.Split(Path.DirectorySeparatorChar));
        }

        private bool HasExtension(string path)
        {
            return Options.ExcludedExtensions.Contains(Path.GetExtension(path) ?? "");
        }

        #endregion
    }
}
