using System.Collections.Immutable;

namespace Vela.Core.Contracts;

public sealed record WslInventory(
    DateTimeOffset CapturedAtUtc,
    ImmutableArray<WslDistribution> Distributions);

public sealed record WslDistribution(
    string Name,
    WslDistributionState State,
    int? Version,
    bool IsDefault,
    string? VhdxPath = null,
    long? VhdxSizeBytes = null);

public enum WslDistributionState
{
    Unknown,
    Stopped,
    Running
}
