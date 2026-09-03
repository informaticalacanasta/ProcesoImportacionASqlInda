namespace DbInda.Worker.Files;

public enum ArchiveReconcileDecision
{
    AllocateAndMove,
    MoveOriginToDestination,
    FinalizeFromDestination,
    FinalizeKeepOrigin,
    ReallocateAndMove,
    MarkError
}

public static class ArchiveReconciliation
{
    public static ArchiveReconcileDecision Decide(
        bool originExists,
        bool destinationExists,
        bool destinationHasExpectedHash,
        bool hasIntendedDestination)
    {
        if (!hasIntendedDestination)
            return originExists ? ArchiveReconcileDecision.AllocateAndMove : ArchiveReconcileDecision.MarkError;

        if (originExists && !destinationExists)
            return ArchiveReconcileDecision.MoveOriginToDestination;

        if (!originExists && destinationExists && destinationHasExpectedHash)
            return ArchiveReconcileDecision.FinalizeFromDestination;

        if (originExists && destinationExists && destinationHasExpectedHash)
            return ArchiveReconcileDecision.FinalizeKeepOrigin;

        if (destinationExists && !destinationHasExpectedHash)
            return originExists ? ArchiveReconcileDecision.ReallocateAndMove : ArchiveReconcileDecision.MarkError;

        return ArchiveReconcileDecision.MarkError;
    }
}
