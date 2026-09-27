using System.IO;

namespace Coh3Trainer.Services;

public interface IApplicationPaths
{
    string DataDirectory { get; }
    string LogsDirectory { get; }
    string SettingsFilePath { get; }
}

public sealed class ApplicationPaths : IApplicationPaths
{
    public ApplicationPaths(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Coh3Trainer");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        SettingsFilePath = Path.Combine(DataDirectory, "settings.json");
    }

    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string SettingsFilePath { get; }
}
