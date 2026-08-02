namespace Vela.Core.Contracts;

public interface IVhdxInspector
{
    Task<VhdxInspectionResult> InspectAsync(
        string vhdxPath,
        CancellationToken cancellationToken);
}
