using System;
using System.IO;
using System.Text.RegularExpressions;
using MPQToTACT.Helpers;
using MPQToTACT.Readers;
using TACT.Net;
using TACT.Net.Install;
using TACT.Net.Root;

namespace MPQToTACT
{
    class Program
    {
        public const string WoWDirectory = @"E:\World of Warcraft 0.5.3";
        public const string BuildName = "WOW-3368patch0.5.3_Alpha_Retail";
        public const string OutputFolder = @"C:\wamp64\www";

        static void Main(string[] args)
        {
            Clean(OutputFolder);

            // create the repo
            var tactRepo = CreateRepo(BuildName);
            // load the the Install/Download tag lookups
            TagGenerator.Load(tactRepo);

            // load the readers
            var dirReader = new DirectoryReader(WoWDirectory, tactRepo);
            var mpqReader = new MPQReader(dirReader.PatchArchives, tactRepo);

            // populate the Install and Root files
            // TACT.Net will handle the population of/references to the other system files
            PopulateInstallFile(tactRepo, dirReader, mpqReader);
            PopulateRootFile(tactRepo, dirReader, mpqReader);

            // build and save the repo
            Log.WriteLine("Building and Saving repo");
            tactRepo.Save(OutputFolder);

            // cleanup
            DeleteDirectory("temp");
        }

        #region Methods

        /// <summary>
        /// Creates a new TACT Repo prepopulated with basic product information
        /// </summary>
        /// <param name="buildName"></param>
        /// <returns></returns>
        private static TACTRepo CreateRepo(string buildName)
        {
            // validate that the build name is blizz-like and extract the build info
            var match = Regex.Match(buildName, @"wow-(\d{4,6})patch(\d\.\d{1,2}\.\d{1,2})_(retail|ptr|beta|alpha)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            if (!match.Success)
                throw new ArgumentException("Invalid buildname format. Example: 'WOW-18125patch6.0.1_Beta'");

            Log.WriteLine($"Creating Repo for {buildName}");

            string versionName = match.Groups[2].Value + "." + match.Groups[1].Value;
            string buildId = match.Groups[1].Value;
            string branch = match.Groups[3].Value;

            // calculate the build uid
            string buildUID = "wow";
            switch(branch.ToLower())
            {
                case "ptr":
                    buildUID = "wowt";
                    break;
                case "beta":
                    buildUID = "wow_beta";
                    break;
                case "alpha":
                    buildUID = "wow_alpha";
                    break;
            }

            // open a new tact instance
            TACTRepo tactrepo = new TACTRepo();
            tactrepo.Create("wow", Locale.US, uint.Parse(buildId));

            // update the configs with the build and server info
            // - CDNs file is probably not necessary for this use case
            tactrepo.ConfigContainer.VersionsFile.SetValue("BuildId", buildId);
            tactrepo.ConfigContainer.VersionsFile.SetValue("VersionsName", versionName);
            tactrepo.ConfigContainer.BuildConfig.SetValue("Build-Name", buildName, 0);
            tactrepo.ConfigContainer.BuildConfig.SetValue("Build-UID", buildUID, 0);
            tactrepo.ConfigContainer.BuildConfig.SetValue("Build-Product", "WoW", 0);
            tactrepo.ConfigContainer.CDNsFile.SetValue("Hosts", "localhost");
            tactrepo.ConfigContainer.CDNsFile.SetValue("Servers", "http://127.0.0.1");

            // set root variables
            tactrepo.RootFile.LocaleFlags = LocaleFlags.enUS;
            tactrepo.RootFile.ContentFlags = ContentFlags.None;
            tactrepo.RootFile.FileLookup = new ListFileLookup();

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
                mpqReader.Process<InstallFile>((x) => repo.InstallFile.AddOrUpdate(x, repo));
            }
        }

        private static void PopulateRootFile(TACTRepo repo, DirectoryReader dirReader, MPQReader mpqReader)
        {
            Log.WriteLine("Extracting Data files");

            // load files from mpqs, patches and loose files in the data directory 
            mpqReader.FileList.Clear();
            mpqReader.EnumerateDataArchives(dirReader.DataArchives);
            mpqReader.EnumeratePatchArchives();
            mpqReader.EnumerateLooseDataFiles(dirReader.GetLooseDataFiles());

            Log.WriteLine("Populating Root file");
            mpqReader.Process<RootFile>((x) => repo.RootFile.AddOrUpdate(x, repo));
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Deleted all files from the temp and output directories
        /// </summary>
        /// <param name="output"></param>
        private static void Clean(string output)
        {
            DeleteDirectory(Path.Combine(output, "wow"));
            DeleteDirectory(Path.Combine(output, "tpr"));
            DeleteDirectory("temp");

            Directory.CreateDirectory("temp");
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
                System.Threading.Thread.Sleep(50);
                Directory.Delete(path, true);
            }
        }

        #endregion
    }
}
