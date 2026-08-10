using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Vela.Windows.Storage;

/// <summary>
/// Reads the ext4 superblock from the first payload block of a dynamic VHDX.
/// It provides a stopped-distro fallback for the impact preview without
/// mounting the disk or starting WSL.
/// </summary>
public sealed class VhdxExt4UsageReader
{
    private const long RegionTableOffset = 0x30000;
    private const int RegionTableReadLength = 0x1000;
    private const int MetadataReadLength = 0x1000;
    private const long Ext4SuperblockOffset = 1024;
    private const int Ext4SuperblockReadLength = 0x200;
    private const ulong BatStateMask = 0x7;
    private const ulong FullyPresentPayloadState = 6;
    private const ulong FullyPresentPayloadStateWithoutFooter = 7;
    private static readonly Guid BatRegionId = Guid.Parse("2dc27766-f623-4200-9d64-115e9bfd4a08");
    private static readonly Guid MetadataRegionId = Guid.Parse("8b7ca206-4790-4b9a-b8fe-575f050f886e");
    private static readonly Guid FileParametersId = Guid.Parse("caa16737-fa36-4d43-b3b6-33f0aa44e76b");

    public bool TryReadUsedBytes(string? vhdxPath, out long usedBytes)
    {
        usedBytes = 0;
        if (string.IsNullOrWhiteSpace(vhdxPath) || vhdxPath.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                vhdxPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            return TryReadUsedBytes(stream, out usedBytes);
        }
        catch (Exception)
        {
            usedBytes = 0;
            return false;
        }
    }

    private static bool TryReadUsedBytes(FileStream stream, out long usedBytes)
    {
        usedBytes = 0;
        var regionTable = new byte[RegionTableReadLength];
        if (!ReadAt(stream, RegionTableOffset, regionTable) ||
            !regionTable.AsSpan(0, 4).SequenceEqual("regi"u8))
        {
            return false;
        }

        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(regionTable.AsSpan(8));
        if (entryCount == 0 || entryCount > 31)
        {
            return false;
        }

        ulong? batOffset = null;
        ulong? metadataOffset = null;
        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = checked(0x10 + (int)index * 32);
            var regionId = new Guid(regionTable.AsSpan(entryOffset, 16));
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(regionTable.AsSpan(entryOffset + 16));
            if (regionId == BatRegionId)
            {
                batOffset = offset;
            }
            else if (regionId == MetadataRegionId)
            {
                metadataOffset = offset;
            }
        }

        if (batOffset is not { } batStart || metadataOffset is not { } metadataStart)
        {
            return false;
        }

        var metadataHeader = new byte[MetadataReadLength];
        if (!ReadAt(stream, checked((long)metadataStart), metadataHeader) ||
            !metadataHeader.AsSpan(0, 8).SequenceEqual("metadata"u8))
        {
            return false;
        }

        var metadataEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(metadataHeader.AsSpan(10));
        if (metadataEntryCount == 0 || metadataEntryCount > 32)
        {
            return false;
        }

        ulong? blockSize = null;
        for (var index = 0; index < metadataEntryCount; index++)
        {
            var entryOffset = checked(0x20 + index * 32);
            if (entryOffset + 32 > metadataHeader.Length)
            {
                return false;
            }

            var itemId = new Guid(metadataHeader.AsSpan(entryOffset, 16));
            if (itemId != FileParametersId)
            {
                continue;
            }

            var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(metadataHeader.AsSpan(entryOffset + 16));
            var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(metadataHeader.AsSpan(entryOffset + 20));
            if (dataLength < sizeof(uint) || dataLength > 4096)
            {
                return false;
            }

            var data = new byte[dataLength];
            if (!ReadAt(stream, checked((long)metadataStart + dataOffset), data))
            {
                return false;
            }

            blockSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, sizeof(uint)));
            break;
        }

        if (blockSize is not { } payloadBlockSize ||
            payloadBlockSize < 1_048_576 ||
            payloadBlockSize % 1_048_576 != 0)
        {
            return false;
        }

        var batEntry = new byte[sizeof(ulong)];
        if (!ReadAt(stream, checked((long)batStart), batEntry))
        {
            return false;
        }

        var batValue = BinaryPrimitives.ReadUInt64LittleEndian(batEntry);
        var payloadState = batValue & BatStateMask;
        if (payloadState is not (FullyPresentPayloadState or FullyPresentPayloadStateWithoutFooter))
        {
            return false;
        }

        var payloadOffset = batValue & ~BatStateMask;
        var superblock = new byte[Ext4SuperblockReadLength];
        if (payloadOffset > long.MaxValue ||
            !ReadAt(stream, checked((long)payloadOffset + Ext4SuperblockOffset), superblock) ||
            BinaryPrimitives.ReadUInt16LittleEndian(superblock.AsSpan(0x38)) != 0xef53)
        {
            return false;
        }

        var logBlockSize = BinaryPrimitives.ReadInt32LittleEndian(superblock.AsSpan(0x18));
        if (logBlockSize is < 0 or > 6)
        {
            return false;
        }

        var totalBlocks = ReadExt4BlockCount(superblock, 0x04, 0x150);
        var freeBlocks = ReadExt4BlockCount(superblock, 0x0c, 0x158);
        if (totalBlocks == 0 || freeBlocks > totalBlocks)
        {
            return false;
        }

        try
        {
            usedBytes = checked((long)(totalBlocks - freeBlocks) * (1024L << logBlockSize));
            return usedBytes >= 0;
        }
        catch (OverflowException)
        {
            usedBytes = 0;
            return false;
        }
    }

    private static ulong ReadExt4BlockCount(byte[] superblock, int lowOffset, int highOffset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(lowOffset)) |
        ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(highOffset)) << 32);

    private static bool ReadAt(FileStream stream, long offset, byte[] buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
        {
            return false;
        }

        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }
}
