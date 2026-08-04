using System;

namespace ArisenEditor.Views;

internal enum StartupWorldPresentationBarrierDecision
{
    None,
    WaitForActivation,
    WaitForOutput,
    DiscardOutput,
    PresentAndRelease
}

internal enum StartupWorldPresentationReconcileDecision
{
    None,
    StaleNotification,
    WaitForActivation,
    ActivationBoundaryCaptured,
    ReleaseWithoutActivation
}

internal readonly record struct StartupWorldPresentationTarget(
    Guid Guid,
    string PackageId)
{
    public bool IsValid => Guid != Guid.Empty && !string.IsNullOrWhiteSpace(PackageId);

    public bool Matches(StartupWorldPresentationTarget other) =>
        Guid == other.Guid &&
        string.Equals(PackageId, other.PackageId, StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct StartupWorldPresentationObservation(
    long Revision,
    StartupWorldPresentationTarget? ActiveWorld,
    Guid ActiveWorldGuid,
    StartupWorldPresentationTarget? PendingWorld)
{
    public bool HasAnyActiveWorld =>
        ActiveWorld is { IsValid: true } || ActiveWorldGuid != Guid.Empty;

    public bool HasCoherentActiveWorld =>
        ActiveWorld is { IsValid: true } active &&
        ActiveWorldGuid != Guid.Empty &&
        active.Guid == ActiveWorldGuid;
}

internal struct StartupWorldPresentationBarrierState
{
    public bool IsActive { get; private set; }
    public bool HasActivationBoundary { get; private set; }
    public ulong ActivationOutputTicket { get; private set; }
    public long ObservedRevision { get; private set; }
    public long ActivationRevision { get; private set; }
    public StartupWorldPresentationTarget Target { get; private set; }

    public bool TryBegin(
        StartupWorldPresentationTarget configuredStartup,
        in StartupWorldPresentationObservation observation)
    {
        Reset();
        if (!configuredStartup.IsValid ||
            observation.HasAnyActiveWorld ||
            observation.PendingWorld is not { IsValid: true } pendingWorld)
        {
            return false;
        }

        IsActive = true;
        ObservedRevision = observation.Revision;
        Target = pendingWorld;
        return true;
    }

    public StartupWorldPresentationReconcileDecision Reconcile(
        long notificationRevision,
        in StartupWorldPresentationObservation current,
        ulong activationOutputTicket)
    {
        if (!IsActive)
        {
            return StartupWorldPresentationReconcileDecision.None;
        }

        if (notificationRevision != current.Revision ||
            current.Revision < ObservedRevision)
        {
            return StartupWorldPresentationReconcileDecision.StaleNotification;
        }

        if (current.PendingWorld is { IsValid: true } pendingWorld)
        {
            Target = pendingWorld;
            ObservedRevision = current.Revision;
            ActivationRevision = 0;
            ActivationOutputTicket = 0;
            HasActivationBoundary = false;
            return StartupWorldPresentationReconcileDecision.WaitForActivation;
        }

        if (current.HasAnyActiveWorld)
        {
            if (!current.HasCoherentActiveWorld)
            {
                return StartupWorldPresentationReconcileDecision.StaleNotification;
            }

            StartupWorldPresentationTarget activeWorld = current.ActiveWorld!.Value;
            if (HasActivationBoundary &&
                ActivationRevision == current.Revision &&
                Target.Matches(activeWorld))
            {
                return StartupWorldPresentationReconcileDecision.None;
            }

            Target = activeWorld;
            ObservedRevision = current.Revision;
            ActivationRevision = current.Revision;
            ActivationOutputTicket = activationOutputTicket;
            HasActivationBoundary = true;
            return StartupWorldPresentationReconcileDecision.ActivationBoundaryCaptured;
        }

        Reset();
        return StartupWorldPresentationReconcileDecision.ReleaseWithoutActivation;
    }

    public readonly bool IsCurrentActivation(
        in StartupWorldPresentationObservation observation) =>
        IsActive &&
        HasActivationBoundary &&
        observation.Revision == ActivationRevision &&
        observation.HasCoherentActiveWorld &&
        Target.Matches(observation.ActiveWorld!.Value);

    public readonly StartupWorldPresentationBarrierDecision Evaluate(ulong outputTicket)
    {
        if (!IsActive)
        {
            return StartupWorldPresentationBarrierDecision.None;
        }

        if (!HasActivationBoundary)
        {
            return StartupWorldPresentationBarrierDecision.WaitForActivation;
        }

        if (outputTicket == 0)
        {
            return StartupWorldPresentationBarrierDecision.WaitForOutput;
        }

        return outputTicket <= ActivationOutputTicket
            ? StartupWorldPresentationBarrierDecision.DiscardOutput
            : StartupWorldPresentationBarrierDecision.PresentAndRelease;
    }

    public void CompleteAfterPresented(ulong outputTicket)
    {
        if (Evaluate(outputTicket) !=
            StartupWorldPresentationBarrierDecision.PresentAndRelease)
        {
            throw new InvalidOperationException(
                $"Output ticket {outputTicket} does not cross startup-world activation boundary " +
                $"{ActivationOutputTicket}.");
        }

        Reset();
    }

    public void Reset()
    {
        IsActive = false;
        HasActivationBoundary = false;
        ActivationOutputTicket = 0;
        ObservedRevision = 0;
        ActivationRevision = 0;
        Target = default;
    }
}
