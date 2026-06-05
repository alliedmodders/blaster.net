// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.Diagnostics.CodeAnalysis;

namespace Blaster.Valve;

/// <summary>
/// Exception thrown when packet parsing fails.
/// </summary>
public class PacketException : Exception
{
    public PacketException(string message) : base(message) { }
}

/// <summary>
/// Builds binary packets using a buffer-based approach.
/// </summary>
public class PacketBuilder
{
    private readonly MemoryStream _buffer = new();

    /// <summary>
    /// Writes a null-terminated string to the packet.
    /// </summary>
    public void WriteCString(string str)
    {
        _buffer.Write(System.Text.Encoding.UTF8.GetBytes(str));
        _buffer.WriteByte(0);
    }

    /// <summary>
    /// Writes an 8-bit unsigned integer to the packet.
    /// </summary>
    public void WriteUInt8(byte value)
    {
        _buffer.WriteByte(value);
    }

    /// <summary>
    /// Writes a 16-bit unsigned integer (little-endian) to the packet.
    /// </summary>
    public void WriteUInt16(ushort value)
    {
        _buffer.Write(BitConverter.GetBytes(value), 0, 2);
    }

    /// <summary>
    /// Writes a 32-bit unsigned integer (little-endian) to the packet.
    /// </summary>
    public void WriteUInt32(uint value)
    {
        _buffer.Write(BitConverter.GetBytes(value), 0, 4);
    }

    /// <summary>
    /// Writes a 32-bit signed integer (little-endian) to the packet.
    /// </summary>
    public void WriteInt32(int value)
    {
        _buffer.Write(BitConverter.GetBytes(value), 0, 4);
    }

    /// <summary>
    /// Writes raw bytes to the packet.
    /// </summary>
    public void WriteBytes(byte[] data)
    {
        _buffer.Write(data, 0, data.Length);
    }

    /// <summary>
    /// Gets the packet data as a byte array.
    /// </summary>
    public byte[] GetBytes()
    {
        return _buffer.ToArray();
    }

    /// <summary>
    /// Gets the current size of the packet.
    /// </summary>
    public int Length => (int)_buffer.Length;
}

/// <summary>
/// Reads binary packets with bounds checking.
/// </summary>
public class PacketReader
{
    private readonly byte[] _buffer;
    private int _position;

    public PacketReader(byte[] data)
    {
        _buffer = data ?? throw new ArgumentNullException(nameof(data));
        _position = 0;
    }

    /// <summary>
    /// Gets the current position in the packet.
    /// </summary>
    public int Position => _position;

    /// <summary>
    /// Gets the total length of the packet.
    /// </summary>
    public int Length => _buffer.Length;

    /// <summary>
    /// Checks if there is more data to read.
    /// </summary>
    public bool HasMore => _position < _buffer.Length;

    /// <summary>
    /// Returns the remaining bytes without advancing the position.
    /// </summary>
    public ReadOnlySpan<byte> RemainingBytes => _buffer.AsSpan(_position);

    /// <summary>
    /// Reads a slice of bytes from the packet.
    /// </summary>
    /// <exception cref="PacketException">Thrown if attempting to read beyond packet bounds.</exception>
    public byte[] ReadSlice(int count)
    {
        if (count < 0 || _position + count > _buffer.Length)
        {
            throw new PacketException("Attempted to read out of bounds");
        }

        byte[] result = new byte[count];
        Array.Copy(_buffer, _position, result, 0, count);
        _position += count;
        return result;
    }

    /// <summary>
    /// Reads an 8-bit unsigned integer from the packet.
    /// </summary>
    /// <exception cref="PacketException">Thrown if attempting to read beyond packet bounds.</exception>
    public byte ReadUInt8()
    {
        if (_position >= _buffer.Length)
        {
            throw new PacketException("Attempted to read out of bounds");
        }
        return _buffer[_position++];
    }

