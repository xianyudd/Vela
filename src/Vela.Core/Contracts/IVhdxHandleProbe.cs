namespace Vela.Core.Contracts;

/// <summary>
/// Whether a VHDX file is currently free for an exclusive writer.
/// </summary>
public enum VhdxHandleState
{
    /// <summary>
    /// Nothing holds the file, so diskpart can acquire the exclusive handle
    /// <c>compact vdisk</c> requires.
    /// </summary>
    Free,

    /// <summary>
    /// Another process holds the file and refuses to share it. On WSL 2 this is
    /// normally the shared utility VM, which keeps a distribution's disk
    /// attached for the whole life of the VM once that distribution starts.
    /// </summary>
    Held,

    /// <summary>
    /// The state could not be determined. Callers must treat this as "no
    /// evidence" and continue, never as a failure.
    /// </summary>
    Unknown
}

/// <summary>
/// Reports whether a VHDX can be opened exclusively. This is the only cheap
/// question that predicts whether diskpart will be able to compact it.
/// </summary>
public interface IVhdxHandleProbe
{
    /// <summary>
    /// Probes <paramref name="vhdxPath"/> without modifying it.
    /// </summary>
    Task<VhdxHandleState> ProbeAsync(string vhdxPath, CancellationToken cancellationToken);
}
