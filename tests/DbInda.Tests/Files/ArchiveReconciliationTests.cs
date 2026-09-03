using DbInda.Worker.Files;

namespace DbInda.Tests.Files;

public sealed class ArchiveReconciliationTests
{
    [Fact]
    public void Caso_A_origen_existe_destino_no_mueve()
        => Assert.Equal(
            ArchiveReconcileDecision.MoveOriginToDestination,
            ArchiveReconciliation.Decide(originExists: true, destinationExists: false, destinationHasExpectedHash: false, hasIntendedDestination: true));

    [Fact]
    public void Caso_B_solo_destino_con_hash_finaliza()
        => Assert.Equal(
            ArchiveReconcileDecision.FinalizeFromDestination,
            ArchiveReconciliation.Decide(originExists: false, destinationExists: true, destinationHasExpectedHash: true, hasIntendedDestination: true));

    [Fact]
    public void Caso_C_ambos_con_hash_finaliza_y_conserva_origen()
        => Assert.Equal(
            ArchiveReconcileDecision.FinalizeKeepOrigin,
            ArchiveReconciliation.Decide(originExists: true, destinationExists: true, destinationHasExpectedHash: true, hasIntendedDestination: true));

    [Fact]
    public void Caso_D_ninguno_error()
        => Assert.Equal(
            ArchiveReconcileDecision.MarkError,
            ArchiveReconciliation.Decide(originExists: false, destinationExists: false, destinationHasExpectedHash: false, hasIntendedDestination: true));

    [Fact]
    public void Pendiente_sin_prevista_con_origen_asigna_destino()
        => Assert.Equal(
            ArchiveReconcileDecision.AllocateAndMove,
            ArchiveReconciliation.Decide(originExists: true, destinationExists: false, destinationHasExpectedHash: false, hasIntendedDestination: false));

    [Fact]
    public void Destino_ocupado_con_otro_hash_no_se_toca()
        => Assert.Equal(
            ArchiveReconcileDecision.ReallocateAndMove,
            ArchiveReconciliation.Decide(originExists: true, destinationExists: true, destinationHasExpectedHash: false, hasIntendedDestination: true));
}
