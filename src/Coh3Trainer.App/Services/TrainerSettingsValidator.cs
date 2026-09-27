using System.Globalization;
using Coh3Trainer.Localization;
using Coh3Trainer.Models;

namespace Coh3Trainer.Services;

public readonly record struct ResourceSettingsInput(string Amount, string? Hotkey);
public readonly record struct PopulationLimitSettingsInput(bool Enabled, string Limit);

public interface ITrainerSettingsValidator
{
    bool TryApply(
        TrainerSettings settings,
        IReadOnlyDictionary<ResourceKind, ResourceSettingsInput> inputs,
        PopulationLimitSettingsInput populationLimit,
        out string message);
}

public sealed class TrainerSettingsValidator : ITrainerSettingsValidator
{
    private readonly ITextLocalizer _localizer;

    public TrainerSettingsValidator(ITextLocalizer? localizer = null) =>
        _localizer = localizer ?? LocalizationService.Current;

    public bool TryApply(
        TrainerSettings settings,
        IReadOnlyDictionary<ResourceKind, ResourceSettingsInput> inputs,
        PopulationLimitSettingsInput populationLimit,
        out string message)
    {
        var parsed = new Dictionary<ResourceKind, (int Amount, string Hotkey)>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (inputs.Count != TrainerSettings.EnabledResources.Count ||
            TrainerSettings.EnabledResources.Any(kind => !inputs.ContainsKey(kind)))
        {
            message = _localizer.Get("Validation.IncompleteSettings");
            return false;
        }

        if (!int.TryParse(
                populationLimit.Limit,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedPopulationLimit) ||
            !PopulationLimitRules.IsValidLimit(parsedPopulationLimit))
        {
            message = _localizer.Get(
                "Validation.PopulationRange",
                PopulationLimitRules.MinimumLimit,
                PopulationLimitRules.MaximumLimit);
            return false;
        }

        foreach (var (kind, input) in inputs)
        {
            if (!int.TryParse(input.Amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
            {
                message = _localizer.Get("Validation.ValidAmount", ResourceName(kind));
                return false;
            }

            if (kind == ResourceKind.CommandPoints && !CommandPointRules.IsValidIncrement(amount))
            {
                message = _localizer.Get(
                    "Validation.CommandPointsRange",
                    CommandPointRules.MinimumIncrement,
                    CommandPointRules.MaximumPoints);
                return false;
            }

            if (kind != ResourceKind.CommandPoints && amount is < 1 or > 1_000_000)
            {
                message = _localizer.Get("Validation.ResourceRange", ResourceName(kind));
                return false;
            }

            if (!TrainerSettings.IsFunctionKey(input.Hotkey) || !selectedKeys.Add(input.Hotkey!))
            {
                message = _localizer.Get("Validation.DistinctKeys", inputs.Count);
                return false;
            }

            parsed[kind] = (amount, input.Hotkey!);
        }

        foreach (var (kind, value) in parsed)
        {
            settings.Resources[kind].Amount = value.Amount;
            settings.Resources[kind].Hotkey = value.Hotkey;
        }

        settings.PopulationLimit.Enabled = populationLimit.Enabled;
        settings.PopulationLimit.Limit = parsedPopulationLimit;

        message = string.Empty;
        return true;
    }

    private string ResourceName(ResourceKind kind) => _localizer.Get($"Resource.{kind}");
}
