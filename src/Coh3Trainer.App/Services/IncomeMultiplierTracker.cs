using Coh3Trainer.Models;

namespace Coh3Trainer.Services;

public sealed class IncomeMultiplierTracker
{
    private const double MaximumObservedIncrease = 25;
    private const double MinimumObservedIncrease = 0.0001;

    private readonly object _syncRoot = new();
    private readonly Dictionary<ResourceKind, double> _lastObserved = new();
    private double _multiplier = 1;

    public double Multiplier
    {
        get
        {
            lock (_syncRoot)
            {
                return _multiplier;
            }
        }
    }

    public bool Configure(double multiplier)
    {
        if (!TrainerSettings.IsIncomeMultiplierSupported(multiplier))
        {
            return false;
        }

        lock (_syncRoot)
        {
            _multiplier = multiplier;
            _lastObserved.Clear();
        }

        return true;
    }

    public bool TryCalculateBonus(ResourceKind resource, double currentValue, out double bonus)
    {
        bonus = 0;
        if (!TrainerSettings.IncomeResources.Contains(resource) || !double.IsFinite(currentValue))
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (!_lastObserved.TryGetValue(resource, out var previousValue))
            {
                _lastObserved[resource] = currentValue;
                return false;
            }

            _lastObserved[resource] = currentValue;
            var increase = currentValue - previousValue;
            if (_multiplier <= 1 ||
                increase <= MinimumObservedIncrease ||
                increase > MaximumObservedIncrease)
            {
                return false;
            }

            bonus = increase * (_multiplier - 1);
            return bonus > MinimumObservedIncrease;
        }
    }

    public void Confirm(ResourceKind resource, double value)
    {
        lock (_syncRoot)
        {
            _lastObserved[resource] = value;
        }
    }

    public void Forget(ResourceKind resource)
    {
        lock (_syncRoot)
        {
            _lastObserved.Remove(resource);
        }
    }

    public void ResetObservations()
    {
        lock (_syncRoot)
        {
            _lastObserved.Clear();
        }
    }
}
