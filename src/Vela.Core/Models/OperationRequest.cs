namespace Vela.Core.Models;

public sealed record OperationRequest(
    Guid RunId,
    Profile Profile,
    OperationIntent Intent);

public enum OperationIntent
{
    Preflight,
    Compact
}
