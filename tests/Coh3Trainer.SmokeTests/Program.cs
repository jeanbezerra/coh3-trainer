using Coh3Trainer.Models;
using Coh3Trainer.Services;

var failures = new List<string>();

void Assert(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

var defaults = TrainerSettings.Default();
Assert(defaults.Resources[ResourceKind.Manpower].Hotkey == "F6", "Atalho padrão de Manpower");
Assert(defaults.Resources[ResourceKind.Fuel].Hotkey == "F7", "Atalho padrão de Fuel");
Assert(defaults.Resources[ResourceKind.Army].Hotkey == "F8", "Atalho padrão de Army");
Assert(defaults.Resources[ResourceKind.CommandPoints].Hotkey == "F9", "Atalho padrão de Command Points");
Assert(defaults.Resources[ResourceKind.CommandPoints].Amount == 5, "Incremento padrão de Command Points");
Assert(defaults.Resources[ResourceKind.VictoryPoints].Hotkey == "F10", "Atalho reservado de Victory Points");
Assert(defaults.IncomeMultiplier == 1, "Multiplicador de renda padrão");
Assert(defaults.PopulationLimit.Enabled, "Limite de população ativo por padrão");
Assert(defaults.PopulationLimit.Limit == 250, "Limite de população padrão");
Assert(defaults.Resources.Count == 5, "Compatibilidade da configuração com os cinco recursos conhecidos");
Assert(defaults.Resources.Keys.Count(TrainerSettings.EnabledResources.Contains) == 4, "Somente recursos estáveis estão habilitados");
Assert(TrainerSettings.IncomeResources.Count == 3, "Command Points não participa do multiplicador de renda");

var validator = new TrainerSettingsValidator();
var validInputs = new Dictionary<ResourceKind, ResourceSettingsInput>
{
    [ResourceKind.Manpower] = new("1500", "F3"),
    [ResourceKind.Fuel] = new("750", "F4"),
    [ResourceKind.Army] = new("600", "F5"),
    [ResourceKind.CommandPoints] = new("5", "F6")
};
Assert(
    validator.TryApply(defaults, validInputs, new PopulationLimitSettingsInput(true, "300"), out _),
    "Validação de configuração válida");
Assert(defaults.Resources[ResourceKind.Manpower].Amount == 1500, "Aplicação da quantidade validada");
Assert(defaults.PopulationLimit.Limit == 300, "Aplicação do limite de população validado");

var amountBeforeInvalidInput = defaults.Resources[ResourceKind.Manpower].Amount;
var invalidInputs = new Dictionary<ResourceKind, ResourceSettingsInput>
{
    [ResourceKind.Manpower] = new("2000", "F3"),
    [ResourceKind.Fuel] = new("750", "F3"),
    [ResourceKind.Army] = new("inválido", "F5"),
    [ResourceKind.CommandPoints] = new("5", "F6")
};
Assert(
    !validator.TryApply(defaults, invalidInputs, new PopulationLimitSettingsInput(true, "300"), out _),
    "Rejeição de configuração inválida");
Assert(
    defaults.Resources[ResourceKind.Manpower].Amount == amountBeforeInvalidInput,
    "Validação inválida não altera parcialmente a configuração");
Assert(
    !validator.TryApply(defaults, validInputs, new PopulationLimitSettingsInput(true, "99"), out _),
    "Rejeição de limite de população fora da faixa");
var invalidCommandPointInputs = new Dictionary<ResourceKind, ResourceSettingsInput>(validInputs)
{
    [ResourceKind.CommandPoints] = new("33", "F6")
};
Assert(
    !validator.TryApply(defaults, invalidCommandPointInputs, new PopulationLimitSettingsInput(true, "300"), out _),
    "Rejeição de incremento de Command Points fora da faixa");
Assert(CommandPointRules.IsPlausibleValue(32), "Valor máximo de Command Points aceito");
Assert(!CommandPointRules.IsPlausibleValue(33), "Valor excessivo de Command Points rejeitado");
Assert(
    PlayerCommandPointLayout.IsSupportedVersion("5.1.50313.0"),
    "Versão validada para Command Points");
Assert(PlayerCommandPointLayout.CurrentValueOffset == 0x6A4, "Offset corrente de Command Points");
Assert(PlayerCommandPointLayout.ScarValueOffset == 0x188, "Offset SCAR de Command Points");

var populationOverride = PlayerPopulationLayout.CreateOverride(500);
Assert(populationOverride.Length == 12, "Layout completo do override de população");
Assert(BitConverter.ToSingle(populationOverride, 0) == 500, "Override da população de pessoal");
Assert(BitConverter.ToSingle(populationOverride, 4) == float.MaxValue, "Sentinela da população secundária");
Assert(BitConverter.ToSingle(populationOverride, 8) == float.MaxValue, "Sentinela da população terciária");
Assert(PlayerPopulationLayout.IsPlausibleOverride(populationOverride), "Validação do layout de população");
Assert(
    PlayerPopulationLayout.IsSupportedVersion("5.1.50313.0"),
    "Versão validada para limite de população");

Assert(TrainerConnectionState.Connected.IsConnected(), "Estado conectado reconhecido");
Assert(TrainerConnectionState.Ready.IsConnected(), "Estado pronto reconhecido");
Assert(!TrainerConnectionState.Error.IsConnected(), "Estado de erro não é tratado como conectado");

var incomeTracker = new IncomeMultiplierTracker();
Assert(incomeTracker.Configure(2), "Configuração do multiplicador 2x");
Assert(
    !incomeTracker.TryCalculateBonus(ResourceKind.Manpower, 100, out _),
    "Primeira leitura apenas estabelece a referência de renda");
Assert(
    incomeTracker.TryCalculateBonus(ResourceKind.Manpower, 101, out var incomeBonus) &&
    Math.Abs(incomeBonus - 1) < 0.001,
    "Cálculo do bônus de renda 2x");
incomeTracker.Confirm(ResourceKind.Manpower, 102);
Assert(
    !incomeTracker.TryCalculateBonus(ResourceKind.Manpower, 90, out _),
    "Gastos não geram bônus de renda");
Assert(
    !incomeTracker.TryCalculateBonus(ResourceKind.CommandPoints, 5, out _),
    "Command Points não são tratados como renda");
Assert(!incomeTracker.Configure(4), "Multiplicadores não suportados são rejeitados");

var testRoot = Path.Combine(Path.GetTempPath(), "Coh3TrainerSmokeTests", Guid.NewGuid().ToString("N"));
try
{
    var paths = new ApplicationPaths(testRoot);
    var store = new SettingsStore(paths);
    defaults.Resources[ResourceKind.Fuel].Amount = 321;
    defaults.Resources[ResourceKind.CommandPoints].Amount = 7;
    defaults.IncomeMultiplier = 3;
    defaults.PopulationLimit.Enabled = true;
    defaults.PopulationLimit.Limit = 500;
    store.Save(defaults);
    var loaded = store.Load();
    Assert(loaded.Resources[ResourceKind.Fuel].Amount == 321, "Persistência das configurações");
    Assert(loaded.Resources[ResourceKind.CommandPoints].Amount == 7, "Persistência de Command Points");
    Assert(loaded.IncomeMultiplier == 3, "Persistência do multiplicador de renda");
    Assert(loaded.PopulationLimit.Enabled, "Persistência da ativação do limite de população");
    Assert(loaded.PopulationLimit.Limit == 500, "Persistência do limite de população");

    var logger = new FileAppLogger(paths);
    logger.Write("evento de teste");
    var logFiles = Directory.GetFiles(paths.LogsDirectory, "trainer-*.log");
    Assert(logFiles.Length == 1, "Criação do arquivo diário de log");
    Assert(File.ReadAllText(logFiles[0]).Contains("evento de teste"), "Persistência do evento no log");
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, true);
    }
}

