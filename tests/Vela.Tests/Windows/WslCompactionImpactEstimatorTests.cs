using Vela.Core.Contracts;
using Vela.Windows.Processes;
using Vela.Windows.Storage;
using System.Buffers.Binary;

namespace Vela.Tests.Windows;

public sealed class WslCompactionImpactEstimatorTests
{
    [Fact]
    public async Task EstimateAsync_uses_guest_used_bytes_to_calculate_reclaimable_space()
    {
        var runner = new Fakes.FakeProcessRunner
        {
            ThrowOnInvocation = false,
            Result = CreateResult(
                "Filesystem  1B-blocks  Used  Available  Use% Mounted on",
                "/dev/sdc    10737418240 4294967296 6442450944 40% /")
        };
        var estimator = new WslCompactionImpactEstimator(runner, new NativeToolPaths());

        var result = await estimator.EstimateAsync(
            "docker-desktop",
            vhdxPath: @"C:\Vela\fixtures\ext4.vhdx",
            currentVhdxSizeBytes: 10L * 1024 * 1024 * 1024,
            targetIsRunning: true,
            cancellationToken: CancellationToken.None);

        Assert.Equal(CompactionImpactStatus.Estimated, result.Status);
        Assert.Equal(4L * 1024 * 1024 * 1024, result.UsedBytes);
        Assert.Equal(6L * 1024 * 1024 * 1024, result.ReclaimableBytes);
        Assert.Equal(
            new[] { "--distribution", "docker-desktop", "--", "df", "-B1", "-P", "/" },
            Assert.Single(runner.Invocations).Arguments);
    }

    [Fact]
    public async Task EstimateAsync_returns_unavailable_when_df_output_has_no_used_bytes()
    {
        var runner = new Fakes.FakeProcessRunner
        {
            ThrowOnInvocation = false,
            Result = CreateResult("df: unavailable")
        };
        var estimator = new WslCompactionImpactEstimator(runner, new NativeToolPaths());

        var result = await estimator.EstimateAsync(
            "Ubuntu-24.04",
            vhdxPath: @"C:\Vela\fixtures\ext4.vhdx",
            currentVhdxSizeBytes: 10L * 1024 * 1024 * 1024,
            targetIsRunning: true,
            cancellationToken: CancellationToken.None);

        Assert.Equal(CompactionImpactStatus.Unavailable, result.Status);
        Assert.Null(result.ReclaimableBytes);
    }

    [Fact]
    public async Task EstimateAsync_reads_ext4_usage_from_vhdx_when_df_is_unavailable()
    {
        using var fixture = MinimalVhdxFixture.Create(
            totalBlocks: 4_096,
            freeBlocks: 1_024);
        var runner = new Fakes.FakeProcessRunner
        {
            ThrowOnInvocation = false,
            Result = new ProcessExecutionResult(
                ProcessExecutionStatus.Failed,
                1,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch)
        };
        var estimator = new WslCompactionImpactEstimator(runner, new NativeToolPaths());
        const long currentVhdxSizeBytes = 16L * 1024 * 1024 * 1024;
        const long expectedUsedBytes = (4_096L - 1_024L) * 4_096L;

        var result = await estimator.EstimateAsync(
            "Ubuntu-24.04",
            fixture.Path,
            currentVhdxSizeBytes,
            targetIsRunning: false,
            CancellationToken.None);

        Assert.Equal(CompactionImpactStatus.Estimated, result.Status);
        Assert.Equal(expectedUsedBytes, result.UsedBytes);
        Assert.Equal(currentVhdxSizeBytes - expectedUsedBytes, result.ReclaimableBytes);
        Assert.Equal(0, runner.InvocationCount);
    }