    /// <summary>
    /// Reads a 16-bit unsigned integer (little-endian) from the packet.
    /// </summary>
    /// <exception cref="PacketException">Thrown if attempting to read beyond packet bounds.</exception>
    public ushort ReadUInt16()
    {
        if (_position + 2 > _buffer.Length)
        {
            throw new PacketException("Attempted to read out of bounds");
        }
        ushort value = BitConverter.ToUInt16(_buffer, _position);
        _position += 2;
        return value;
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer (little-endian) from the packet.
    /// </summary>
    /// <exception cref="PacketException">Thrown if attempting to read out of bounds.</exception>
    public uint ReadUInt32()
    {
        if (_position + 4 > _buffer.Length)
        {
            throw new PacketException("Attempted to read out of bounds");
        }
        uint value = BitConverter.ToUInt32(_buffer, _position);
        _position += 4;
        return value;
    }

    /// <summary>
    /// Reads a 32-bit signed integer (little-endian) from the packet.
    /// </summary>
    /// <exception cref="PacketException">Thrown if attempting to read out of bounds.</exception>
    public int ReadInt32()
    {
        if (_position + 4 > _buffer.Length)
        {
            throw new PacketException("Attempted to read out of bounds");
        }
        int value = BitConverter.ToInt32(_buffer, _position);
        _position += 4;
        return value;
    }

    /// <summary>
    /// Reads a 64-bit unsigned integer (little-endian) from the packet.
    /// </summary>
    /// <exception cref="PacketException">Thrown if attempting to read out of bounds.</exception>
    public ulong ReadUInt64()
    {
        if (_position + 8 > _buffer.Length)
        {
            throw new PacketException("Attempted to read out of bounds");
        }
        ulong value = BitConverter.ToUInt64(_buffer, _position);
        _position += 8;
        return value;
    }

    /// <summary>
    /// Reads an IPv4 address (4 bytes) from the packet.
    /// </summary>
    /// <exception cref="PacketException">Thrown if attempting to read beyond packet bounds.</exception>
    public System.Net.IPAddress ReadIPv4()
    {
        const int ipv4Length = 4;
        if (_position + ipv4Length > _buffer.Length)
        {
            throw new PacketException("Attempted to read out of bounds");
        }
        var ipBytes = new byte[ipv4Length];
        Array.Copy(_buffer, _position, ipBytes, 0, ipv4Length);
        _position += ipv4Length;
        return new System.Net.IPAddress(ipBytes);
    }

    /// <summary>
    /// Reads a port (2 bytes, big-endian) from the packet.
    /// </summary>
    /// <exception cref="PacketException">Thrown if attempting to read beyond packet bounds.</exception>
    public ushort ReadPort()
    {
        if (_position + 2 > _buffer.Length)
        {
            throw new PacketException("Attempted to read out of bounds");
        }
        // Ports in Valve protocol are big-endian
        ushort value = (ushort)((_buffer[_position] << 8) | _buffer[_position + 1]);
        _position += 2;
        return value;
    }

    /// <summary>
    /// Attempts to read a null-terminated string from the packet.
    /// </summary>
    /// <returns>True if a null-terminated string was found; false otherwise.</returns>
    public bool TryReadString([NotNullWhen(true)] out string? result)
    {
        int start = _position;
        while (_position < _buffer.Length)
        {
            if (_buffer[_position] == 0)
            {
                result = System.Text.Encoding.UTF8.GetString(_buffer, start, _position - start);
                _position++;
                return true;
            }
            _position++;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Reads a null-terminated string from the packet.
    /// </summary>
    /// <exception cref="PacketException">Thrown if no null terminator is found.</exception>
    public string ReadString()
    {
        if (!TryReadString(out var result))
        {
            throw new PacketException("String is not null-terminated");
        }
        return result;
    }

    /// <summary>
    /// Skips a number of bytes in the packet.
    /// </summary>
    /// <exception cref="PacketException">Thrown if attempting to skip beyond packet bounds.</exception>
    public void Skip(int count)
    {
        if (count < 0 || _position + count > _buffer.Length)
        {
            throw new PacketException("Attempted to skip out of bounds");
        }
        _position += count;
    }
}
