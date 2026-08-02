using Vela.Core.Models;

namespace Vela.Core.Contracts;

public sealed record VhdxInspectionResult(
    VhdxInspectionStatus Status,
    VhdxSnapshot? Snapshot);

public enum VhdxInspectionStatus
{
    Succeeded,
    Missing,
    Failed
}
