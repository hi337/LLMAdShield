namespace LLMAdShield.Core;

public sealed class BalancedDetectionStateMachine : IDetectionStateMachine
{
    private readonly object _gate = new();
    private readonly DetectionPolicy _policy;
    private readonly Queue<DateTimeOffset> _recentAdvertisementVerdicts = new();
    private bool _isMuted;
    private DateTimeOffset? _muteLeaseExpiresAt;
    private DateTimeOffset? _minimumMuteUntil;
    private int _consecutiveProgramVerdicts;
    private bool _programExitEvidenceSeen;

    public BalancedDetectionStateMachine(DetectionPolicy? policy = null)
    {
        _policy = policy ?? DetectionPolicy.Balanced;
        _policy.Validate();
    }

    public DetectionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public DetectionDecision Process(ClassificationResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            var expirationDecision = ExpireLeaseIfNeeded(now);
            PruneAdvertisementVerdicts(now);

            if (!result.HasUsableVerdict)
            {
                _consecutiveProgramVerdicts = 0;
                _programExitEvidenceSeen = false;
                return expirationDecision ?? Decision(
                    DetectionAction.None,
                    DetectionReason.ClassificationFailure,
                    now);
            }

            // Public-safety content is always a release-only signal. Even if the response
            // arrived late, acting on it can only fail open and avoids covering an alert.
            if (result.Verdict!.CurrentLabel == ContentLabel.PublicSafety)
            {
                var safetyDecision = ProcessPublicSafety(now);
                return expirationDecision is not null && safetyDecision.Action == DetectionAction.None
                    ? Decision(DetectionAction.ReleaseMute, DetectionReason.PublicSafety, now)
                    : safetyDecision;
            }

            var age = now - result.FrameWindowEndedAt;
            if (age > _policy.MaximumResultAge)
            {
                _consecutiveProgramVerdicts = 0;
                _programExitEvidenceSeen = false;
                return expirationDecision ?? Decision(
                    DetectionAction.None,
                    DetectionReason.StaleClassification,
                    now);
            }

            var verdict = result.Verdict!;
            var contentDecision = verdict.CurrentLabel switch
            {
                ContentLabel.Program => ProcessProgram(verdict, now),
                ContentLabel.Commercial or ContentLabel.PromoOrSponsor =>
                    ProcessAdvertisement(verdict.Confidence, now),
                _ => ProcessUnknown(now),
            };

            return expirationDecision is not null && contentDecision.Action == DetectionAction.None
                ? Decision(DetectionAction.ReleaseMute, DetectionReason.LeaseExpired, now)
                : contentDecision;
        }
    }

    public DetectionDecision Tick(DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneAdvertisementVerdicts(now);
            return ExpireLeaseIfNeeded(now) ??
                Decision(DetectionAction.None, DetectionReason.NoChange, now);
        }
    }

    public DetectionDecision ForceFailOpen(DateTimeOffset now, DetectionReason reason)
    {
        lock (_gate)
        {
            var wasMuted = _isMuted;
            ResetState();
            return Decision(
                wasMuted ? DetectionAction.ReleaseMute : DetectionAction.None,
                reason,
                now);
        }
    }

    private DetectionDecision ProcessAdvertisement(ConfidenceLevel confidence, DateTimeOffset now)
    {
        _consecutiveProgramVerdicts = 0;
        _programExitEvidenceSeen = false;

        if (confidence == ConfidenceLevel.Low)
        {
            return Decision(DetectionAction.None, DetectionReason.LowConfidence, now);
        }

        if (_isMuted)
        {
            _recentAdvertisementVerdicts.Clear();
            return StartOrRenewMute(now, DetectionReason.ConfirmedAdvertisementRenewal);
        }

        if (confidence == ConfidenceLevel.High)
        {
            _recentAdvertisementVerdicts.Clear();
            return StartOrRenewMute(now, DetectionReason.HighConfidenceAdvertisement);
        }

        _recentAdvertisementVerdicts.Enqueue(now);
        PruneAdvertisementVerdicts(now);
        if (_recentAdvertisementVerdicts.Count >= _policy.MediumConfidenceConfirmationsRequired)
        {
            _recentAdvertisementVerdicts.Clear();
            return StartOrRenewMute(now, DetectionReason.RepeatedAdvertisementEvidence);
        }

        return Decision(DetectionAction.None, DetectionReason.NoChange, now);
    }

    private DetectionDecision ProcessProgram(AdVerdict verdict, DateTimeOffset now)
    {
        _recentAdvertisementVerdicts.Clear();

        if (_isMuted && _minimumMuteUntil is { } minimumMuteUntil && now < minimumMuteUntil)
        {
            _consecutiveProgramVerdicts = 0;
            _programExitEvidenceSeen = false;
            return Decision(DetectionAction.None, DetectionReason.MinimumMuteHold, now);
        }

        if (verdict.Confidence == ConfidenceLevel.Low || verdict.NeedsMoreContext)
        {
            _consecutiveProgramVerdicts = 0;
            _programExitEvidenceSeen = false;
            return Decision(DetectionAction.None, DetectionReason.LowConfidence, now);
        }

        _programExitEvidenceSeen |= verdict.Transition == ContentTransition.AdEnd;
        var confirmationWeight = verdict.Transition == ContentTransition.AdEnd &&
            verdict.Confidence == ConfidenceLevel.High
                ? 2
                : 1;
        _consecutiveProgramVerdicts = Math.Min(
            _policy.ProgramConfirmationsWithoutAdEndRequired,
            _consecutiveProgramVerdicts + confirmationWeight);

        var confirmationsRequired = _programExitEvidenceSeen
            ? _policy.ProgramConfirmationsRequired
            : _policy.ProgramConfirmationsWithoutAdEndRequired;

        if (!_isMuted || _consecutiveProgramVerdicts < confirmationsRequired)
        {
            return Decision(DetectionAction.None, DetectionReason.NoChange, now);
        }

        ResetState();
        return Decision(DetectionAction.ReleaseMute, DetectionReason.ProgramResumed, now);
    }

    private DetectionDecision ProcessPublicSafety(DateTimeOffset now)
    {
        var wasMuted = _isMuted;
        ResetState();
        return Decision(
            wasMuted ? DetectionAction.ReleaseMute : DetectionAction.None,
            DetectionReason.PublicSafety,
            now);
    }

    private DetectionDecision ProcessUnknown(DateTimeOffset now)
    {
        _consecutiveProgramVerdicts = 0;
        _programExitEvidenceSeen = false;
        return Decision(DetectionAction.None, DetectionReason.UnknownContent, now);
    }

    private DetectionDecision StartOrRenewMute(DateTimeOffset now, DetectionReason reason)
    {
        if (!_isMuted)
        {
            _minimumMuteUntil = now + _policy.MinimumMuteDuration;
        }

        _isMuted = true;
        var renewedUntil = now + _policy.MuteLeaseDuration;
        var currentUntil = _muteLeaseExpiresAt ?? DateTimeOffset.MinValue;
        var minimumUntil = _minimumMuteUntil ?? DateTimeOffset.MinValue;
        _muteLeaseExpiresAt = renewedUntil > currentUntil ? renewedUntil : currentUntil;
        if (_muteLeaseExpiresAt < minimumUntil)
        {
            _muteLeaseExpiresAt = minimumUntil;
        }

        return Decision(DetectionAction.AcquireOrRenewMute, reason, now);
    }

    private DetectionDecision? ExpireLeaseIfNeeded(DateTimeOffset now)
    {
        if (!_isMuted || _muteLeaseExpiresAt is null || now < _muteLeaseExpiresAt.Value)
        {
            return null;
        }

        ResetState();
        return Decision(DetectionAction.ReleaseMute, DetectionReason.LeaseExpired, now);
    }

    private void PruneAdvertisementVerdicts(DateTimeOffset now)
    {
        var cutoff = now - _policy.AdConfirmationWindow;
        while (_recentAdvertisementVerdicts.TryPeek(out var timestamp) && timestamp < cutoff)
        {
            _recentAdvertisementVerdicts.Dequeue();
        }
    }

    private void ResetState()
    {
        _isMuted = false;
        _muteLeaseExpiresAt = null;
        _minimumMuteUntil = null;
        _consecutiveProgramVerdicts = 0;
        _programExitEvidenceSeen = false;
        _recentAdvertisementVerdicts.Clear();
    }

    private DetectionDecision Decision(
        DetectionAction action,
        DetectionReason reason,
        DateTimeOffset now) =>
        new(action, reason, now, CreateSnapshot());

    private DetectionSnapshot CreateSnapshot() =>
        new(
            _isMuted,
            _isMuted || _recentAdvertisementVerdicts.Count > 0,
            _muteLeaseExpiresAt,
            _recentAdvertisementVerdicts.Count,
            _consecutiveProgramVerdicts);
}
