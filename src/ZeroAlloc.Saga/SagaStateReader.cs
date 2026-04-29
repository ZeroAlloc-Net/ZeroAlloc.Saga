using System;
using System.Buffers.Binary;
using System.Text;

namespace ZeroAlloc.Saga;

/// <summary>
/// Reads typed primitive values from a <see cref="ReadOnlySpan{T}"/> previously
/// produced by <see cref="SagaStateWriter"/>. Each <c>ReadXxx</c> method
/// consumes the bytes written by the corresponding <c>WriteXxx</c> call.
/// </summary>
/// <remarks>
/// Methods take a local copy of the underlying span before slicing to keep the
/// <c>ErrorProne.NET.Structs</c> EPS06 analyzer happy — the analyzer flags
/// <c>Slice</c> calls on a non-<c>readonly</c> struct field as "hidden copy"
/// candidates. Since <see cref="ReadOnlySpan{T}"/> is itself a thin pointer/length
/// pair, taking a local does not allocate.
/// </remarks>
public ref struct SagaStateReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public SagaStateReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public byte ReadByte()
    {
        var data = _data;
        return data[_position++];
    }

    public sbyte ReadSByte() => (sbyte)ReadByte();

    public short ReadInt16()
    {
        var data = _data;
        var v = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(_position));
        _position += 2;
        return v;
    }

    public ushort ReadUInt16()
    {
        var data = _data;
        var v = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(_position));
        _position += 2;
        return v;
    }

    public int ReadInt32()
    {
        var data = _data;
        var v = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(_position));
        _position += 4;
        return v;
    }

    public uint ReadUInt32()
    {
        var data = _data;
        var v = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(_position));
        _position += 4;
        return v;
    }

    public long ReadInt64()
    {
        var data = _data;
        var v = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(_position));
        _position += 8;
        return v;
    }

    public ulong ReadUInt64()
    {
        var data = _data;
        var v = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(_position));
        _position += 8;
        return v;
    }

    public float ReadSingle()
    {
        var data = _data;
        var v = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(_position));
        _position += 4;
        return v;
    }

    public double ReadDouble()
    {
        var data = _data;
        var v = BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(_position));
        _position += 8;
        return v;
    }

    public decimal ReadDecimal()
    {
        var data = _data;
        Span<int> bits = stackalloc int[4];
        for (int i = 0; i < 4; i++)
            bits[i] = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(_position + i * 4, 4));
        _position += 16;
        return new decimal(bits);
    }

    public bool ReadBoolean() => ReadByte() != 0;

    /// <summary>
    /// Reads a UTF-8 length-prefixed string. A leading length of <c>-1</c>
    /// returns <c>null</c>; <c>0</c> returns the empty string.
    /// </summary>
    public string? ReadString()
    {
        var len = ReadInt32();
        if (len < 0) return null;
        if (len == 0) return string.Empty;
        var data = _data;
        var s = Encoding.UTF8.GetString(data.Slice(_position, len));
        _position += len;
        return s;
    }

    public DateTime ReadDateTime() => DateTime.FromBinary(ReadInt64());

    public DateTimeOffset ReadDateTimeOffset()
    {
        var utcTicks = ReadInt64();
        var offsetMinutes = ReadInt16();
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        return new DateTimeOffset(utcTicks + offset.Ticks, offset);
    }

    public TimeSpan ReadTimeSpan() => new TimeSpan(ReadInt64());

    public Guid ReadGuid()
    {
        var data = _data;
        var g = new Guid(data.Slice(_position, 16));
        _position += 16;
        return g;
    }

    /// <summary>
    /// Reads a length-prefixed byte sequence. A leading length of <c>-1</c>
    /// returns <c>null</c>; <c>0</c> returns an empty array.
    /// </summary>
    public byte[]? ReadBytes()
    {
        var len = ReadInt32();
        if (len < 0) return null;
        if (len == 0) return Array.Empty<byte>();
        var data = _data;
        var bytes = data.Slice(_position, len).ToArray();
        _position += len;
        return bytes;
    }

    /// <summary>
    /// Reads a length-prefixed byte sequence, distinguishing <c>null</c>
    /// (length <c>-1</c>) from an empty array (length <c>0</c>). Pair with
    /// <see cref="SagaStateWriter.WriteBytes(byte[])"/> on the write side.
    /// </summary>
    /// <remarks>
    /// Wire format is identical to <see cref="ReadBytes"/>; this overload is
    /// kept distinct only to make the null-preservation contract explicit at
    /// the call site emitted by the source generator for <c>byte[]?</c> fields.
    /// </remarks>
    public byte[]? ReadBytesNullable()
    {
        var len = ReadInt32();
        if (len < 0) return null;
        if (len == 0) return Array.Empty<byte>();
        var data = _data;
        var bytes = data.Slice(_position, len).ToArray();
        _position += len;
        return bytes;
    }
}
