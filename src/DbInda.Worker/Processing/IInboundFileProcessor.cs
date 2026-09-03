namespace DbInda.Worker.Processing;

public interface IInboundFileProcessor
{
    Task ProcessAsync(string fullPath, CancellationToken cancellationToken);
}
