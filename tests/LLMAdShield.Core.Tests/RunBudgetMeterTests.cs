using LLMAdShield.Core;

namespace LLMAdShield.Core.Tests;

public sealed class RunBudgetMeterTests
{
    [Fact]
    public void CanLaunchRequestCostingAtMost_ProtectsHardCapBeforeDispatch()
    {
        var meter = new RunBudgetMeter(initialSpendUsd: 9.995m);

        Assert.True(meter.CanLaunchRequestCostingAtMost(0.005m));
        Assert.False(meter.CanLaunchRequestCostingAtMost(0.006m));
    }

    [Fact]
    public void DefaultMeter_WarnsAtEightDollarsAndStopsAtTen()
    {
        var meter = new RunBudgetMeter();

        var belowWarning = meter.RecordCost(7.99m);
        var warning = meter.RecordCost(0.01m);
        var cap = meter.RecordCost(2m);

        Assert.Equal(BudgetLevel.Normal, belowWarning.Snapshot.Level);
        Assert.False(belowWarning.WarningThresholdCrossed);
        Assert.True(warning.WarningThresholdCrossed);
        Assert.Equal(BudgetLevel.Warning, warning.Snapshot.Level);
        Assert.True(warning.Snapshot.CanLaunchRequest);
        Assert.True(cap.HardCapCrossed);
        Assert.Equal(BudgetLevel.Exhausted, cap.Snapshot.Level);
        Assert.False(cap.Snapshot.CanLaunchRequest);
        Assert.False(meter.CanLaunchRequest);
    }

    [Fact]
    public void ThresholdEvents_AreOnlyRaisedWhenCrossed()
    {
        var meter = new RunBudgetMeter(initialSpendUsd: 9m);

        var first = meter.RecordCost(1m);
        var later = meter.RecordCost(1m);

        Assert.True(first.HardCapCrossed);
        Assert.False(later.HardCapCrossed);
        Assert.False(later.WarningThresholdCrossed);
        Assert.Equal(11m, later.Snapshot.SpentUsd);
    }

    [Fact]
    public void NegativeCost_IsRejected()
    {
        var meter = new RunBudgetMeter();

        Assert.Throws<ArgumentOutOfRangeException>(() => meter.RecordCost(-0.01m));
    }
}
