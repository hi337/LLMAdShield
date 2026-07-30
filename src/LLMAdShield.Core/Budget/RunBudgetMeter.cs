namespace LLMAdShield.Core;

public sealed record BudgetPolicy
{
    public decimal WarningThresholdUsd { get; init; } = 8m;

    public decimal HardCapUsd { get; init; } = 10m;

    public static BudgetPolicy Default { get; } = new();

    internal void Validate()
    {
        if (WarningThresholdUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WarningThresholdUsd));
        }

        if (HardCapUsd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HardCapUsd));
        }

        if (WarningThresholdUsd >= HardCapUsd)
        {
            throw new ArgumentException("The warning threshold must be below the hard cap.");
        }
    }
}

public enum BudgetLevel
{
    Normal,
    Warning,
    Exhausted,
}

public sealed record BudgetSnapshot(
    decimal SpentUsd,
    decimal WarningThresholdUsd,
    decimal HardCapUsd,
    BudgetLevel Level,
    bool CanLaunchRequest);

public sealed record BudgetUpdate(
    BudgetSnapshot Snapshot,
    bool WarningThresholdCrossed,
    bool HardCapCrossed);

public sealed class RunBudgetMeter
{
    private readonly object _gate = new();
    private readonly BudgetPolicy _policy;
    private decimal _spentUsd;
    private bool _launchesStopped;

    public RunBudgetMeter(BudgetPolicy? policy = null, decimal initialSpendUsd = 0m)
    {
        _policy = policy ?? BudgetPolicy.Default;
        _policy.Validate();

        if (initialSpendUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialSpendUsd));
        }

        _spentUsd = initialSpendUsd;
    }

    public BudgetSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public bool CanLaunchRequest
    {
        get
        {
            lock (_gate)
            {
                return !_launchesStopped && _spentUsd < _policy.HardCapUsd;
            }
        }
    }

    public bool CanLaunchRequestCostingAtMost(decimal maximumCostUsd)
    {
        if (maximumCostUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCostUsd));
        }

        lock (_gate)
        {
            return !_launchesStopped && _spentUsd + maximumCostUsd <= _policy.HardCapUsd;
        }
    }

    public BudgetSnapshot StopLaunchingRequests()
    {
        lock (_gate)
        {
            _launchesStopped = true;
            return CreateSnapshot();
        }
    }

    public BudgetUpdate RecordCost(decimal costUsd)
    {
        if (costUsd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(costUsd));
        }

        lock (_gate)
        {
            var previousSpend = _spentUsd;
            _spentUsd += costUsd;

            return new BudgetUpdate(
                CreateSnapshot(),
                previousSpend < _policy.WarningThresholdUsd &&
                    _spentUsd >= _policy.WarningThresholdUsd,
                previousSpend < _policy.HardCapUsd &&
                    _spentUsd >= _policy.HardCapUsd);
        }
    }

    private BudgetSnapshot CreateSnapshot()
    {
        var level = _launchesStopped || _spentUsd >= _policy.HardCapUsd
            ? BudgetLevel.Exhausted
            : _spentUsd >= _policy.WarningThresholdUsd
                ? BudgetLevel.Warning
                : BudgetLevel.Normal;

        return new BudgetSnapshot(
            _spentUsd,
            _policy.WarningThresholdUsd,
            _policy.HardCapUsd,
            level,
            !_launchesStopped && _spentUsd < _policy.HardCapUsd);
    }
}
