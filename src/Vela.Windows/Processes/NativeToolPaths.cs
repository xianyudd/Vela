namespace Vela.Windows.Processes;

public sealed class NativeToolPaths
{
    public string WslExePath { get; } = Path.Combine(Environment.SystemDirectory, "wsl.exe");

    public string DiskPartExePath { get; } = Path.Combine(Environment.SystemDirectory, "diskpart.exe");

    public string FsutilExePath { get; } = Path.Combine(Environment.SystemDirectory, "fsutil.exe");
}
