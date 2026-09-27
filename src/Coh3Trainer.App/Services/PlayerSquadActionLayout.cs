namespace Coh3Trainer.Services;

/// <summary>
/// Native entry points validated against a specific executable build. These are
/// consumed only by code running on the game's own simulation thread.
/// </summary>
public sealed record PlayerSquadActionLayout(
    long GetPlayerSquadsRva,
    long IncreaseVeterancyRankRva,
    long SetHealthRva,
    long AdjustAbilityCooldownRva)
{
    internal const string SupportedVersion = "5.1.50313.0";

    public static bool IsSupportedVersion(string version) =>
        string.Equals(version, SupportedVersion, StringComparison.OrdinalIgnoreCase);

    public static PlayerSquadActionLayout? ForVersion(string version) =>
        IsSupportedVersion(version)
            ? new PlayerSquadActionLayout(
                0x2AACE30,
                0x2AF5590,
                0x2AF8070,
                0x2AEC0C0)
            : null;
}
