using Coh3Trainer.Models;

namespace Coh3Trainer.Services;

public enum TrainerConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Ready,
    Error
}

public enum PopulationLimitState
{
    Disabled,
    WaitingForMatch,
    Active,
    Unsupported
}

public enum PlayerSquadAction
{
    PromoteVeterancy = 1,
    Heal = 2,
    ResetCooldowns = 3
}

public static class TrainerConnectionStateExtensions
{
    public static bool IsConnected(this TrainerConnectionState state) =>
        state is TrainerConnectionState.Connected or TrainerConnectionState.Ready;
}

public readonly record struct TrainerResult(bool Success, string Message)
{
    public static TrainerResult Ok(string message) => new(true, message);
    public static TrainerResult Fail(string message) => new(false, message);
}

public interface ITrainerBackend : IDisposable
{
    TrainerConnectionState State { get; }
    string StatusMessage { get; }
    string GameVersion { get; }
    double IncomeMultiplier { get; }
    bool PopulationLimitEnabled { get; }
    int PopulationLimit { get; }
    bool IsPopulationLimitApplied { get; }
    PopulationLimitState PopulationLimitState { get; }
    bool SupportsPlayerSquadActions { get; }
    Task<TrainerResult> ConnectAsync();
    Task<IReadOnlyDictionary<ResourceKind, double?>> ReadResourcesAsync();
    Task<TrainerResult> AddResourceAsync(ResourceKind resource, int amount);
    Task<TrainerResult> ExecutePlayerSquadActionAsync(PlayerSquadAction action);
    TrainerResult ConfigureIncomeMultiplier(double multiplier);
    TrainerResult ConfigurePopulationLimit(bool enabled, int limit);
    void Disconnect();
}
