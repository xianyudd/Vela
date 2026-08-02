namespace Vela.Core.Models;

public sealed record Profile(
    Guid Id,
    string DisplayName,
    string DistroName,
    string VhdxPath,
    ShutdownMode ShutdownMode,
    TimeSpan ShutdownTimeout);

public enum ShutdownMode
{
    Global,
    Distro
}
