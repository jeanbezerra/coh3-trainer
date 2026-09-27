using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Coh3Trainer.Interop;
using Coh3Trainer.Models;
using Coh3Trainer.Services;

namespace Coh3Trainer;

public partial class MainWindow : Window
{
    private static readonly string[] FunctionKeys = Enumerable.Range(1, 12).Select(x => $"F{x}").ToArray();
    private readonly ISettingsStore _settingsStore;
    private readonly ITrainerSettingsValidator _settingsValidator;
    private readonly ITrainerBackend _backend;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IAppLogger _logger;
    private readonly IShellService _shellService;
    private readonly DispatcherTimer _refreshTimer;
    private TrainerSettings _settings;
    private HotkeyService? _hotkeys;
    private bool _busy;
    private bool _connecting;

    public MainWindow() : this(new ApplicationPaths())
    {
    }

    private MainWindow(IApplicationPaths applicationPaths) : this(
        new SettingsStore(applicationPaths),
        new TrainerSettingsValidator(),
        new MemoryTrainerBackend(),
        applicationPaths,
        new FileAppLogger(applicationPaths),
        new WindowsShellService())
    {
    }

    internal MainWindow(
        ISettingsStore settingsStore,
        ITrainerSettingsValidator settingsValidator,
        ITrainerBackend backend,
        IApplicationPaths applicationPaths,
        IAppLogger logger,
        IShellService shellService)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _settingsValidator = settingsValidator;
        _backend = backend;
        _applicationPaths = applicationPaths;
        _logger = logger;
        _shellService = shellService;
        _settings = _settingsStore.Load();
        _backend.ConfigureIncomeMultiplier(_settings.IncomeMultiplier);
        _backend.ConfigurePopulationLimit(_settings.PopulationLimit.Enabled, _settings.PopulationLimit.Limit);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += RefreshTimer_Tick;
        PopulateSelectors();
        PopulateSettingsControls();
        UpdateHotkeyLabels();
        UpdateIncomeMultiplierStatus();
        UpdatePopulationLimitStatus();
        _logger.Write("Trainer iniciado.");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.EnableDarkTitleBar(handle);
        _hotkeys = new HotkeyService(handle);
        _hotkeys.Pressed += Hotkeys_Pressed;
        RegisterHotkeys();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await ConnectToGameAsync();
        _refreshTimer.Start();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _hotkeys?.Dispose();
        _backend.Dispose();
        _logger.Write("Trainer encerrado.");
    }

    private void CurrentLanguageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        PortugueseLanguageMenuItem.IsChecked = true;
        ShowEvent("Idioma atual: Português (Brasil).");
    }

    private void OpenSettingsFolderMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_applicationPaths.DataDirectory, "Pasta de configurações aberta.");

    private void OpenLogsFolderMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_applicationPaths.LogsDirectory, "Pasta de logs aberta.");

    private void HelpMenuItem_Click(object sender, RoutedEventArgs e) =>
        ShowInformationDialog(
            "AJUDA",
            "Como usar",
            "1. Abra o Company of Heroes 3.\n" +
            "2. Aguarde a conexão automática ou use Tentar novamente.\n" +
            "3. Entre em uma partida compatível.\n" +
            "4. Use os botões ou os atalhos configurados para adicionar recursos.\n" +
            "5. Command Points usam F9 e adicionam 5 pontos por padrão.\n" +
            "6. Ajuste o multiplicador de renda e o limite de população na guia Configuração.");

    private void ContributorsMenuItem_Click(object sender, RoutedEventArgs e) =>
        ShowInformationDialog(
            "COLABORADORES",
            "LordSteelHand",
            "Idealizador do CoH3 Resource Trainer e responsável pela visão do projeto.");

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        ShowInformationDialog(
            "SOBRE",
            "CoH3 Resource Trainer",
            $"Versão {version}\n\nFerramenta local para campanha e partidas solo ou privadas contra IA.");
    }

    private void OpenFolder(string path, string successMessage)
    {
        try
        {
            _shellService.OpenFolder(path);
            ShowEvent(successMessage);
        }
        catch (Exception ex)
        {
            ShowEvent($"Não foi possível abrir a pasta: {ex.Message}");
        }
    }

    private void ShowInformationDialog(string section, string heading, string body)
    {
        var dialog = new InformationDialog(section, heading, body)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void PopulateSelectors()
    {
        foreach (var combo in new[]
                 {
                     ManpowerHotkeyCombo,
                     FuelHotkeyCombo,
                     ArmyHotkeyCombo,
                     CommandPointsHotkeyCombo
                 })
        {
            combo.ItemsSource = FunctionKeys;
        }

        IncomeMultiplierCombo.ItemsSource = TrainerSettings.IncomeMultipliers
            .Select(FormatIncomeMultiplier)
            .ToArray();
    }

    private void PopulateSettingsControls()
    {
        SetResourceControls(ResourceKind.Manpower, ManpowerAmountTextBox, ManpowerHotkeyCombo);
        SetResourceControls(ResourceKind.Fuel, FuelAmountTextBox, FuelHotkeyCombo);
        SetResourceControls(ResourceKind.Army, ArmyAmountTextBox, ArmyHotkeyCombo);
        SetResourceControls(ResourceKind.CommandPoints, CommandPointsAmountTextBox, CommandPointsHotkeyCombo);
        IncomeMultiplierCombo.SelectedItem = FormatIncomeMultiplier(_settings.IncomeMultiplier);
        PopulationLimitEnabledCheckBox.IsChecked = _settings.PopulationLimit.Enabled;
        PopulationLimitTextBox.Text = _settings.PopulationLimit.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PopulationLimitTextBox.IsEnabled = _settings.PopulationLimit.Enabled;
    }

    private void SetResourceControls(ResourceKind kind, TextBox amount, ComboBox hotkey)
    {
        var item = _settings.Resources[kind];
        amount.Text = item.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        hotkey.SelectedItem = item.Hotkey;
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCollectSettings(out var validationMessage))
        {
            ShowEvent(validationMessage);
            return;
        }

        try
        {
            _settingsStore.Save(_settings);
            var incomeResult = _backend.ConfigureIncomeMultiplier(_settings.IncomeMultiplier);
            if (!incomeResult.Success)
            {
                throw new InvalidOperationException(incomeResult.Message);
            }

            var populationResult = _backend.ConfigurePopulationLimit(
                _settings.PopulationLimit.Enabled,
                _settings.PopulationLimit.Limit);
            if (!populationResult.Success)
            {
                throw new InvalidOperationException(populationResult.Message);
            }

            PopulateSettingsControls();
            UpdateHotkeyLabels();
            UpdateIncomeMultiplierStatus();
            UpdatePopulationLimitStatus();
            RegisterHotkeys();
            ShowEvent($"Configuração salva. {incomeResult.Message} {populationResult.Message}");
        }
        catch (Exception ex)
        {
            ShowEvent($"Erro ao salvar: {ex.Message}");
        }
    }

    private bool TryCollectSettings(out string message)
    {
        var incomeLabel = IncomeMultiplierCombo.SelectedItem?.ToString();
        if (incomeLabel is null ||
            !double.TryParse(
                incomeLabel.TrimEnd('x', 'X'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var incomeMultiplier) ||
            !TrainerSettings.IsIncomeMultiplierSupported(incomeMultiplier))
        {
            message = "Selecione um multiplicador de renda válido.";
            return false;
        }

        var inputs = new Dictionary<ResourceKind, ResourceSettingsInput>
        {
            [ResourceKind.Manpower] = new(ManpowerAmountTextBox.Text, ManpowerHotkeyCombo.SelectedItem?.ToString()),
            [ResourceKind.Fuel] = new(FuelAmountTextBox.Text, FuelHotkeyCombo.SelectedItem?.ToString()),
            [ResourceKind.Army] = new(ArmyAmountTextBox.Text, ArmyHotkeyCombo.SelectedItem?.ToString()),
            [ResourceKind.CommandPoints] = new(
                CommandPointsAmountTextBox.Text,
                CommandPointsHotkeyCombo.SelectedItem?.ToString())
        };
        var populationInput = new PopulationLimitSettingsInput(
            PopulationLimitEnabledCheckBox.IsChecked == true,
            PopulationLimitTextBox.Text);
        if (!_settingsValidator.TryApply(_settings, inputs, populationInput, out message))
        {
            return false;
        }

        _settings.IncomeMultiplier = incomeMultiplier;
        return true;
    }

    private void PopulationLimitEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        PopulationLimitTextBox.IsEnabled = PopulationLimitEnabledCheckBox.IsChecked == true;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!_backend.State.IsConnected())
        {
            await ConnectToGameAsync();
            return;
        }

        _busy = true;
        ConnectButton.IsEnabled = false;
        try
        {
            _backend.Disconnect();
            ClearResourceValues();
            ShowEvent("Jogo desconectado.");
            UpdateStatus();
        }
        finally
        {
            _busy = false;
            UpdateConnectionButton();
        }
    }

    private async Task ConnectToGameAsync()
    {
        if (_connecting)
        {
            return;
        }

        _busy = true;
        _connecting = true;
        ClearResourceValues();
        ShowEvent("Conectando ao jogo...");
        try
        {
            var connectionTask = _backend.ConnectAsync();
            UpdateStatus();
            var result = await connectionTask;
            ShowEvent(result.Message);
            UpdateStatus();
            if (result.Success)
            {
                await RefreshValuesAsync();
            }
        }
        catch (Exception ex)
        {
            _backend.Disconnect();
            ShowEvent($"Falha ao conectar: {ex.Message}");
            UpdateStatus();
        }
        finally
        {
            _connecting = false;
            _busy = false;
            UpdateConnectionButton();
        }
    }

    private async void AddResourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<ResourceKind>(tag, out var resource))
        {
            await AddResourceAsync(resource);
        }
    }

    private async void Hotkeys_Pressed(object? sender, ResourceKind resource)
    {
        await AddResourceAsync(resource);
    }

    private async Task AddResourceAsync(ResourceKind resource)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var amount = _settings.Resources[resource].Amount;
            var result = await _backend.AddResourceAsync(resource, amount);
            ShowEvent(result.Message);
            await RefreshValuesAsync();
        }
        catch (Exception ex)
        {
            ShowEvent(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!_busy)
        {
            await RefreshValuesAsync();
            UpdateStatus();
        }
    }

    private async Task RefreshValuesAsync()
    {
        try
        {
            var values = await _backend.ReadResourcesAsync();
            UpdateResourceValue(ManpowerValueText, ManpowerAddButton, values[ResourceKind.Manpower]);
            UpdateResourceValue(FuelValueText, FuelAddButton, values[ResourceKind.Fuel]);
            UpdateResourceValue(ArmyValueText, ArmyAddButton, values[ResourceKind.Army]);
            UpdateResourceValue(
                CommandPointsValueText,
                CommandPointsAddButton,
                values[ResourceKind.CommandPoints]);
            UpdatePopulationLimitStatus();
            LastUpdateText.Text = values
                .Where(pair => pair.Key != ResourceKind.VictoryPoints)
                .Any(pair => pair.Value.HasValue)
                ? $"Atualizado {DateTime.Now:HH:mm:ss.fff}"
                : "Aguardando dados";
        }
        catch (Exception ex)
        {
            ShowEvent($"Falha de leitura: {ex.Message}");
        }
    }

    private void ClearResourceValues()
    {
        ManpowerValueText.Text = "—";
        FuelValueText.Text = "—";
        ArmyValueText.Text = "—";
        CommandPointsValueText.Text = "—";
        ManpowerAddButton.IsEnabled = false;
        FuelAddButton.IsEnabled = false;
        ArmyAddButton.IsEnabled = false;
        CommandPointsAddButton.IsEnabled = false;
        LastUpdateText.Text = "Aguardando dados";
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null)
        {
            return;
        }

        var errors = _hotkeys.Register(_settings);
        if (errors.Count > 0)
        {
            ShowEvent(string.Join(" ", errors));
        }
    }

    private void UpdateHotkeyLabels()
    {
        ManpowerHotkeyText.Text = $"{_settings.Resources[ResourceKind.Manpower].Hotkey}  +{_settings.Resources[ResourceKind.Manpower].Amount:N0}";
        FuelHotkeyText.Text = $"{_settings.Resources[ResourceKind.Fuel].Hotkey}  +{_settings.Resources[ResourceKind.Fuel].Amount:N0}";
        ArmyHotkeyText.Text = $"{_settings.Resources[ResourceKind.Army].Hotkey}  +{_settings.Resources[ResourceKind.Army].Amount:N0}";
        CommandPointsHotkeyText.Text = $"{_settings.Resources[ResourceKind.CommandPoints].Hotkey}  +{_settings.Resources[ResourceKind.CommandPoints].Amount:N0}";
    }

    private void UpdateIncomeMultiplierStatus()
    {
        IncomeMultiplierStatusText.Text = _settings.IncomeMultiplier <= 1
            ? "RENDA PADRÃO"
            : $"RENDA {_settings.IncomeMultiplier:0.#}x";
    }

    private void UpdatePopulationLimitStatus()
    {
        PopulationLimitStatusText.Text = _backend.PopulationLimitState switch
        {
            PopulationLimitState.Active => $"POP {_backend.PopulationLimit:N0}",
            PopulationLimitState.Disabled => "POP PADRÃO",
            PopulationLimitState.Unsupported => "POP INDISPONÍVEL",
            _ => $"POP {_backend.PopulationLimit:N0} • AGUARDANDO"
        };
    }

    private void UpdateStatus()
    {
        StatusText.Text = _backend.StatusMessage;
        var showVersion = _backend.State.IsConnected() && _backend.GameVersion != "—";
        VersionText.Visibility = showVersion ? Visibility.Visible : Visibility.Collapsed;
        VersionText.Text = showVersion ? $"Versão {_backend.GameVersion}" : string.Empty;
        StatusIndicator.Fill = _backend.State switch
        {
            TrainerConnectionState.Ready => new SolidColorBrush(Color.FromRgb(34, 197, 94)),
            TrainerConnectionState.Connected => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            TrainerConnectionState.Connecting => new SolidColorBrush(Color.FromRgb(96, 165, 250)),
            TrainerConnectionState.Error => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            _ => new SolidColorBrush(Color.FromRgb(156, 163, 175))
        };
        UpdateConnectionButton();
        UpdatePopulationLimitStatus();
    }

    private void UpdateConnectionButton()
    {
        if (_connecting || _backend.State == TrainerConnectionState.Connecting)
        {
            ConnectButton.Content = "CONECTANDO...";
            ConnectButton.ToolTip = "Conexão em andamento";
            ConnectButton.IsEnabled = false;
            return;
        }

        var connectedToGame = _backend.State.IsConnected();
        ConnectButton.IsEnabled = true;
        ConnectButton.Content = connectedToGame
            ? "DESCONECTAR"
            : _backend.State == TrainerConnectionState.Error ? "TENTAR NOVAMENTE" : "CONECTAR";
        ConnectButton.ToolTip = connectedToGame
            ? "Encerrar a conexão com o processo do jogo"
            : "Localizar o Company of Heroes 3";
    }

    private void ShowEvent(string message)
    {
        EventText.Text = $"{DateTime.Now:HH:mm:ss}  {message}";
        EventText.Foreground = new SolidColorBrush(Colors.White);
        _logger.Write(message);
    }

    private static string FormatValue(double? value) => value.HasValue ? value.Value.ToString("N0") : "—";

    private static string FormatIncomeMultiplier(double multiplier) => $"{multiplier:0.#}x";

    private static void UpdateResourceValue(TextBlock valueText, Button addButton, double? value)
    {
        valueText.Text = FormatValue(value);
        addButton.IsEnabled = value.HasValue;
    }
}