    [Fact]
    public async Task EstimateAsync_does_not_start_a_stopped_target_when_offline_usage_is_unavailable()
    {
        var runner = new Fakes.FakeProcessRunner
        {
            ThrowOnInvocation = false,
            Result = CreateResult(
                "Filesystem  1B-blocks  Used  Available  Use% Mounted on",
                "/dev/sdc    10737418240 4294967296 6442450944 40% /")
        };
        var estimator = new WslCompactionImpactEstimator(runner, new NativeToolPaths());

        var result = await estimator.EstimateAsync(
            "docker-desktop",
            vhdxPath: @"C:\Vela\fixtures\missing-ext4.vhdx",
            currentVhdxSizeBytes: 10L * 1024 * 1024 * 1024,
            targetIsRunning: false,
            cancellationToken: CancellationToken.None);

        Assert.Equal(CompactionImpactStatus.Unavailable, result.Status);
        Assert.Null(result.UsedBytes);
        Assert.Null(result.ReclaimableBytes);
        Assert.Equal(0, runner.InvocationCount);
    }

    private static ProcessExecutionResult CreateResult(params string[] output) =>
        new(
            ProcessExecutionStatus.Succeeded,
            0,
            output.ToImmutableArray(),
            ImmutableArray<string>.Empty,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private sealed class MinimalVhdxFixture : IDisposable
    {
        private const long BatOffset = 0x0030_0000;
        private const long MetadataOffset = 0x0020_0000;
        private const long PayloadOffset = 0x00c0_0000;

        private MinimalVhdxFixture(string path) => Path = path;

        public string Path { get; }

        public static MinimalVhdxFixture Create(uint totalBlocks, uint freeBlocks)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"vela-impact-{Guid.NewGuid():N}.vhdx");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            stream.SetLength(PayloadOffset + 1_048_576);

            WriteRegionTable(stream);
            WriteMetadata(stream);
            WriteBat(stream);
            WriteExt4Superblock(stream, totalBlocks, freeBlocks);
            return new MinimalVhdxFixture(path);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }

        private static void WriteRegionTable(FileStream stream)
        {
            var table = new byte[0x70];
            Encoding.ASCII.GetBytes("regi").CopyTo(table, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(8), 2);
            WriteRegionEntry(table, 0x10, Guid.Parse("2dc27766-f623-4200-9d64-115e9bfd4a08"), BatOffset, 0x1000);
            WriteRegionEntry(table, 0x30, Guid.Parse("8b7ca206-4790-4b9a-b8fe-575f050f886e"), MetadataOffset, 0x1000);
            WriteAt(stream, 0x30000, table);
        }

        private static void WriteMetadata(FileStream stream)
        {
            var metadata = new byte[0x10040];
            Encoding.ASCII.GetBytes("metadata").CopyTo(metadata, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(metadata.AsSpan(10), 1);
            WriteMetadataEntry(
                metadata,
                0x20,
                Guid.Parse("caa16737-fa36-4d43-b3b6-33f0aa44e76b"),
                0x10000,
                8,
                4);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(0x10000), 1_048_576);
            WriteAt(stream, MetadataOffset, metadata);
        }

        private static void WriteBat(FileStream stream)
        {
            var bat = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bat, (ulong)(PayloadOffset | 6));
            WriteAt(stream, BatOffset, bat);
        }

        private static void WriteExt4Superblock(FileStream stream, uint totalBlocks, uint freeBlocks)
        {
            var superblock = new byte[0x200];
            BinaryPrimitives.WriteUInt32LittleEndian(superblock.AsSpan(4), totalBlocks);
            BinaryPrimitives.WriteUInt32LittleEndian(superblock.AsSpan(12), freeBlocks);
            BinaryPrimitives.WriteUInt32LittleEndian(superblock.AsSpan(0x18), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(superblock.AsSpan(0x38), 0xef53);
            WriteAt(stream, PayloadOffset + 1024, superblock);
        }

        private static void WriteRegionEntry(
            byte[] table,
            int offset,
            Guid id,
            long regionOffset,
            long regionLength)
        {
            id.ToByteArray().CopyTo(table, offset);
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(offset + 16), (ulong)regionOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(offset + 24), (ulong)regionLength);
        }

        private static void WriteMetadataEntry(
            byte[] metadata,
            int offset,
            Guid id,
            int dataOffset,
            int dataLength,
            int flags)
        {
            id.ToByteArray().CopyTo(metadata, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(offset + 16), (uint)dataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(offset + 20), (uint)dataLength);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(offset + 24), (uint)flags);
        }

        private static void WriteAt(FileStream stream, long offset, byte[] bytes)
        {
            stream.Position = offset;
            stream.Write(bytes);
        }
    }
}
