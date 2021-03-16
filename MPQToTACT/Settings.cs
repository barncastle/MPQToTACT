using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace MPQToTACT
{
    class Settings
    {
        public static string[] ExcludedDirectories { get; set; }
        public static string[] ExcludedExtensions { get; set; }
        public static string TempDirectory { get; set; }

        static Settings()
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("config.json", optional: false)
                .Build();

            ExcludedDirectories = config.GetValue<string[]>("excludedDirectories");
            ExcludedExtensions = config.GetValue<string[]>("excludedExtensions");
            TempDirectory = config.GetValue<string>("tempDirectory");
        }
    }
}
