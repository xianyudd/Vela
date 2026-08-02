namespace Vela.Core.Contracts;

public interface ILxssProfileResolver
{
    Task<LxssProfileResolution> ResolveAsync(
        string distroName,
        string requestedVhdxPath,
        CancellationToken cancellationToken);
}
