using System;
using System.Buffers;
using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.Saga.Tests;

/// <summary>
/// Backs the README claim that <see cref="SagaStateWriter"/> and <see cref="SagaStateReader"/>
/// are zero-allocation. Each gate measures whole public calls over every supported shape. The
/// writer writes into a caller-owned buffer that is reset between calls, so any byte counted
/// comes from the writer. The reader allocates nothing except the value that
/// <see cref="SagaStateReader.ReadString"/> and <see cref="SagaStateReader.ReadBytes"/> return.
/// </summary>
public class SagaStateAllocationTests
{
    private const int Iterations = 1_000;

    private static readonly Guid SampleGuid = new("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateTime SampleDate = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset SampleOffset = new(2026, 10, 15, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly byte[] SampleBytes = new byte[16];
    private const string SampleString = "order-42";

    private static void WriteEveryShape(IBufferWriter<byte> buffer)
    {
        var w = new SagaStateWriter(buffer);
        w.WriteByte(1);
        w.WriteSByte(-1);
        w.WriteInt16(short.MaxValue);
        w.WriteUInt16(ushort.MaxValue);
        w.WriteInt32(int.MaxValue);
        w.WriteUInt32(uint.MaxValue);
        w.WriteInt64(long.MaxValue);
        w.WriteUInt64(ulong.MaxValue);
        w.WriteSingle(1.5f);
        w.WriteDouble(2.5d);
        w.WriteDecimal(123.45m);
        w.WriteBoolean(true);
        w.WriteDateTime(SampleDate);
        w.WriteDateTimeOffset(SampleOffset);
        w.WriteTimeSpan(TimeSpan.FromMinutes(5));
        w.WriteGuid(SampleGuid);
        w.WriteString(SampleString);
        w.WriteString(null);
        w.WriteBytes(SampleBytes);
        w.WriteBytes((byte[]?)null);
        w.WriteBytes(SampleBytes.AsSpan());
    }

    private static int ReadPrimitives(ReadOnlySpan<byte> data)
    {
        var r = new SagaStateReader(data);
        var sum = r.ReadByte() + r.ReadSByte() + r.ReadInt16() + r.ReadUInt16() + r.ReadInt32();
        _ = r.ReadUInt32();
        _ = r.ReadInt64();
        _ = r.ReadUInt64();
        _ = r.ReadSingle();
        _ = r.ReadDouble();
        _ = r.ReadDecimal();
        _ = r.ReadBoolean();
        _ = r.ReadDateTime();
        _ = r.ReadDateTimeOffset();
        _ = r.ReadTimeSpan();
        _ = r.ReadGuid();
        return sum;
    }

    [Fact]
    public void Writer_EveryShape_IntoReusedBuffer_AllocatesNothing()
    {
        var buffer = new ArrayBufferWriter<byte>(initialCapacity: 512);
        AllocationGate.AssertBudget(0, Iterations, () =>
        {
            buffer.ResetWrittenCount();
            WriteEveryShape(buffer);
        }, "SagaStateWriter, every shape");
    }

    [Fact]
    public void Reader_Primitives_AllocatesNothing()
    {
        var buffer = new ArrayBufferWriter<byte>(initialCapacity: 512);
        WriteEveryShape(buffer);
        var data = buffer.WrittenMemory;
        AllocationGate.AssertBudget(0, Iterations, () => _ = ReadPrimitives(data.Span),
            "SagaStateReader, primitives");
    }

    [Fact]
    public void Reader_StringAndBytes_AllocateOnlyTheReturnedValues()
    {
        var buffer = new ArrayBufferWriter<byte>(initialCapacity: 64);
        var w = new SagaStateWriter(buffer);
        w.WriteString(SampleString);
        w.WriteString(null);
        w.WriteString(string.Empty);
        var strings = buffer.WrittenMemory.ToArray();

        buffer = new ArrayBufferWriter<byte>(initialCapacity: 64);
        w = new SagaStateWriter(buffer);
        w.WriteBytes(SampleBytes);
        w.WriteBytes((byte[]?)null);
        w.WriteBytes(ReadOnlySpan<byte>.Empty);
        var arrays = buffer.WrittenMemory.ToArray();

        var stringBytes = AllocationGate.MeasureBytesPerCall(Iterations, () =>
        {
            var r = new SagaStateReader(strings);
            _ = r.ReadString();
            _ = r.ReadString();
            _ = r.ReadString();
        });
        var arrayBytes = AllocationGate.MeasureBytesPerCall(Iterations, () =>
        {
            var r = new SagaStateReader(arrays);
            _ = r.ReadBytes();
            _ = r.ReadBytesNullable();
            _ = r.ReadBytes();
        });
        // The returned values and nothing else: "order-42" is a 40 B string and a 16-byte array is
        // 40 B. The null and empty reads return null, string.Empty and Array.Empty, so 0 B.
        Assert.Equal(40, stringBytes);
        Assert.Equal(40, arrayBytes);
    }

    [Fact]
    public void Generated_Restore_AllocatesNothing()
    {
        ISagaPersistableState state = new ZeroAlloc.Saga.Tests.Fixtures.OrderFulfillmentSaga();
        var snapshot = state.Snapshot();
        AllocationGate.AssertBudget(0, Iterations, () => state.Restore(snapshot),
            "generated Restore, OrderId + decimal");
    }

    [Fact]
    public void Generated_Snapshot_AllocatesItsBufferAndTheReturnedArray()
    {
        ISagaPersistableState state = new ZeroAlloc.Saga.Tests.Fixtures.OrderFulfillmentSaga();
        var bytes = AllocationGate.MeasureBytesPerCall(Iterations, () => _ = state.Snapshot());

        // Snapshot() returns a new byte[], 21 bytes of state here, 48 B as an array. The other
        // 120 B are the ArrayBufferWriter and its 64-byte initial buffer, created per call by the
        // generated code, #273.
        Assert.Equal(168, bytes);
    }
}
