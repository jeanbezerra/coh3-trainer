namespace Coh3Trainer.Models;

public static class CommandPointRules
{
    public const int DefaultIncrement = 5;
    public const int MinimumIncrement = 1;
    public const int MaximumPoints = 32;

    public static bool IsValidIncrement(int amount) =>
        amount is >= MinimumIncrement and <= MaximumPoints;

    public static bool IsPlausibleValue(double value) =>
        double.IsFinite(value) && value is >= 0 and <= MaximumPoints;
}
