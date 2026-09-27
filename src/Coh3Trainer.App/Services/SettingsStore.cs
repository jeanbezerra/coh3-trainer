using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coh3Trainer.Models;

namespace Coh3Trainer.Services;

public interface ISettingsStore
{
    string SettingsPath { get; }
    TrainerSettings Load();
    void Save(TrainerSettings settings);
}

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;

    public SettingsStore() : this(new ApplicationPaths())
    {
    }

    public SettingsStore(IApplicationPaths paths) : this(paths.SettingsFilePath)
    {
    }

    public SettingsStore(string settingsPath) => _settingsPath = settingsPath;

    public string SettingsPath => _settingsPath;

    public TrainerSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return TrainerSettings.Default();
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<TrainerSettings>(json, JsonOptions) ?? TrainerSettings.Default();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return TrainerSettings.Default();
        }
    }

    public void Save(TrainerSettings settings)
    {
        settings.Normalize();
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _settingsPath, true);
    }
}
