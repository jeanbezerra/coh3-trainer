namespace Coh3Trainer.Services;

public static class PlayerCommandPointLayout
{
    public const string SupportedGameVersion = "5.1.50313.0";
    public const int CurrentValueOffset = 0x6A4;
    public const int ScarValueOffset = 0x188;

    public static bool IsSupportedVersion(string gameVersion) =>
        string.Equals(gameVersion, SupportedGameVersion, StringComparison.OrdinalIgnoreCase);
}
