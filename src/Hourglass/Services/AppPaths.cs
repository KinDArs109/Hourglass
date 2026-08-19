using System.IO;

namespace Hourglass.Services;

public static class AppPaths
{
    public const string ProductName = "Hourglass";

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ProductName);

    public static string ConfigFile { get; } = Path.Combine(DataDirectory, "config.json");
}
