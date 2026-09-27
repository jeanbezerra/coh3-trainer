using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Coh3Trainer.Interop;
using Coh3Trainer.Models;
using Microsoft.Win32.SafeHandles;

namespace Coh3Trainer.Services;

public sealed class MemoryTrainerBackend : ITrainerBackend
{
    private const uint RequiredAccess = NativeMethods.ProcessQueryLimitedInformation |
                                        NativeMethods.ProcessVmRead |
                                        NativeMethods.ProcessVmWrite |
                                        NativeMethods.ProcessVmOperation;
    private const int LocalPlayerFlagOffset = 0x682;
    private const int FuelOffset = 0x6A8;
    private const int ManpowerOffset = 0x6AC;
    private const int MunitionsOffset = 0x6B0;
    private const double MaximumResourceValue = 999_999;

    private readonly ProfileRepository _profiles;
    private readonly IncomeMultiplierTracker _incomeTracker;
    private readonly SemaphoreSlim _memoryLock = new(1, 1);
    private readonly object _populationConfigurationLock = new();
    private readonly Dictionary<ResourceKind, BoundResource> _resources = new();
    private Process? _process;
    private SafeProcessHandle? _handle;
    private PlayerSignatureLocator? _automaticLocator;
    private bool _populationLimitEnabled = true;
    private int _populationLimit = PopulationLimitRules.DefaultLimit;
    private nint _populationPlayerAddress;
    private byte[]? _originalPopulationOverride;
    private bool _populationOverrideWasApplied;

    public MemoryTrainerBackend(
        ProfileRepository? profiles = null,
        IncomeMultiplierTracker? incomeTracker = null)
    {
        _profiles = profiles ?? new ProfileRepository();
        _incomeTracker = incomeTracker ?? new IncomeMultiplierTracker();
    }

    public TrainerConnectionState State { get; private set; } = TrainerConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Jogo não conectado.";
    public string GameVersion { get; private set; } = "—";
    public double IncomeMultiplier => _incomeTracker.Multiplier;
    public bool PopulationLimitEnabled
    {
        get
        {
            lock (_populationConfigurationLock)
            {
                return _populationLimitEnabled;
            }
        }
    }

    public int PopulationLimit
    {
        get
        {
            lock (_populationConfigurationLock)
            {
                return _populationLimit;
            }
        }
    }

    public bool IsPopulationLimitApplied
    {
        get
        {
            lock (_populationConfigurationLock)
            {
                return _populationLimitEnabled && _populationOverrideWasApplied;
            }
        }
    }

    public PopulationLimitState PopulationLimitState
    {
        get
        {
            lock (_populationConfigurationLock)
            {
                if (!_populationLimitEnabled)
                {
                    return PopulationLimitState.Disabled;
                }

                if (GameVersion != "—" && !PlayerPopulationLayout.IsSupportedVersion(GameVersion))
                {
                    return PopulationLimitState.Unsupported;
                }

                return _populationOverrideWasApplied
                    ? PopulationLimitState.Active
                    : PopulationLimitState.WaitingForMatch;
            }
        }
    }

    public bool HasProfile { get; private set; }

    public Task<TrainerResult> ConnectAsync()
    {
        Disconnect();
        State = TrainerConnectionState.Connecting;
        StatusMessage = "Localizando o Company of Heroes 3...";
        return Task.Run(ConnectCore);
    }

