using System.Globalization;
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
    public bool TryApply(
        TrainerSettings settings,
        IReadOnlyDictionary<ResourceKind, ResourceSettingsInput> inputs,
        PopulationLimitSettingsInput populationLimit,
        out string message)
    {
        var parsed = new Dictionary<ResourceKind, (int Amount, string Hotkey)>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!int.TryParse(
                populationLimit.Limit,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedPopulationLimit) ||
            !PopulationLimitRules.IsValidLimit(parsedPopulationLimit))
        {
            message = $"O limite de população deve estar entre {PopulationLimitRules.MinimumLimit} e {PopulationLimitRules.MaximumLimit}.";
            return false;
        }

        foreach (var (kind, input) in inputs)
        {
            if (!int.TryParse(input.Amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
            {
                message = $"Informe uma quantidade válida para {kind.DisplayName()}.";
                return false;
            }

            if (kind == ResourceKind.CommandPoints && !CommandPointRules.IsValidIncrement(amount))
            {
                message = $"A quantidade de Command Points deve estar entre {CommandPointRules.MinimumIncrement} e {CommandPointRules.MaximumPoints}.";
                return false;
            }

            if (kind != ResourceKind.CommandPoints && amount is < 1 or > 1_000_000)
            {
                message = $"A quantidade de {kind.DisplayName()} deve estar entre 1 e 1.000.000.";
                return false;
            }

            if (!TrainerSettings.IsFunctionKey(input.Hotkey) || !selectedKeys.Add(input.Hotkey!))
            {
                message = $"Selecione {inputs.Count} teclas F diferentes.";
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
}
