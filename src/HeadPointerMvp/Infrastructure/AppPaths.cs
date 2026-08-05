namespace HeadPointerMvp.Infrastructure;

public static class AppPaths
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeadPointerMvp");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "logs", $"headpointer-{DateTime.UtcNow:yyyyMMdd}.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
    }
}
