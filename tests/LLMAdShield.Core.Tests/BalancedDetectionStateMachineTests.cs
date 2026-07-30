using LLMAdShield.Core;

namespace LLMAdShield.Core.Tests;

public sealed class BalancedDetectionStateMachineTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ContentLabel.Commercial)]
    [InlineData(ContentLabel.PromoOrSponsor)]
    public void OneHighConfidenceAdvertisement_StartsTwentySecondMinimumMute(ContentLabel label)
    {
        var detector = new BalancedDetectionStateMachine();

        var decision = detector.Process(Result(Epoch, label, ConfidenceLevel.High), Epoch);

        Assert.Equal(DetectionAction.AcquireOrRenewMute, decision.Action);
        Assert.Equal(DetectionReason.HighConfidenceAdvertisement, decision.Reason);
        Assert.True(decision.Snapshot.IsMuted);
        Assert.Equal(Epoch.AddSeconds(20), decision.Snapshot.MuteLeaseExpiresAt);
    }

    [Fact]
    public void TwoMediumConfidenceAdvertisementsWithinFourSeconds_StartMute()
    {
        var detector = new BalancedDetectionStateMachine();

        var first = detector.Process(
            Result(Epoch, ContentLabel.Commercial, ConfidenceLevel.Medium),
            Epoch);
        var secondAt = Epoch.AddSeconds(4);
        var second = detector.Process(
            Result(secondAt, ContentLabel.PromoOrSponsor, ConfidenceLevel.Medium),
            secondAt);

        Assert.Equal(DetectionAction.None, first.Action);
        Assert.True(first.Snapshot.IsAdvertisementSuspected);
        Assert.Equal(DetectionAction.AcquireOrRenewMute, second.Action);
        Assert.Equal(DetectionReason.RepeatedAdvertisementEvidence, second.Reason);
    }

    [Fact]
    public void MediumConfidenceAdvertisementsOutsideFourSeconds_DoNotStartMute()
    {
        var detector = new BalancedDetectionStateMachine();
        detector.Process(Result(Epoch, ContentLabel.Commercial, ConfidenceLevel.Medium), Epoch);
        var later = Epoch.AddMilliseconds(4001);

        var decision = detector.Process(
            Result(later, ContentLabel.Commercial, ConfidenceLevel.Medium),
            later);

        Assert.Equal(DetectionAction.None, decision.Action);
        Assert.False(decision.Snapshot.IsMuted);
        Assert.Equal(1, decision.Snapshot.RecentAdvertisementConfirmations);
    }

    [Fact]
    public void EarlyAdvertisementRenewal_DoesNotShortenMinimumMute()
    {
        var detector = MutedDetector();
        var renewalAt = Epoch.AddSeconds(3);

        var decision = detector.Process(
            Result(renewalAt, ContentLabel.Commercial, ConfidenceLevel.Medium),
            renewalAt);

        Assert.Equal(DetectionAction.AcquireOrRenewMute, decision.Action);
        Assert.Equal(DetectionReason.ConfirmedAdvertisementRenewal, decision.Reason);
        Assert.Equal(Epoch.AddSeconds(20), decision.Snapshot.MuteLeaseExpiresAt);
    }

    [Fact]
    public void AdvertisementNearEndOfMinimumMute_RenewsLeaseForTenSeconds()
    {
        var detector = MutedDetector();
        var renewalAt = Epoch.AddSeconds(15);

        var decision = detector.Process(
            Result(renewalAt, ContentLabel.Commercial, ConfidenceLevel.Medium),
            renewalAt);

        Assert.Equal(DetectionAction.AcquireOrRenewMute, decision.Action);
        Assert.Equal(Epoch.AddSeconds(25), decision.Snapshot.MuteLeaseExpiresAt);
    }

    [Fact]
    public void ProgramVerdictsDuringMinimumMute_AreIgnored()
    {
        var detector = MutedDetector();
        var firstAt = Epoch.AddSeconds(1);
        var secondAt = Epoch.AddSeconds(2);

        var first = detector.Process(
            Result(firstAt, ContentLabel.Program, ConfidenceLevel.Low),
            firstAt);
        var second = detector.Process(
            Result(secondAt, ContentLabel.Program, ConfidenceLevel.Medium),
            secondAt);

        Assert.Equal(DetectionReason.MinimumMuteHold, first.Reason);
        Assert.Equal(DetectionAction.None, second.Action);
        Assert.Equal(DetectionReason.MinimumMuteHold, second.Reason);
        Assert.True(second.Snapshot.IsMuted);
        Assert.Equal(0, second.Snapshot.ConsecutiveProgramVerdicts);
    }

    [Fact]
    public void FiveConfidentProgramVerdictsWithoutAdEnd_ReleaseMute()
    {
        var detector = MutedDetector();
        var renewalAt = Epoch.AddSeconds(15);
        detector.Process(
            Result(renewalAt, ContentLabel.Commercial, ConfidenceLevel.Medium),
            renewalAt);
        var firstAt = Epoch.AddSeconds(20);
        var secondAt = Epoch.AddSeconds(21);
        var thirdAt = Epoch.AddSeconds(22);
        var fourthAt = Epoch.AddSeconds(23);
        var fifthAt = Epoch.AddSeconds(24);

        var first = detector.Process(
            Result(firstAt, ContentLabel.Program, ConfidenceLevel.Medium),
            firstAt);
        var second = detector.Process(
            Result(secondAt, ContentLabel.Program, ConfidenceLevel.Medium),
            secondAt);
        var third = detector.Process(
            Result(thirdAt, ContentLabel.Program, ConfidenceLevel.High),
            thirdAt);
        var fourth = detector.Process(
            Result(fourthAt, ContentLabel.Program, ConfidenceLevel.Medium),
            fourthAt);
        var fifth = detector.Process(
            Result(fifthAt, ContentLabel.Program, ConfidenceLevel.High),
            fifthAt);

        Assert.Equal(DetectionAction.None, first.Action);
        Assert.Equal(DetectionAction.None, second.Action);
        Assert.Equal(DetectionAction.None, third.Action);
        Assert.Equal(DetectionAction.None, fourth.Action);
        Assert.Equal(DetectionAction.ReleaseMute, fifth.Action);
        Assert.Equal(DetectionReason.ProgramResumed, fifth.Reason);
        Assert.False(fifth.Snapshot.IsMuted);
    }

    [Fact]
    public void LowConfidenceProgram_DoesNotCountTowardRelease()
    {
        var detector = MutedDetector();
        var renewalAt = Epoch.AddSeconds(15);
        detector.Process(
            Result(renewalAt, ContentLabel.Commercial, ConfidenceLevel.Medium),
            renewalAt);

        var low = detector.Process(
            Result(Epoch.AddSeconds(20), ContentLabel.Program, ConfidenceLevel.Low),
            Epoch.AddSeconds(20));
        var first = detector.Process(
            Result(Epoch.AddSeconds(21), ContentLabel.Program, ConfidenceLevel.Medium),
            Epoch.AddSeconds(21));
        var second = detector.Process(
            Result(Epoch.AddSeconds(22), ContentLabel.Program, ConfidenceLevel.High),
            Epoch.AddSeconds(22));

        Assert.Equal(DetectionReason.LowConfidence, low.Reason);
        Assert.Equal(1, first.Snapshot.ConsecutiveProgramVerdicts);
        Assert.Equal(DetectionAction.None, second.Action);
        Assert.Equal(2, second.Snapshot.ConsecutiveProgramVerdicts);
        Assert.True(second.Snapshot.IsMuted);
    }

    [Fact]
    public void ThreeProgramGuessesInsideAdvertisement_DoNotCauseReleaseWithoutAdEnd()
    {
        var detector = MutedDetector();
        var renewalAt = Epoch.AddSeconds(15);
        detector.Process(
            Result(renewalAt, ContentLabel.Commercial, ConfidenceLevel.High),
            renewalAt);

        var first = detector.Process(
            Result(Epoch.AddSeconds(20), ContentLabel.Program, ConfidenceLevel.High),
            Epoch.AddSeconds(20));
        var second = detector.Process(
            Result(Epoch.AddSeconds(21), ContentLabel.Program, ConfidenceLevel.Medium),
            Epoch.AddSeconds(21));
        var third = detector.Process(
            Result(Epoch.AddSeconds(22), ContentLabel.Program, ConfidenceLevel.High),
            Epoch.AddSeconds(22));
        var advertisement = detector.Process(
            Result(Epoch.AddSeconds(23), ContentLabel.Commercial, ConfidenceLevel.High),
            Epoch.AddSeconds(23));

        Assert.Equal(DetectionAction.None, first.Action);
        Assert.Equal(DetectionAction.None, second.Action);
        Assert.Equal(DetectionAction.None, third.Action);
        Assert.True(third.Snapshot.IsMuted);
        Assert.Equal(DetectionAction.AcquireOrRenewMute, advertisement.Action);
        Assert.Equal(0, advertisement.Snapshot.ConsecutiveProgramVerdicts);
    }

    [Fact]
    public void HighConfidenceAdEnd_StillRequiresOneFollowUpProgramVerdict()
    {
        var detector = MutedDetector();
        var renewalAt = Epoch.AddSeconds(15);
        detector.Process(
            Result(renewalAt, ContentLabel.Commercial, ConfidenceLevel.Medium),
            renewalAt);

        var adEnd = detector.Process(
            Result(
                Epoch.AddSeconds(20),
                ContentLabel.Program,
                ConfidenceLevel.High,
                ContentTransition.AdEnd),
            Epoch.AddSeconds(20));
        var followUp = detector.Process(
            Result(Epoch.AddSeconds(21), ContentLabel.Program, ConfidenceLevel.Medium),
            Epoch.AddSeconds(21));

        Assert.Equal(DetectionAction.None, adEnd.Action);
        Assert.Equal(2, adEnd.Snapshot.ConsecutiveProgramVerdicts);
        Assert.Equal(DetectionAction.ReleaseMute, followUp.Action);
        Assert.Equal(DetectionReason.ProgramResumed, followUp.Reason);
    }

    [Fact]
    public void PublicSafety_ReleasesMuteImmediately()
    {
        var detector = MutedDetector();
        var now = Epoch.AddSeconds(1);

        var decision = detector.Process(
            Result(now, ContentLabel.PublicSafety, ConfidenceLevel.Low),
            now);

        Assert.Equal(DetectionAction.ReleaseMute, decision.Action);
        Assert.Equal(DetectionReason.PublicSafety, decision.Reason);
        Assert.False(decision.Snapshot.IsMuted);
    }

    [Fact]
    public void StalePublicSafetyVerdict_StillFailsOpenImmediately()
    {
        var detector = MutedDetector();
        var now = Epoch.AddSeconds(4);

        var decision = detector.Process(
            Result(Epoch, ContentLabel.PublicSafety, ConfidenceLevel.Low),
            now);

        Assert.Equal(DetectionAction.ReleaseMute, decision.Action);
        Assert.Equal(DetectionReason.PublicSafety, decision.Reason);
        Assert.False(decision.Snapshot.IsMuted);
    }

    [Fact]
    public void StaleClassification_CannotStartMute()
    {
        var detector = new BalancedDetectionStateMachine();
        var now = Epoch.AddSeconds(4);

        var decision = detector.Process(
            Result(Epoch, ContentLabel.Commercial, ConfidenceLevel.High),
            now);

        Assert.Equal(DetectionAction.None, decision.Action);
        Assert.Equal(DetectionReason.StaleClassification, decision.Reason);
        Assert.False(decision.Snapshot.IsMuted);
    }

    [Fact]
    public void ErrorsDoNotRenewMute_AndLeaseFailsOpenAtExpiry()
    {
        var detector = MutedDetector();
        var errorAt = Epoch.AddSeconds(2);

        var error = detector.Process(ErrorResult(errorAt), errorAt);
        var beforeExpiry = detector.Tick(Epoch.AddMilliseconds(19999));
        var expired = detector.Tick(Epoch.AddSeconds(20));

        Assert.Equal(DetectionReason.ClassificationFailure, error.Reason);
        Assert.Equal(Epoch.AddSeconds(20), error.Snapshot.MuteLeaseExpiresAt);
        Assert.Equal(DetectionAction.None, beforeExpiry.Action);
        Assert.Equal(DetectionAction.ReleaseMute, expired.Action);
        Assert.Equal(DetectionReason.LeaseExpired, expired.Reason);
    }

    [Fact]
    public void MediumAdvertisementAtLeaseExpiry_ReleasesAudioButKeepsFreshSuspicion()
    {
        var detector = MutedDetector();
        var expiry = Epoch.AddSeconds(20);

        var decision = detector.Process(
            Result(expiry, ContentLabel.Commercial, ConfidenceLevel.Medium),
            expiry);

        Assert.Equal(DetectionAction.ReleaseMute, decision.Action);
        Assert.Equal(DetectionReason.LeaseExpired, decision.Reason);
        Assert.False(decision.Snapshot.IsMuted);
        Assert.True(decision.Snapshot.IsAdvertisementSuspected);
        Assert.Equal(decision.Snapshot, detector.Snapshot);
    }

    [Fact]
    public void UnknownAndLowConfidenceResults_CannotStartMute()
    {
        var detector = new BalancedDetectionStateMachine();

        var unknown = detector.Process(
            Result(Epoch, ContentLabel.Unknown, ConfidenceLevel.High),
            Epoch);
        var lowAdAt = Epoch.AddSeconds(1);
        var lowAd = detector.Process(
            Result(lowAdAt, ContentLabel.Commercial, ConfidenceLevel.Low),
            lowAdAt);

        Assert.Equal(DetectionReason.UnknownContent, unknown.Reason);
        Assert.Equal(DetectionReason.LowConfidence, lowAd.Reason);
        Assert.False(lowAd.Snapshot.IsMuted);
    }

    [Fact]
    public void BudgetCap_CanForceImmediateFailOpen()
    {
        var detector = MutedDetector();

        var decision = detector.ForceFailOpen(Epoch.AddSeconds(1), DetectionReason.BudgetCap);

        Assert.Equal(DetectionAction.ReleaseMute, decision.Action);
        Assert.Equal(DetectionReason.BudgetCap, decision.Reason);
        Assert.False(decision.Snapshot.IsMuted);
    }

    private static BalancedDetectionStateMachine MutedDetector()
    {
        var detector = new BalancedDetectionStateMachine();
        detector.Process(Result(Epoch, ContentLabel.Commercial, ConfidenceLevel.High), Epoch);
        return detector;
    }

    private static ClassificationResult Result(
        DateTimeOffset frameTime,
        ContentLabel label,
        ConfidenceLevel confidence,
        ContentTransition transition = ContentTransition.None) =>
        new()
        {
            Outcome = ClassificationOutcome.Success,
            Model = "test-model",
            FrameWindowEndedAt = frameTime,
            StartedAt = frameTime,
            CompletedAt = frameTime,
            Verdict = new AdVerdict
            {
                CurrentLabel = label,
                Transition = transition,
                Confidence = confidence,
                Evidence = [],
                NeedsMoreContext = false,
            },
        };

    private static ClassificationResult ErrorResult(DateTimeOffset frameTime) =>
        new()
        {
            Outcome = ClassificationOutcome.Error,
            Model = "test-model",
            FrameWindowEndedAt = frameTime,
            StartedAt = frameTime,
            CompletedAt = frameTime,
            ErrorCode = "test_error",
        };
}
