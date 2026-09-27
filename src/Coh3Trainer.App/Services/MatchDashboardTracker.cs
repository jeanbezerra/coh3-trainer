using Coh3Trainer.Models;

namespace Coh3Trainer.Services;

public sealed class MatchDashboardTracker
{
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinimumRateInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionLossTolerance = TimeSpan.FromSeconds(5);
    private readonly Queue<MatchSample> _samples = new();
    private DateTimeOffset? _sessionStartedAt;
    private DateTimeOffset? _lastLiveAt;

    public MatchDashboardSnapshot Current { get; private set; } = MatchDashboardSnapshot.Empty;

    public MatchDashboardSnapshot Update(
        DateTimeOffset capturedAt,
        IReadOnlyDictionary<ResourceKind, double?> values)
    {
        var currentValues = CopyValues(values);
        var hasLiveData = TrainerSettings.IncomeResources.All(
            resource => currentValues[resource].HasValue);

        if (!hasLiveData)
        {
            if (_lastLiveAt.HasValue && capturedAt - _lastLiveAt.Value > SessionLossTolerance)
            {
                Reset();
            }

            var elapsed = GetElapsed(_lastLiveAt);
            Current = new MatchDashboardSnapshot(
                false,
                _sessionStartedAt.HasValue,
                elapsed,
                _lastLiveAt,
                currentValues,
                CreateEmptyValues());
            return Current;
        }

        _sessionStartedAt ??= capturedAt;
        _lastLiveAt = capturedAt;
        _samples.Enqueue(new MatchSample(capturedAt, currentValues));
        RemoveExpiredSamples(capturedAt);

        Current = new MatchDashboardSnapshot(
            true,
            true,
            GetElapsed(capturedAt),
            capturedAt,
            currentValues,
            CalculateRates());
        return Current;
    }

    public void Reset()
    {
        _samples.Clear();
        _sessionStartedAt = null;
        _lastLiveAt = null;
        Current = MatchDashboardSnapshot.Empty;
    }

    private TimeSpan GetElapsed(DateTimeOffset? until)
    {
        if (!_sessionStartedAt.HasValue || !until.HasValue)
        {
            return TimeSpan.Zero;
        }

        return until.Value - _sessionStartedAt.Value;
    }

    private void RemoveExpiredSamples(DateTimeOffset capturedAt)
    {
        var oldestAllowed = capturedAt - SampleWindow;
        while (_samples.Count > 1 && _samples.Peek().CapturedAt < oldestAllowed)
        {
            _samples.Dequeue();
        }
    }

    private IReadOnlyDictionary<ResourceKind, double?> CalculateRates()
    {
        var rates = CreateEmptyValues();
        if (_samples.Count < 2)
        {
            return rates;
        }

        var first = _samples.Peek();
        var last = _samples.Last();
        var interval = last.CapturedAt - first.CapturedAt;
        if (interval < MinimumRateInterval)
        {
            return rates;
        }

        foreach (var resource in TrainerSettings.EnabledResources)
        {
            var firstValue = first.Values[resource];
            var lastValue = last.Values[resource];
            if (firstValue.HasValue && lastValue.HasValue)
            {
                rates[resource] = (lastValue.Value - firstValue.Value) / interval.TotalMinutes;
            }
        }

        return rates;
    }

    private static Dictionary<ResourceKind, double?> CopyValues(
        IReadOnlyDictionary<ResourceKind, double?> values)
    {
        var copy = CreateEmptyValues();
        foreach (var resource in TrainerSettings.EnabledResources)
        {
            if (values.TryGetValue(resource, out var value))
            {
                copy[resource] = value;
            }
        }

        return copy;
    }

    private static Dictionary<ResourceKind, double?> CreateEmptyValues() =>
        TrainerSettings.EnabledResources.ToDictionary(resource => resource, _ => (double?)null);

    private sealed record MatchSample(
        DateTimeOffset CapturedAt,
        IReadOnlyDictionary<ResourceKind, double?> Values);
}

public sealed record MatchDashboardSnapshot(
    bool HasLiveData,
    bool HasSession,
    TimeSpan Elapsed,
    DateTimeOffset? LastUpdatedAt,
    IReadOnlyDictionary<ResourceKind, double?> Values,
    IReadOnlyDictionary<ResourceKind, double?> NetPerMinute)
{
    public static MatchDashboardSnapshot Empty { get; } = new(
        false,
        false,
        TimeSpan.Zero,
        null,
        TrainerSettings.EnabledResources.ToDictionary(resource => resource, _ => (double?)null),
        TrainerSettings.EnabledResources.ToDictionary(resource => resource, _ => (double?)null));
}
