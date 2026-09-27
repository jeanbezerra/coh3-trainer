using System.Text.Json.Serialization;

namespace Coh3Trainer.Models;

public sealed class TrainerSettings
{
    public Dictionary<ResourceKind, ResourceBindingSettings> Resources { get; set; } = CreateDefaults();
    public double IncomeMultiplier { get; set; } = 1;
    public PopulationLimitSettings PopulationLimit { get; set; } = new();

    [JsonIgnore]
    public static IReadOnlyList<ResourceKind> AllResources { get; } = Enum.GetValues<ResourceKind>();

    [JsonIgnore]
    public static IReadOnlyList<ResourceKind> EnabledResources { get; } =
        new[] { ResourceKind.Manpower, ResourceKind.Fuel, ResourceKind.Army, ResourceKind.CommandPoints };

    [JsonIgnore]
    public static IReadOnlyList<ResourceKind> IncomeResources { get; } =
        new[] { ResourceKind.Manpower, ResourceKind.Fuel, ResourceKind.Army };

    [JsonIgnore]
    public static IReadOnlyList<double> IncomeMultipliers { get; } = new[] { 1d, 2d, 3d, 5d };

    public static TrainerSettings Default() => new();

    public void Normalize()
    {
        if (!IsIncomeMultiplierSupported(IncomeMultiplier))
        {
            IncomeMultiplier = 1;
        }

        PopulationLimit ??= new PopulationLimitSettings();
        if (!PopulationLimitRules.IsValidLimit(PopulationLimit.Limit))
        {
            PopulationLimit.Limit = PopulationLimitRules.DefaultLimit;
        }

        Resources ??= new Dictionary<ResourceKind, ResourceBindingSettings>();
        var defaults = CreateDefaults();
        foreach (var kind in AllResources)
        {
            if (!Resources.TryGetValue(kind, out var item) || item is null)
            {
                Resources[kind] = defaults[kind];
                continue;
            }

            item.Amount = kind == ResourceKind.CommandPoints
                ? Math.Clamp(item.Amount, CommandPointRules.MinimumIncrement, CommandPointRules.MaximumPoints)
                : Math.Clamp(item.Amount, 1, 1_000_000);
            if (!IsFunctionKey(item.Hotkey))
            {
                item.Hotkey = defaults[kind].Hotkey;
            }
        }
    }

    public static bool IsFunctionKey(string? key) =>
        key is not null && key.Length is >= 2 and <= 3 &&
        key[0] == 'F' && int.TryParse(key[1..], out var number) && number is >= 1 and <= 12;

    public static bool IsIncomeMultiplierSupported(double multiplier) =>
        IncomeMultipliers.Any(value => Math.Abs(value - multiplier) < 0.001);

    private static Dictionary<ResourceKind, ResourceBindingSettings> CreateDefaults() => new()
    {
        [ResourceKind.Manpower] = new() { Amount = 1000, Hotkey = "F6" },
        [ResourceKind.Fuel] = new() { Amount = 500, Hotkey = "F7" },
        [ResourceKind.Army] = new() { Amount = 500, Hotkey = "F8" },
        [ResourceKind.CommandPoints] = new() { Amount = CommandPointRules.DefaultIncrement, Hotkey = "F9" },
        [ResourceKind.VictoryPoints] = new() { Amount = 100, Hotkey = "F10" }
    };
}

public sealed class ResourceBindingSettings
{
    public int Amount { get; set; }
    public string Hotkey { get; set; } = string.Empty;
}

public sealed class PopulationLimitSettings
{
    public bool Enabled { get; set; } = true;
    public int Limit { get; set; } = PopulationLimitRules.DefaultLimit;
}
