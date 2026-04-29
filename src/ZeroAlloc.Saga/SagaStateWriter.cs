using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace ZeroAlloc.Saga;

/// <summary>
/// Writes typed primitive values to an <see cref="IBufferWriter{T}"/> as a
/// little-endian, length-prefixed binary format. Zero-allocation, AOT-safe.
/// Used by generator-emitted <c>Snapshot()</c> methods.
/// </summary>
/// <remarks>
/// The serialized format is opaque and not guaranteed stable across major
/// versions of <c>ZeroAlloc.Saga</c>. The generator emits a leading version
/// byte ahead of any user-state writes; backends rely on that to detect
/// mismatches via <see cref="SagaStateVersionMismatchException"/>.
/// </remarks>
public readonly ref struct SagaStateWriter
{
    private readonly IBufferWriter<byte> _buffer;

    public SagaStateWriter(IBufferWriter<byte> buffer) => _buffer = buffer;

    public void WriteByte(byte value)
    {
        var span = _buffer.GetSpan(1);
        span[0] = value;
        _buffer.Advance(1);
    }

    public void WriteSByte(sbyte value) => WriteByte((byte)value);

    public void WriteInt16(short value)
    {
        var span = _buffer.GetSpan(2);
        BinaryPrimitives.WriteInt16LittleEndian(span, value);
        _buffer.Advance(2);
    }

    public void WriteUInt16(ushort value)
    {
        var span = _buffer.GetSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        _buffer.Advance(2);
    }

    public void WriteInt32(int value)
    {
        var span = _buffer.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(span, value);
        _buffer.Advance(4);
    }

    public void WriteUInt32(uint value)
    {
        var span = _buffer.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        _buffer.Advance(4);
    }

    public void WriteInt64(long value)
    {
        var span = _buffer.GetSpan(8);
        BinaryPrimitives.WriteInt64LittleEndian(span, value);
        _buffer.Advance(8);
    }

    public void WriteUInt64(ulong value)
    {
        var span = _buffer.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        _buffer.Advance(8);
    }

    public void WriteSingle(float value)
    {
        var span = _buffer.GetSpan(4);
        BinaryPrimitives.WriteSingleLittleEndian(span, value);
        _buffer.Advance(4);
    }

    public void WriteDouble(double value)
    {
        var span = _buffer.GetSpan(8);
        BinaryPrimitives.WriteDoubleLittleEndian(span, value);
        _buffer.Advance(8);
    }

    public void WriteDecimal(decimal value)
    {
        // decimal.GetBits → 4 ints = 16 bytes. AOT-safe, no reflection.
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        var span = _buffer.GetSpan(16);
        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(i * 4, 4), bits[i]);
        _buffer.Advance(16);
    }

    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    /// <summary>
    /// Writes a UTF-8 length-prefixed string. A null value is encoded as
    /// length <c>-1</c>; an empty string is length <c>0</c>.
    /// </summary>
    public void WriteString(string? value)
    {
        if (value is null)
        {
            WriteInt32(-1);
            return;
        }
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteInt32(byteCount);
        if (byteCount == 0) return;
        var span = _buffer.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(value, span);
        _buffer.Advance(byteCount);
    }

    public void WriteDateTime(DateTime value) => WriteInt64(value.ToBinary());

    public void WriteDateTimeOffset(DateTimeOffset value)
    {
        WriteInt64(value.UtcTicks);
        WriteInt16((short)value.Offset.TotalMinutes);
    }

    public void WriteTimeSpan(TimeSpan value) => WriteInt64(value.Ticks);

    public void WriteGuid(Guid value)
    {
        var span = _buffer.GetSpan(16);
        value.TryWriteBytes(span);
        _buffer.Advance(16);
    }

    /// <summary>
    /// Writes a length-prefixed byte sequence. A length of <c>-1</c> encodes
    /// a null reference; <c>0</c> encodes an empty array. The <see cref="ReadOnlySpan{T}"/>
    /// overload always emits a non-negative length — pass an array via the
    /// <c>byte[]?</c> overload to round-trip a null reference.
    /// </summary>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteInt32(value.Length);
        if (value.Length == 0) return;
        var span = _buffer.GetSpan(value.Length);
        value.CopyTo(span);
        _buffer.Advance(value.Length);
    }

    /// <summary>
    /// Writes a length-prefixed byte sequence, distinguishing <c>null</c>
    /// from an empty array. A null reference is encoded as length <c>-1</c>;
    /// an empty array as length <c>0</c>. Pair with
    /// <see cref="SagaStateReader.ReadBytesNullable"/> on the read side.
    /// </summary>
    public void WriteBytes(byte[]? value)
    {
        if (value is null)
        {
            WriteInt32(-1);
            return;
        }
        WriteInt32(value.Length);
        if (value.Length == 0) return;
        var span = _buffer.GetSpan(value.Length);
        value.AsSpan().CopyTo(span);
        _buffer.Advance(value.Length);
    }
}
