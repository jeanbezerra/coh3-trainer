using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Coh3Trainer.Interop;
using Coh3Trainer.Localization;
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
    private readonly LocalizationService _localizer = LocalizationService.Current;
    private readonly DispatcherTimer _refreshTimer;
    private readonly MatchDashboardTracker _matchDashboard = new();
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
        _localizer.SetCulture(_settings.Culture);
        _backend.ConfigureIncomeMultiplier(_settings.IncomeMultiplier);
        _backend.ConfigurePopulationLimit(_settings.PopulationLimit.Enabled, _settings.PopulationLimit.Limit);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += RefreshTimer_Tick;
        PopulateSelectors();
        PopulateSettingsControls();
        UpdateHotkeyLabels();
        UpdateIncomeMultiplierStatus();
        UpdatePopulationLimitStatus();
        UpdateMatchDashboard(_matchDashboard.Current);
        UpdateLastUpdateText(_matchDashboard.Current);
        UpdateLanguageMenuChecks();
        _logger.Write(L("Log.Started"));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.EnableDarkTitleBar(handle);
        _hotkeys = new HotkeyService(handle, _localizer);
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
        _logger.Write(L("Log.Closed"));
    }

    private void LanguageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string cultureName } ||
            !LocalizationService.IsSupportedCulture(cultureName))
        {
            return;
        }

        _localizer.SetCulture(cultureName);
        _settings.Culture = _localizer.CultureName;
        UpdateLanguageMenuChecks();
        ApplyLocalizedState();
        var language = LocalizationService.SupportedLanguages.First(
            option => option.CultureName == _localizer.CultureName);
        try
        {
            _settingsStore.Save(_settings);
            ShowEvent(L("Event.LanguageChanged", language.DisplayName));
        }
        catch (Exception ex)
        {
            ShowEvent(L("Event.ErrorSaving", ex.Message));
        }
    }

    private void OpenSettingsFolderMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_applicationPaths.DataDirectory, L("Event.SettingsFolderOpened"));

    private void OpenLogsFolderMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(_applicationPaths.LogsDirectory, L("Event.LogsFolderOpened"));

    private void HelpMenuItem_Click(object sender, RoutedEventArgs e) =>
        ShowInformationDialog(
            L("Info.HelpSection"),
            L("Info.HelpHeading"),
            L("Info.HelpBody"));

    private void ContributorsMenuItem_Click(object sender, RoutedEventArgs e) =>
        ShowInformationDialog(
            L("Info.ContributorsSection"),
            "LordSteelHand",
            L("Info.ContributorsBody"));

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        ShowInformationDialog(
            L("Info.AboutSection"),
            L("App.Title"),
            L("Info.AboutBody", version));
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
            ShowEvent(L("Event.CannotOpenFolder", ex.Message));
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
            UpdateMatchDashboardFeatures();
            RegisterHotkeys();
            ShowEvent(L("Event.SettingsSaved", incomeResult.Message, populationResult.Message));
        }
        catch (Exception ex)
        {
            ShowEvent(L("Event.ErrorSaving", ex.Message));
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
            message = L("Validation.SelectIncome");
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
            ShowEvent(L("Event.GameDisconnected"));
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
        ShowEvent(L("Event.Connecting"));
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
            ShowEvent(L("Event.ConnectionFailed", ex.Message));
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

    private async void PlayerSquadActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy ||
            sender is not Button { Tag: string tag } ||
            !Enum.TryParse<PlayerSquadAction>(tag, out var action))
        {
            return;
        }

        _busy = true;
        UpdatePlayerSquadActionsState();
        try
        {
            ShowEvent(L("Event.UnitActionQueued"));
            var result = await _backend.ExecutePlayerSquadActionAsync(action);
            ShowEvent(result.Message);
        }
        catch (Exception ex)
        {
            ShowEvent(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdatePlayerSquadActionsState();
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
            var dashboardSnapshot = _matchDashboard.Update(DateTimeOffset.Now, values);
            UpdateMatchDashboard(dashboardSnapshot);
            UpdatePopulationLimitStatus();
            UpdateLastUpdateText(dashboardSnapshot);
        }
        catch (Exception ex)
        {
            ShowEvent(L("Event.ReadFailed", ex.Message));
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
        UpdatePlayerSquadActionsState();
        LastUpdateText.Text = L("Status.WaitingData");
        _matchDashboard.Reset();
        UpdateMatchDashboard(_matchDashboard.Current);
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
            ? L("Income.DefaultShort")
            : L("Income.MultiplierShort", _settings.IncomeMultiplier);
        UpdateMatchDashboardFeatures();
    }

    private void UpdatePopulationLimitStatus()
    {
        PopulationLimitStatusText.Text = _backend.PopulationLimitState switch
        {
            PopulationLimitState.Active => L("Population.ShortActive", _backend.PopulationLimit),
            PopulationLimitState.Disabled => L("Population.ShortDefault"),
            PopulationLimitState.Unsupported => L("Population.ShortUnavailable"),
            _ => L("Population.ShortWaiting", _backend.PopulationLimit)
        };
        UpdateMatchDashboardFeatures();
    }

    private void UpdateStatus()
    {
        StatusText.Text = _backend.StatusMessage;
        var showVersion = _backend.State.IsConnected() && _backend.GameVersion != "—";
        VersionText.Visibility = showVersion ? Visibility.Visible : Visibility.Collapsed;
        VersionText.Text = showVersion ? L("Dashboard.Version", _backend.GameVersion) : string.Empty;
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
        UpdatePlayerSquadActionsState();
        UpdateMatchDashboardConnectionState(_matchDashboard.Current);
    }

    private void UpdatePlayerSquadActionsState()
    {
        var available = !_busy &&
                        _backend.State == TrainerConnectionState.Ready &&
                        _backend.SupportsPlayerSquadActions;
        PromoteVeterancyButton.IsEnabled = available;
        HealAllButton.IsEnabled = available;
        ResetCooldownsButton.IsEnabled = available;

        SquadActionsAvailabilityText.Text = !_backend.State.IsConnected()
            ? L("Units.ConnectHint")
            : !_backend.SupportsPlayerSquadActions
                ? L("Units.Unsupported", _backend.GameVersion)
                : _backend.State != TrainerConnectionState.Ready
                    ? L("Units.WaitingMatch")
                    : _busy
                        ? L("Units.Executing")
                        : L("Units.Ready");
    }

    private void UpdateConnectionButton()
    {
        if (_connecting || _backend.State == TrainerConnectionState.Connecting)
        {
            ConnectButton.Content = L("Action.Connecting");
            ConnectButton.ToolTip = L("Tooltip.Connecting");
            ConnectButton.IsEnabled = false;
            return;
        }

        var connectedToGame = _backend.State.IsConnected();
        ConnectButton.IsEnabled = true;
        ConnectButton.Content = connectedToGame
            ? L("Action.Disconnect")
            : _backend.State == TrainerConnectionState.Error ? L("Action.Retry") : L("Action.Connect");
        ConnectButton.ToolTip = connectedToGame
            ? L("Tooltip.Disconnect")
            : L("Tooltip.Connect");
    }

    private void ShowEvent(string message)
    {
        EventText.Text = $"{DateTime.Now:HH:mm:ss}  {message}";
        EventText.Foreground = new SolidColorBrush(Colors.White);
        _logger.Write(message);
    }

    private static string FormatValue(double? value) => value.HasValue ? value.Value.ToString("N0") : "—";

    private static string FormatIncomeMultiplier(double multiplier) => $"{multiplier:0.#}x";

    private void UpdateMatchDashboard(MatchDashboardSnapshot snapshot)
    {
        MatchElapsedText.Text = $"{(int)snapshot.Elapsed.TotalHours:00}:{snapshot.Elapsed.Minutes:00}:{snapshot.Elapsed.Seconds:00}";
        MatchLastSampleText.Text = snapshot.LastUpdatedAt.HasValue
            ? L("Dashboard.LastSample", snapshot.LastUpdatedAt.Value.LocalDateTime)
            : L("Dashboard.LastSampleEmpty");

        UpdateMatchResource(
            MatchManpowerValueText,
            MatchManpowerRateText,
            snapshot,
            ResourceKind.Manpower);
        UpdateMatchResource(MatchFuelValueText, MatchFuelRateText, snapshot, ResourceKind.Fuel);
        UpdateMatchResource(MatchArmyValueText, MatchArmyRateText, snapshot, ResourceKind.Army);
        UpdateMatchResource(
            MatchCommandPointsValueText,
            MatchCommandPointsRateText,
            snapshot,
            ResourceKind.CommandPoints);
        UpdateMatchDashboardConnectionState(snapshot);
        UpdateMatchDashboardFeatures();
    }

    private void UpdateMatchDashboardConnectionState(MatchDashboardSnapshot snapshot)
    {
        MatchVersionText.Text = _backend.GameVersion == "—"
            ? L("Dashboard.VersionEmpty")
            : L("Dashboard.Version", _backend.GameVersion);

        if (!_backend.State.IsConnected())
        {
            MatchStateText.Text = L("Dashboard.StateDisconnected");
            MatchConnectionText.Text = L("Dashboard.WaitingConnection");
            return;
        }

        if (snapshot.HasLiveData)
        {
            MatchStateText.Text = L("Dashboard.StateInMatch");
            MatchConnectionText.Text = L("Dashboard.TelemetryActive");
            return;
        }

        MatchStateText.Text = snapshot.HasSession
            ? L("Dashboard.StateSyncing")
            : L("Dashboard.StateWaitingMatch");
        MatchConnectionText.Text = snapshot.HasSession
            ? L("Dashboard.TelemetryInterrupted")
            : L("Dashboard.EnterMatch");
    }

    private void UpdateMatchDashboardFeatures()
    {
        MatchIncomeMultiplierText.Text = _backend.IncomeMultiplier <= 1
            ? L("Dashboard.IncomeDefault")
            : L("Dashboard.IncomeActive", _backend.IncomeMultiplier);
        MatchPopulationLimitText.Text = _backend.PopulationLimitState switch
        {
            PopulationLimitState.Active => L("Dashboard.PopActive", _backend.PopulationLimit),
            PopulationLimitState.Disabled => L("Dashboard.PopDefault"),
            PopulationLimitState.Unsupported => L("Dashboard.PopUnsupported"),
            _ => _backend.PopulationLimitEnabled
                ? L("Dashboard.PopWaiting", _backend.PopulationLimit)
                : L("Dashboard.PopDefault")
        };
    }

    private void UpdateMatchResource(
        TextBlock valueText,
        TextBlock rateText,
        MatchDashboardSnapshot snapshot,
        ResourceKind resource)
    {
        snapshot.Values.TryGetValue(resource, out var value);
        snapshot.NetPerMinute.TryGetValue(resource, out var rate);
        valueText.Text = FormatValue(value);
        rateText.Text = L("Dashboard.NetChange", FormatRate(rate));
    }

    private static string FormatRate(double? rate) => rate.HasValue
        ? rate.Value.ToString("+0.##;-0.##;0")
        : "—";

    private void UpdateLastUpdateText(MatchDashboardSnapshot snapshot)
    {
        LastUpdateText.Text = snapshot.HasLiveData && snapshot.LastUpdatedAt.HasValue
            ? L("Status.Updated", snapshot.LastUpdatedAt.Value.LocalDateTime)
            : L("Status.WaitingData");
    }

    private void ApplyLocalizedState()
    {
        UpdateIncomeMultiplierStatus();
        UpdatePopulationLimitStatus();
        UpdateStatus();
        UpdatePlayerSquadActionsState();
        UpdateMatchDashboard(_matchDashboard.Current);
        UpdateLastUpdateText(_matchDashboard.Current);
    }

    private void UpdateLanguageMenuChecks()
    {
        foreach (var item in LanguageMenuItem.Items.OfType<MenuItem>())
        {
            item.IsChecked = string.Equals(
                item.Tag?.ToString(),
                _localizer.CultureName,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private string L(string key, params object[] arguments) => _localizer.Get(key, arguments);

    private static void UpdateResourceValue(TextBlock valueText, Button addButton, double? value)
    {
        valueText.Text = FormatValue(value);
        addButton.IsEnabled = value.HasValue;
    }
}
