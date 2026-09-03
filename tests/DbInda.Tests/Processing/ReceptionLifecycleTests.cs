using DbInda.Worker.Files;
using DbInda.Worker.Models;
using DbInda.Worker.Processing;

namespace DbInda.Tests.Processing;

public sealed class ReceptionLifecycleTests
{
    [Theory]
    [InlineData(ReceptionStatuses.Procesado)]
    [InlineData(ReceptionStatuses.ProcesadoConAdvertencias)]
    [InlineData(ReceptionStatuses.Duplicado)]
    [InlineData(ReceptionStatuses.ConflictoMismaFactura)]
    [InlineData(ReceptionStatuses.ErrorXml)]
    [InlineData(ReceptionStatuses.ErrorPermanente)]
    public void Importacion_terminal_se_puede_archivar(string estado)
    {
        Assert.True(ReceptionLifecycle.ShouldArchive(estado));
        Assert.True(ReceptionLifecycle.IsIncompleteArchive(estado, ArchiveStatuses.Pendiente));
        Assert.True(ReceptionLifecycle.IsIncompleteArchive(estado, ArchiveStatuses.Archivando));
        Assert.False(ReceptionLifecycle.IsIncompleteArchive(estado, ArchiveStatuses.Archivado));
    }

    [Theory]
    [InlineData(ReceptionStatuses.ErrorSql)]
    [InlineData(ReceptionStatuses.Pendiente)]
    [InlineData(ReceptionStatuses.Procesando)]
    public void Importacion_no_terminal_no_se_archiva(string estado)
    {
        Assert.False(ReceptionLifecycle.ShouldArchive(estado));
        Assert.False(ReceptionLifecycle.IsIncompleteArchive(estado, ArchiveStatuses.Pendiente));
        Assert.True(ReceptionLifecycle.IsRecoverable(estado));
    }

    [Fact]
    public void Error_xml_va_a_errores_y_procesado_a_procesados()
    {
        Assert.Equal(ArchiveKind.Error, ReceptionLifecycle.ArchiveKindFor(ReceptionStatuses.ErrorXml));
        Assert.Equal(ArchiveKind.Processed, ReceptionLifecycle.ArchiveKindFor(ReceptionStatuses.Procesado));
        Assert.Equal(ArchiveKind.Processed, ReceptionLifecycle.ArchiveKindFor(ReceptionStatuses.Duplicado));
    }
}