if (args.Contains("--game-integration", StringComparer.OrdinalIgnoreCase))
{
    using var live = new MemoryTrainerBackend();
    var connected = await live.ConnectAsync();
    Assert(connected.Success, $"Conexão somente leitura com o jogo: {connected.Message}");
    if (connected.Success)
    {
        IReadOnlyDictionary<ResourceKind, double?> values = new Dictionary<ResourceKind, double?>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            values = await live.ReadResourcesAsync();
            var economicValuesAvailable = new[]
                {
                    values[ResourceKind.Manpower],
                    values[ResourceKind.Fuel],
                    values[ResourceKind.Army]
                }.All(value => value.HasValue);
            if (economicValuesAvailable)
            {
                break;
            }

            await Task.Delay(250);
        }

        var economicValues = new[]
        {
            values[ResourceKind.Manpower],
            values[ResourceKind.Fuel],
            values[ResourceKind.Army]
        };
        Assert(values.Count == 5, "Backend real retorna os cinco slots de recurso");
        Assert(
            economicValues.All(value => value.HasValue) || economicValues.All(value => !value.HasValue),
            "Backend real não retorna uma captura econômica parcial");
        Console.WriteLine($"Integração: {connected.Message}");
        Console.WriteLine(economicValues.All(value => value.HasValue)
            ? $"Manpower={values[ResourceKind.Manpower]:N0}; Fuel={values[ResourceKind.Fuel]:N0}; Munições={values[ResourceKind.Army]:N0}"
            : "Recursos econômicos=aguardando entrar em uma partida");
        Console.WriteLine(values[ResourceKind.VictoryPoints] is { } victoryPoints
            ? $"Victory Points={victoryPoints:N0}"
            : "Victory Points=indisponível no modo atual");
        Console.WriteLine(values[ResourceKind.CommandPoints] is { } commandPoints
            ? $"Command Points={commandPoints:N0}"
            : "Command Points=aguardando uma versão ou partida compatível");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures.Select(x => $"FALHOU: {x}")));
    return 1;
}

Console.WriteLine("Smoke tests concluídos com sucesso.");
return 0;
