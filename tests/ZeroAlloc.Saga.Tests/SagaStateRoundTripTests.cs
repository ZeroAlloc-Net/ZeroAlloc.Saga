using System;
using System.Buffers;
using ZeroAlloc.Saga;

namespace ZeroAlloc.Saga.Tests;

/// <summary>
/// Round-trip coverage of every primitive supported by
/// <see cref="SagaStateWriter"/> / <see cref="SagaStateReader"/>. Each test
/// writes a value, reads it back, and asserts equality. Edge values
/// (Min/Max, null, empty) are exercised explicitly.
/// </summary>
public class SagaStateRoundTripTests
{
    private static ArrayBufferWriter<byte> NewBuffer() => new ArrayBufferWriter<byte>(initialCapacity: 64);

    [Fact]
    public void Byte_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteByte(42);
        Assert.Equal((byte)42, new SagaStateReader(buf.WrittenSpan).ReadByte());
    }

    [Fact]
    public void SByte_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteSByte(-7);
        Assert.Equal((sbyte)-7, new SagaStateReader(buf.WrittenSpan).ReadSByte());
    }

    [Fact]
    public void Int16_MinMax_RoundTrips()
    {
        var buf1 = NewBuffer();
        new SagaStateWriter(buf1).WriteInt16(short.MinValue);
        Assert.Equal(short.MinValue, new SagaStateReader(buf1.WrittenSpan).ReadInt16());

        var buf2 = NewBuffer();
        new SagaStateWriter(buf2).WriteInt16(short.MaxValue);
        Assert.Equal(short.MaxValue, new SagaStateReader(buf2.WrittenSpan).ReadInt16());
    }

    [Fact]
    public void UInt16_MaxValue_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteUInt16(ushort.MaxValue);
        Assert.Equal(ushort.MaxValue, new SagaStateReader(buf.WrittenSpan).ReadUInt16());
    }

    [Fact]
    public void Int32_MinValue_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteInt32(int.MinValue);
        Assert.Equal(int.MinValue, new SagaStateReader(buf.WrittenSpan).ReadInt32());
    }

    [Fact]
    public void Int32_MaxValue_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteInt32(int.MaxValue);
        Assert.Equal(int.MaxValue, new SagaStateReader(buf.WrittenSpan).ReadInt32());
    }

    [Fact]
    public void UInt32_MaxValue_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteUInt32(uint.MaxValue);
        Assert.Equal(uint.MaxValue, new SagaStateReader(buf.WrittenSpan).ReadUInt32());
    }

    [Fact]
    public void Int64_MinMax_RoundTrips()
    {
        var buf1 = NewBuffer();
        new SagaStateWriter(buf1).WriteInt64(long.MinValue);
        Assert.Equal(long.MinValue, new SagaStateReader(buf1.WrittenSpan).ReadInt64());

        var buf2 = NewBuffer();
        new SagaStateWriter(buf2).WriteInt64(long.MaxValue);
        Assert.Equal(long.MaxValue, new SagaStateReader(buf2.WrittenSpan).ReadInt64());
    }

    [Fact]
    public void UInt64_MaxValue_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteUInt64(ulong.MaxValue);
        Assert.Equal(ulong.MaxValue, new SagaStateReader(buf.WrittenSpan).ReadUInt64());
    }

    [Fact]
    public void Single_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteSingle(3.14159f);
        Assert.Equal(3.14159f, new SagaStateReader(buf.WrittenSpan).ReadSingle());
    }

    [Fact]
    public void Double_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteDouble(2.71828d);
        Assert.Equal(2.71828d, new SagaStateReader(buf.WrittenSpan).ReadDouble());
    }

    [Fact]
    public void Decimal_MaxValue_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteDecimal(decimal.MaxValue);
        Assert.Equal(decimal.MaxValue, new SagaStateReader(buf.WrittenSpan).ReadDecimal());
    }

    [Fact]
    public void Decimal_NegativeFraction_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteDecimal(-12345.6789m);
        Assert.Equal(-12345.6789m, new SagaStateReader(buf.WrittenSpan).ReadDecimal());
    }

    [Fact]
    public void Boolean_True_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteBoolean(true);
        Assert.True(new SagaStateReader(buf.WrittenSpan).ReadBoolean());
    }

    [Fact]
    public void Boolean_False_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteBoolean(false);
        Assert.False(new SagaStateReader(buf.WrittenSpan).ReadBoolean());
    }

    [Fact]
    public void String_NonNull_Utf8_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteString("hello, sägä");
        Assert.Equal("hello, sägä", new SagaStateReader(buf.WrittenSpan).ReadString());
    }

    [Fact]
    public void String_Null_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteString(null);
        Assert.Null(new SagaStateReader(buf.WrittenSpan).ReadString());
    }

    [Fact]
    public void String_Empty_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteString(string.Empty);
        Assert.Equal(string.Empty, new SagaStateReader(buf.WrittenSpan).ReadString());
    }

    [Fact]
    public void DateTime_RoundTrips_PreservesUtcKind()
    {
        var dt = new DateTime(2026, 4, 29, 12, 30, 0, DateTimeKind.Utc);
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteDateTime(dt);
        var rt = new SagaStateReader(buf.WrittenSpan).ReadDateTime();
        Assert.Equal(dt, rt);
        Assert.Equal(DateTimeKind.Utc, rt.Kind);
    }

    [Fact]
    public void DateTimeOffset_RoundTrips_PreservesOffset()
    {
        var dto = new DateTimeOffset(2026, 4, 29, 12, 30, 0, TimeSpan.FromHours(2));
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteDateTimeOffset(dto);
        var rt = new SagaStateReader(buf.WrittenSpan).ReadDateTimeOffset();
        Assert.Equal(dto, rt);
        Assert.Equal(TimeSpan.FromHours(2), rt.Offset);
    }

    [Fact]
    public void TimeSpan_RoundTrips()
    {
        var ts = TimeSpan.FromDays(7) + TimeSpan.FromMilliseconds(123);
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteTimeSpan(ts);
        Assert.Equal(ts, new SagaStateReader(buf.WrittenSpan).ReadTimeSpan());
    }

    [Fact]
    public void Guid_RoundTrips()
    {
        var g = Guid.NewGuid();
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteGuid(g);
        Assert.Equal(g, new SagaStateReader(buf.WrittenSpan).ReadGuid());
    }

    [Fact]
    public void Bytes_NonEmpty_RoundTrips()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 250, 251, 252 };
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteBytes(bytes);
        Assert.Equal(bytes, new SagaStateReader(buf.WrittenSpan).ReadBytes());
    }

    [Fact]
    public void Bytes_Empty_RoundTrips()
    {
        var buf = NewBuffer();
        new SagaStateWriter(buf).WriteBytes(ReadOnlySpan<byte>.Empty);
        var rt = new SagaStateReader(buf.WrittenSpan).ReadBytes();
        Assert.NotNull(rt);
        Assert.Empty(rt!);
    }

    [Fact]
    public void Multiple_Fields_InOrder_RoundTrip()
    {
        var buf = NewBuffer();
        var w = new SagaStateWriter(buf);
        w.WriteByte(1);
        w.WriteInt32(2);
        w.WriteString("three");
        w.WriteDecimal(4.5m);
        w.WriteGuid(Guid.Empty);
        w.WriteBoolean(true);

        var r = new SagaStateReader(buf.WrittenSpan);
        Assert.Equal((byte)1, r.ReadByte());
        Assert.Equal(2, r.ReadInt32());
        Assert.Equal("three", r.ReadString());
        Assert.Equal(4.5m, r.ReadDecimal());
        Assert.Equal(Guid.Empty, r.ReadGuid());
        Assert.True(r.ReadBoolean());
    }

    // Pattern: a generator-emitted Snapshot/Restore encodes a leading version
    // byte. Round-trip a representative state shape that mimics that wire layout.
    [Fact]
    public void VersionByte_PlusFields_RoundTrip_MatchesGeneratorWireLayout()
    {
        const byte expectedVersion = 1;
        var buf = NewBuffer();
        var w = new SagaStateWriter(buf);
        w.WriteByte(expectedVersion);
        w.WriteGuid(new Guid("12345678-1234-1234-1234-123456789abc"));
        w.WriteDecimal(199.99m);
        w.WriteString("alice@example.com");

        var r = new SagaStateReader(buf.WrittenSpan);
        Assert.Equal(expectedVersion, r.ReadByte());
        Assert.Equal(new Guid("12345678-1234-1234-1234-123456789abc"), r.ReadGuid());
        Assert.Equal(199.99m, r.ReadDecimal());
        Assert.Equal("alice@example.com", r.ReadString());
    }
}
