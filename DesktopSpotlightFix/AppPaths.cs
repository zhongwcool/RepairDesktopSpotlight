namespace DesktopSpotlightFix;

public static class AppPaths
{
    public const string AppFolderName = "DesktopSpotlightFix";

    public static string LocalRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string LogDirectory => Path.Combine(LocalRoot, "logs");

    public static string BackupRoot => Path.Combine(LocalRoot, "backups");

    public static string FallbackPicturesDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "SpotlightDesktop");

    public static string IrisCacheDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            PackageIdentity.TaskCatalog.PackageFamilyName,
            "LocalCache",
            "Microsoft",
            "IrisService");

    public static string DefaultLogFile()
    {
        Directory.CreateDirectory(LogDirectory);
        return Path.Combine(LogDirectory, "desktop-spotlight-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
    }
}
