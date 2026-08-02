namespace Vela.Core.Models;

public sealed record VhdxSnapshot(
    DateTimeOffset CapturedAtUtc,
    string Path,
    long FileLengthBytes,
    DateTimeOffset LastWriteUtc,
    bool? IsSparse,
    DriveSnapshot Drive);

public sealed record DriveSnapshot(
    string RootPath,
    long TotalSizeBytes,
    long AvailableFreeSpaceBytes);
