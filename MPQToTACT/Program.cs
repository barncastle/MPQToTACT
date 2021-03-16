using System;
using System.IO;
using System.Text.RegularExpressions;
using CommandLine;
using MPQToTACT.Helpers;
using MPQToTACT.ListFiles;
using MPQToTACT.Readers;
using TACT.Net;
using TACT.Net.Install;
using TACT.Net.Root;

namespace MPQToTACT
{
    class Program
    {
        static void Main(string[] args)
        {
            using var parser = new Parser(s =>
            {
                s.HelpWriter = Console.Error;
                s.CaseInsensitiveEnumValues = true;
                s.AutoVersion = false;
            });

            var result = parser.ParseArguments<Options>(args);
            if (result.Tag == ParserResultType.Parsed)
                result.WithParsed(Run);
        }

        private static void Run(Options options)
        {       
            Clean(options);

            options.LoadConfig();

            EncodingCache.Initialise(options);

            // create the repo
            var tactRepo = CreateRepo(options);
            // load the the Install/Download tag lookups
            TagGenerator.Load(tactRepo);

            // load the readers
            var dirReader = new DirectoryReader(options, tactRepo);
            var mpqReader = new MPQReader(options, tactRepo, dirReader.PatchArchives);

            // populate the Install and Root files
            PopulateInstallFile(tactRepo, dirReader, mpqReader);
            PopulateRootFile(tactRepo, dirReader, mpqReader);

            // build and save the repo
            Log.WriteLine("Building and Saving repo");
            tactRepo.Save(options.OutputFolder, Path.Combine(options.OutputFolder, options.BuildName));

            EncodingCache.Save();

            // cleanup
            Clean(options);
        }

        #region Methods

        /// <summary>
        /// Creates a new TACT Repo prepopulated with basic product information
        /// </summary>
        /// <param name="buildName"></param>
        /// <returns></returns>
        private static TACTRepo CreateRepo(Options options)
        {
            // validate that the build name is blizz-like and extract the build info
            var match = Regex.Match(options.BuildName, @"wow-(\d{4,6})patch(\d\.\d{1,2}\.\d{1,2})_(retail|ptr|beta|alpha)", RegexOptions.IgnoreCase);
            if (!match.Success)
                throw new ArgumentException("Invalid buildname format. Example: 'WOW-18125patch6.0.1_Beta'");

            Log.WriteLine($"Creating Repo for {options.BuildName}");

            var versionName = match.Groups[2].Value + "." + match.Groups[1].Value;
            var buildId = match.Groups[1].Value;
            var branch = match.Groups[3].Value;

            // calculate the build uid
            var buildUID = branch.ToLower() switch
            {
                "ptr" => "wowt",
                "beta" => "wow_beta",
                "alpha" => "wow_alpha",
                _ => "wow",
            };

            // open a new tact instance
            TACTRepo tactrepo = new();
            tactrepo.Create("wow", Locale.US, uint.Parse(buildId));

            // update the configs with the build and server info
            // - CDNs file is probably not necessary for this use case
            tactrepo.ManifestContainer.VersionsFile.SetValue("BuildId", buildId);
            tactrepo.ManifestContainer.VersionsFile.SetValue("VersionsName", versionName);
            tactrepo.ConfigContainer.BuildConfig.SetValue("Build-Name", options.BuildName, 0);
            tactrepo.ConfigContainer.BuildConfig.SetValue("Build-UID", buildUID, 0);
            tactrepo.ConfigContainer.BuildConfig.SetValue("Build-Product", "WoW", 0);
            tactrepo.ManifestContainer.CDNsFile.SetValue("Hosts", "localhost");
            tactrepo.ManifestContainer.CDNsFile.SetValue("Servers", "http://127.0.0.1");
            tactrepo.ManifestContainer.CDNsFile.SetValue("Path", "");

            // load and append existing indices
            tactrepo.IndexContainer.Open(options.OutputFolder);
            foreach (var index in Directory.GetFiles(options.OutputFolder, "*.index", SearchOption.AllDirectories))
            {
                tactrepo.ConfigContainer.CDNConfig.AddValue("archives", Path.GetFileNameWithoutExtension(index));
                tactrepo.ConfigContainer.CDNConfig.AddValue("archives-index-size", new FileInfo(index).Length.ToString());
            }

            // set root variables
            tactrepo.RootFile.LocaleFlags = LocaleFlags.enUS;
            tactrepo.RootFile.ContentFlags = ContentFlags.None;
            tactrepo.RootFile.FileLookup = new PooledListFile();
            tactrepo.RootFile.AddBlock(LocaleFlags.All_WoW, ContentFlags.None);

            return tactrepo;
        }

        private static void PopulateInstallFile(TACTRepo repo, DirectoryReader dirReader, MPQReader mpqReader)
        {
            Log.WriteLine("Extracting and Populating Install file");

            // use base-*.mpq if available else enumerate all non-archived files not inside the data folder
            // this ignores temp data folders e.g. wdb, wtf, cache etc
            if (dirReader.BaseArchives.Count == 0)
            {
                dirReader.ExportFiles();
            }
            else
            {
                mpqReader.EnumerateDataArchives(dirReader.BaseArchives, true);
                mpqReader.Process<InstallFile>((x) =>
                {
                    repo.InstallFile.AddOrUpdate(x);
                    repo.EncodingFile.AddOrUpdate(x);
                    repo.IndexContainer.Enqueue(x);
                    repo.DownloadFile.AddOrUpdate(x);
                });
            }
        }

        private static void PopulateRootFile(TACTRepo repo, DirectoryReader dirReader, MPQReader mpqReader)
        {
            Log.WriteLine("Extracting Data files");

            // load files from mpqs, patches and loose files in the data directory 
            mpqReader.FileList.Clear();
            mpqReader.EnumerateDataArchives(dirReader.DataArchives);
            mpqReader.EnumeratePatchArchives();
            //mpqReader.EnumerateLooseDataFiles(dirReader.GetLooseDataFiles());

            Log.WriteLine("Populating Root file");
            mpqReader.Process<RootFile>((x) =>
            {
                repo.RootFile.AddOrUpdate(x);
                repo.EncodingFile.AddOrUpdate(x);
                repo.IndexContainer.Enqueue(x);
                repo.DownloadFile.AddOrUpdate(x);
            });
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Deleted all files from the temp and output directories
        /// </summary>
        /// <param name="output"></param>
        private static void Clean(Options options)
        {
            DeleteDirectory(options.TempDirectory);
            Directory.CreateDirectory(options.TempDirectory);
        }

        private static void DeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
                return;

            Log.WriteLine($"Deleting {path}");

            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception ex)
            when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // handling of windows bug for deletion of large folders
                System.Threading.Thread.Sleep(50);
                Directory.Delete(path, true);
            }
        }

        #endregion
    }
}
