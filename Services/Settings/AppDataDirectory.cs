namespace LiberaFx.Services.Settings;

public static class AppDataDirectory
{
    public static string Root
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                if (!Path.IsPathFullyQualified(overridePath))
                    throw new InvalidOperationException("LIBERAFX_DATA_ROOT must be an absolute path.");
                return Path.GetFullPath(overridePath);
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }
}