    private TrainerResult ConnectCore()
    {
        var processes = Process.GetProcessesByName("RelicCoH3");
        if (processes.Length == 0)
        {
            State = TrainerConnectionState.Error;
            StatusMessage = "Company of Heroes 3 não encontrado. Abra o jogo e tente novamente.";
            return TrainerResult.Fail(StatusMessage);
        }

        _process = SelectGameProcess(processes);
        foreach (var extra in processes.Where(x => x.Id != _process.Id))
        {
            extra.Dispose();
        }

        try
        {
            var mainModule = _process.MainModule
                ?? throw new InvalidOperationException("O módulo principal do jogo não está disponível.");
            GameVersion = mainModule.FileVersionInfo.FileVersion ?? "desconhecida";
            _handle = NativeMethods.OpenProcess(RequiredAccess, false, _process.Id);
            if (_handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var profile = _profiles.Find(_process.ProcessName, GameVersion);
            HasProfile = profile is not null;
            if (profile is not null)
            {
                BindProfile(profile);
                State = TrainerConnectionState.Ready;
                StatusMessage = "Pronto — recursos disponíveis.";
                return TrainerResult.Ok(StatusMessage);
            }

            _automaticLocator = new PlayerSignatureLocator(_process, _handle, mainModule);
            var installation = _automaticLocator.Install();
            if (!installation.Success)
            {
                throw new InvalidOperationException(installation.Message);
            }

            State = TrainerConnectionState.Connected;
            StatusMessage = "Jogo conectado — aguardando uma partida.";
            TryBindAutomaticResources();
            return TrainerResult.Ok(StatusMessage);
        }
        catch (Exception ex)
        {
            Disconnect();
            State = TrainerConnectionState.Error;
            StatusMessage = $"Falha na conexão: {ex.Message}";
            return TrainerResult.Fail(StatusMessage);
        }
    }

    public async Task<IReadOnlyDictionary<ResourceKind, double?>> ReadResourcesAsync()
    {
        var result = TrainerSettings.AllResources.ToDictionary(x => x, _ => (double?)null);
        if (!IsReady())
        {
            MarkProcessEndedIfNecessary();
            return result;
        }

        await _memoryLock.WaitAsync();
        try
        {
            TryBindAutomaticResources();
            foreach (var (kind, bound) in _resources)
            {
                if (TryReadBoundNumber(bound, out var value) &&
                    value >= bound.Minimum && value <= bound.Maximum)
                {
                    result[kind] = value;
                }
            }

            ApplyIncomeMultiplier(result);
        }
        finally
        {
            _memoryLock.Release();
        }

        return result;
    }

    public async Task<TrainerResult> AddResourceAsync(ResourceKind resource, int amount)
    {
        if (!IsReady())
        {
            MarkProcessEndedIfNecessary();
            return TrainerResult.Fail("O jogo não está conectado.");
        }

        await _memoryLock.WaitAsync();
        try
        {
            TryBindAutomaticResources();
            if (!_resources.TryGetValue(resource, out var bound))
            {
                return TrainerResult.Fail(
                    $"{resource.DisplayName()} ainda não foi identificado. Entre em uma partida e tente novamente.");
            }

            if (!TryReadBoundNumber(bound, out var current) ||
                current < bound.Minimum || current > bound.Maximum)
            {
                return TrainerResult.Fail("Leitura fora da faixa segura; escrita cancelada.");
            }

            var desired = Math.Clamp(current + amount, bound.Minimum, bound.Maximum);
            if (!TryWriteBoundNumber(bound, desired, out var confirmed))
            {
                return TrainerResult.Fail($"Falha ao escrever: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }

            if (Math.Abs(confirmed - desired) > (bound.ValueType == ResourceValueType.Single ? 0.1 : 0))
            {
                return TrainerResult.Fail("O jogo não confirmou o novo valor.");
            }

            _incomeTracker.Confirm(resource, confirmed);
            var applied = confirmed - current;
            return applied <= (bound.ValueType == ResourceValueType.Single ? 0.1 : 0)
                ? TrainerResult.Ok($"{resource.DisplayName()} já está no limite de {confirmed:N0}.")
                : TrainerResult.Ok($"+{applied:N0} em {resource.DisplayName()}; novo valor: {confirmed:N0}.");
        }
        finally
        {
            _memoryLock.Release();
        }
    }

    public TrainerResult ConfigureIncomeMultiplier(double multiplier)
    {
        if (!_incomeTracker.Configure(multiplier))
        {
            return TrainerResult.Fail("Selecione um multiplicador de renda válido.");
        }

        return TrainerResult.Ok(multiplier <= 1
            ? "Renda normal restaurada."
            : $"Multiplicador de renda {multiplier:0.#}x ativado.");
    }

    public TrainerResult ConfigurePopulationLimit(bool enabled, int limit)
    {
        if (!PopulationLimitRules.IsValidLimit(limit))
        {
            return TrainerResult.Fail(
                $"O limite de população deve estar entre {PopulationLimitRules.MinimumLimit} e {PopulationLimitRules.MaximumLimit}.");
        }

        lock (_populationConfigurationLock)
        {
            _populationLimitEnabled = enabled;
            _populationLimit = limit;
        }

        if (!enabled)
        {
            return TrainerResult.Ok("Limite de população padrão selecionado.");
        }

        return PopulationLimitState == Services.PopulationLimitState.Unsupported
            ? TrainerResult.Ok($"Limite salvo, mas ainda não compatível com a versão {GameVersion}.")
            : TrainerResult.Ok($"Limite de população configurado em {limit:N0}.");
    }

    public void Disconnect()
    {
        _resources.Clear();
        _incomeTracker.ResetObservations();
        ResetPopulationSession();
        HasProfile = false;
        _automaticLocator?.Dispose();
        _automaticLocator = null;
        _handle?.Dispose();
        _handle = null;
        _process?.Dispose();
        _process = null;
        State = TrainerConnectionState.Disconnected;
        StatusMessage = "Jogo não conectado.";
        GameVersion = "—";
    }

    private static Process SelectGameProcess(IEnumerable<Process> processes) => processes
        .OrderByDescending(HasVisibleWindow)
        .ThenByDescending(GetWorkingSetSafely)
        .First();

    private static bool HasVisibleWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != nint.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static long GetWorkingSetSafely(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    private void MarkProcessEndedIfNecessary()
    {
        if (!State.IsConnected())
        {
            return;
        }

        Disconnect();
        StatusMessage = "O jogo foi fechado. Conecte novamente quando estiver pronto.";
    }

    private void TryBindAutomaticResources()
    {
        if (_automaticLocator is null)
        {
            return;
        }

        var identifiedAnyResource = false;
        if (_automaticLocator.TryGetPlayerAddress(out var playerAddress) &&
            playerAddress.ToInt64() >= 0x10000 &&
            TryReadByte(playerAddress + LocalPlayerFlagOffset, out var localPlayerFlag) &&
            localPlayerFlag == 0)
        {
            var fuel = playerAddress + FuelOffset;
            var manpower = playerAddress + ManpowerOffset;
            var munitions = playerAddress + MunitionsOffset;
            if (IsPlausibleResource(fuel) &&
                IsPlausibleResource(manpower) &&
                IsPlausibleResource(munitions))
            {
                _resources[ResourceKind.Fuel] = CreateAutomaticResource(fuel);
                _resources[ResourceKind.Manpower] = CreateAutomaticResource(manpower);
                _resources[ResourceKind.Army] = CreateAutomaticResource(munitions);
                TryBindCommandPoints(playerAddress);
                SynchronizePopulationLimit(playerAddress);
                identifiedAnyResource = true;
            }
        }

        if (identifiedAnyResource)
        {
            State = TrainerConnectionState.Ready;
            StatusMessage = "Pronto — recursos disponíveis.";
        }
    }

    private bool IsPlausibleResource(nint address) =>
        TryReadNumber(address, ResourceValueType.Single, out var value) &&
        value is >= 0 and <= MaximumResourceValue;

    private static BoundResource CreateAutomaticResource(nint address) =>
        new(address, ResourceValueType.Single, 0, MaximumResourceValue);

    private void TryBindCommandPoints(nint playerAddress)
    {
        if (!PlayerCommandPointLayout.IsSupportedVersion(GameVersion))
        {
            _resources.Remove(ResourceKind.CommandPoints);
            return;
        }

        var currentAddress = playerAddress + PlayerCommandPointLayout.CurrentValueOffset;
        var scarAddress = playerAddress + PlayerCommandPointLayout.ScarValueOffset;
        if (!TryReadNumber(currentAddress, ResourceValueType.Single, out var current) ||
            !TryReadNumber(scarAddress, ResourceValueType.Single, out var scarValue) ||
            !CommandPointRules.IsPlausibleValue(current) ||
            !CommandPointRules.IsPlausibleValue(scarValue) ||
            Math.Abs(current - scarValue) > 0.1)
        {
            _resources.Remove(ResourceKind.CommandPoints);
            return;
        }

        _resources[ResourceKind.CommandPoints] = new BoundResource(
            currentAddress,
            ResourceValueType.Single,
            0,
            CommandPointRules.MaximumPoints,
            scarAddress);
    }

    private void SynchronizePopulationLimit(nint playerAddress)
    {
        if (!PlayerPopulationLayout.IsSupportedVersion(GameVersion))
        {
            return;
        }

        var overrideAddress = playerAddress + PlayerPopulationLayout.OverrideOffset;
        if (_populationPlayerAddress != playerAddress)
        {
            if (!TryReadBytes(overrideAddress, PlayerPopulationLayout.OverrideSize, out var original) ||
                !PlayerPopulationLayout.IsPlausibleOverride(original))
            {
                ResetPopulationSession();
                return;
            }

            lock (_populationConfigurationLock)
            {
                _populationPlayerAddress = playerAddress;
                _originalPopulationOverride = original;
                _populationOverrideWasApplied = false;
            }
        }

        bool enabled;
        int configuredLimit;
        bool wasApplied;
        byte[]? originalOverride;
        lock (_populationConfigurationLock)
        {
            enabled = _populationLimitEnabled;
            configuredLimit = _populationLimit;
            wasApplied = _populationOverrideWasApplied;
            originalOverride = _originalPopulationOverride;
        }

        if (!TryReadBytes(overrideAddress, PlayerPopulationLayout.OverrideSize, out var current))
        {
            return;
        }

        if (enabled)
        {
            var desired = PlayerPopulationLayout.CreateOverride(configuredLimit);
            if (!PlayerPopulationLayout.Matches(current, desired) &&
                (!TryWriteBytes(overrideAddress, desired) ||
                 !TryReadBytes(overrideAddress, PlayerPopulationLayout.OverrideSize, out current) ||
                 !PlayerPopulationLayout.Matches(current, desired)))
            {
                lock (_populationConfigurationLock)
                {
                    _populationOverrideWasApplied = false;
                }
                return;
            }

            lock (_populationConfigurationLock)
            {
                _populationOverrideWasApplied = true;
            }
            return;
        }

        if (!wasApplied || originalOverride is null)
        {
            return;
        }

        if (PlayerPopulationLayout.Matches(current, originalOverride) ||
            (TryWriteBytes(overrideAddress, originalOverride) &&
             TryReadBytes(overrideAddress, PlayerPopulationLayout.OverrideSize, out current) &&
             PlayerPopulationLayout.Matches(current, originalOverride)))
        {
            lock (_populationConfigurationLock)
            {
                _populationOverrideWasApplied = false;
            }
        }
    }

    private void ResetPopulationSession()
    {
        lock (_populationConfigurationLock)
        {
            _populationPlayerAddress = nint.Zero;
            _originalPopulationOverride = null;
            _populationOverrideWasApplied = false;
        }
    }

    private void ApplyIncomeMultiplier(Dictionary<ResourceKind, double?> values)
    {
        foreach (var resource in TrainerSettings.IncomeResources)
        {
            if (values[resource] is not { } current ||
                !_resources.TryGetValue(resource, out var bound))
            {
                _incomeTracker.Forget(resource);
                continue;
            }

            if (!_incomeTracker.TryCalculateBonus(resource, current, out var bonus))
            {
                continue;
            }

            var desired = Math.Clamp(current + bonus, bound.Minimum, bound.Maximum);
            if (!TryWriteBoundNumber(bound, desired, out var confirmed) ||
                confirmed < bound.Minimum || confirmed > bound.Maximum)
            {
                _incomeTracker.Confirm(resource, current);
                continue;
            }

            values[resource] = confirmed;
            _incomeTracker.Confirm(resource, confirmed);
        }
    }

    private void BindProfile(GameProfile profile)
    {
        var module = _process!.Modules.Cast<ProcessModule>()
            .FirstOrDefault(x => string.Equals(x.ModuleName, profile.ModuleName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Módulo {profile.ModuleName} não encontrado.");

        foreach (var (kind, locator) in profile.Resources)
        {
            var address = checked(module.BaseAddress + (nint)ParseHex(locator.BaseOffset));
            foreach (var pointerOffset in locator.PointerOffsets)
            {
                if (!TryReadPointer(address, out var pointer) || pointer == nint.Zero)
                {
                    throw new InvalidOperationException($"Cadeia de ponteiros inválida para {kind.DisplayName()}.");
                }
                address = checked(pointer + (nint)ParseHex(pointerOffset));
            }

            if (!TryReadNumber(address, locator.ValueType, out var current) ||
                current < locator.Minimum || current > locator.Maximum)
            {
                throw new InvalidOperationException($"Valor inicial inválido para {kind.DisplayName()}.");
            }

            _resources[kind] = new BoundResource(address, locator.ValueType, locator.Minimum, locator.Maximum);
        }
    }

    private bool TryReadByte(nint address, out byte value)
    {
        var buffer = new byte[1];
        if (NativeMethods.ReadProcessMemory(_handle!, address, buffer, 1, out var bytesRead) && bytesRead == 1)
        {
            value = buffer[0];
            return true;
        }

        value = 0;
        return false;
    }

    private bool TryReadBytes(nint address, int size, out byte[] buffer)
    {
        buffer = new byte[size];
        return NativeMethods.ReadProcessMemory(_handle!, address, buffer, (nuint)size, out var bytesRead) &&
               bytesRead == (nuint)size;
    }

    private bool TryReadPointer(nint address, out nint value)
    {
        var buffer = new byte[8];
        if (NativeMethods.ReadProcessMemory(_handle!, address, buffer, 8, out var bytesRead) && bytesRead == 8)
        {
            value = (nint)BitConverter.ToInt64(buffer);
            return true;
        }

        value = nint.Zero;
        return false;
    }

    private bool TryReadNumber(nint address, ResourceValueType valueType, out double value)
    {
        var buffer = new byte[4];
        if (NativeMethods.ReadProcessMemory(_handle!, address, buffer, 4, out var bytesRead) && bytesRead == 4)
        {
            value = valueType == ResourceValueType.Int32 ? BitConverter.ToInt32(buffer) : BitConverter.ToSingle(buffer);
            return double.IsFinite(value);
        }

        value = 0;
        return false;
    }

    private bool TryReadBoundNumber(BoundResource bound, out double value) =>
        TryReadNumber(bound.Address, bound.ValueType, out value);

    private bool TryWriteNumber(nint address, ResourceValueType valueType, double value)
    {
        var buffer = valueType == ResourceValueType.Int32
            ? BitConverter.GetBytes(checked((int)Math.Round(value)))
            : BitConverter.GetBytes(checked((float)value));
        return NativeMethods.WriteProcessMemory(_handle!, address, buffer, 4, out var bytesWritten) && bytesWritten == 4;
    }

    private bool TryWriteBoundNumber(BoundResource bound, double value, out double confirmed)
    {
        confirmed = 0;
        if (!TryReadNumber(bound.Address, bound.ValueType, out var original))
        {
            return false;
        }

        double? originalMirror = null;
        if (bound.MirrorAddress != nint.Zero)
        {
            if (!TryReadNumber(bound.MirrorAddress, bound.ValueType, out var mirror))
            {
                return false;
            }
            originalMirror = mirror;
        }

        var primaryWritten = TryWriteNumber(bound.Address, bound.ValueType, value);
        var mirrorWritten = bound.MirrorAddress == nint.Zero ||
                            TryWriteNumber(bound.MirrorAddress, bound.ValueType, value);
        var tolerance = bound.ValueType == ResourceValueType.Single ? 0.1 : 0;
        var primaryConfirmed = primaryWritten &&
                               TryReadNumber(bound.Address, bound.ValueType, out confirmed) &&
                               Math.Abs(confirmed - value) <= tolerance;
        var mirrorConfirmed = mirrorWritten &&
                              (bound.MirrorAddress == nint.Zero ||
                               (TryReadNumber(bound.MirrorAddress, bound.ValueType, out var confirmedMirror) &&
                                Math.Abs(confirmedMirror - value) <= tolerance));
        if (primaryConfirmed && mirrorConfirmed)
        {
            return true;
        }

        TryWriteNumber(bound.Address, bound.ValueType, original);
        if (bound.MirrorAddress != nint.Zero && originalMirror.HasValue)
        {
            TryWriteNumber(bound.MirrorAddress, bound.ValueType, originalMirror.Value);
        }
        confirmed = original;
        return false;
    }

    private bool TryWriteBytes(nint address, byte[] buffer) =>
        NativeMethods.WriteProcessMemory(_handle!, address, buffer, (nuint)buffer.Length, out var bytesWritten) &&
        bytesWritten == (nuint)buffer.Length;

    private bool IsReady()
    {
        if (!State.IsConnected() ||
            _handle is not { IsInvalid: false, IsClosed: false } ||
            _process is null)
        {
            return false;
        }

        try
        {
            return !_process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static long ParseHex(string value)
    {
        var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return Convert.ToInt64(normalized, 16);
    }

    public void Dispose()
    {
        Disconnect();
        _memoryLock.Dispose();
    }

    private readonly record struct BoundResource(
        nint Address,
        ResourceValueType ValueType,
        double Minimum,
        double Maximum,
        nint MirrorAddress = default);
}
