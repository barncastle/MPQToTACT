namespace MPQToTACT.Helpers
{
    static class Extensions
    {
        public static string WoWNormalise(this string str)
        {
            return str.TrimStart(new char[] { '\\', '/' })
                      .Replace('\\', '/')
                      .ToLowerInvariant();
        }
    }
}
