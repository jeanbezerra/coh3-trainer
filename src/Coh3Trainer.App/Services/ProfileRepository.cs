using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coh3Trainer.Models;

namespace Coh3Trainer.Services;

public sealed class ProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _profilesDirectory;

    public ProfileRepository(string? profilesDirectory = null)
    {
        _profilesDirectory = profilesDirectory ?? Path.Combine(AppContext.BaseDirectory, "profiles");
    }

    public GameProfile? Find(string processName, string executableVersion)
    {
        if (!Directory.Exists(_profilesDirectory))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(_profilesDirectory, "*.json"))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(path), JsonOptions);
                if (profile is { Enabled: true, SchemaVersion: 1 } &&
                    string.Equals(profile.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(profile.ExecutableVersion, executableVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }
            catch
            {
                // Um perfil inválido é ignorado; o backend usará a identificação automática.
            }
        }

        return null;
    }
}
