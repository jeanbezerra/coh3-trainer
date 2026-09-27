namespace Coh3Trainer.Models;

public static class PopulationLimitRules
{
    public const int MinimumLimit = 100;
    public const int MaximumLimit = 1_000;
    public const int DefaultLimit = 250;

    public static bool IsValidLimit(int limit) => limit is >= MinimumLimit and <= MaximumLimit;
}
