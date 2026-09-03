using DbInda.Worker.Configuration;

namespace DbInda.Tests.Inbound;

public sealed class ProcessingOptionsValidatorTests
{
    [Fact]
    public void Rechaza_valores_no_positivos()
    {
        var result = new ProcessingOptionsValidator().Validate(
            null,
            new ProcessingOptions
            {
                MaxConcurrency = 0,
                QueueCapacity = 0,
                ScanIntervalSeconds = 0,
                StableChecks = 0,
                StableCheckDelayMilliseconds = 0
            });

        Assert.True(result.Failed);
        Assert.Contains("MaxConcurrency", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("QueueCapacity", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("ScanIntervalSeconds", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("StableChecks", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("StableCheckDelayMilliseconds", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Acepta_valores_por_defecto()
    {
        var result = new ProcessingOptionsValidator().Validate(null, new ProcessingOptions());
        Assert.False(result.Failed);
    }
}
