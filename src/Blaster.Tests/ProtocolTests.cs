// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Blaster.Valve;
using Blaster.Batch;

namespace Blaster.Tests;

public class PacketReaderTests
{
    [Fact]
    public void ReadUInt8_SucceedsWithValidData()
    {
        byte[] data = { 0x42 };
        var reader = new Blaster.Valve.PacketReader(data);
        
        var value = reader.ReadUInt8();
        
        Assert.Equal(0x42, value);
        Assert.Equal(1, reader.Position);
    }

    [Fact]
    public void ReadUInt8_ThrowsWhenOutOfBounds()
    {
        byte[] data = { };
        var reader = new Blaster.Valve.PacketReader(data);
        
        var ex = Assert.Throws<Blaster.Valve.PacketException>(() => reader.ReadUInt8());
        Assert.Contains("out of bounds", ex.Message);
    }

    [Fact]
    public void ReadUInt16_SucceedsWithValidData()
    {
        byte[] data = { 0x34, 0x12 }; // Little-endian: 0x1234
        var reader = new Blaster.Valve.PacketReader(data);
        
        var value = reader.ReadUInt16();
        
        Assert.Equal(0x1234, value);
        Assert.Equal(2, reader.Position);
    }

    [Fact]
    public void ReadUInt16_ThrowsWhenOutOfBounds()
    {
        byte[] data = { 0x42 };
        var reader = new Blaster.Valve.PacketReader(data);
        
        var ex = Assert.Throws<Blaster.Valve.PacketException>(() => reader.ReadUInt16());
        Assert.Contains("out of bounds", ex.Message);
    }

    [Fact]
    public void ReadUInt32_SucceedsWithValidData()
    {
        byte[] data = { 0x78, 0x56, 0x34, 0x12 }; // Little-endian: 0x12345678
        var reader = new Blaster.Valve.PacketReader(data);
        
        var value = reader.ReadUInt32();
        
        Assert.Equal(0x12345678u, value);
        Assert.Equal(4, reader.Position);
    }

    [Fact]
    public void ReadString_SucceedsWithNullTerminatedString()
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("hello\0world");
        var reader = new Blaster.Valve.PacketReader(data);
        
        var value = reader.ReadString();
        
        Assert.Equal("hello", value);
        Assert.Equal(6, reader.Position);
    }

    [Fact]
    public void ReadString_ThrowsWhenNotNullTerminated()
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("hello");
        var reader = new Blaster.Valve.PacketReader(data);
        
        var ex = Assert.Throws<Blaster.Valve.PacketException>(() => reader.ReadString());
        Assert.Contains("not null-terminated", ex.Message);
    }

    [Fact]
    public void TryReadString_ReturnsTrueWithValidString()
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("test\0");
        var reader = new Blaster.Valve.PacketReader(data);
        
        var success = reader.TryReadString(out var value);
        
        Assert.True(success);
        Assert.Equal("test", value);
    }

    [Fact]
    public void TryReadString_ReturnsFalseWithoutNull()
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("test");
        var reader = new Blaster.Valve.PacketReader(data);
        
        var success = reader.TryReadString(out var value);
        
        Assert.False(success);
        Assert.Null(value);
    }

    [Fact]
    public void ReadSlice_ReturnsCorrectBytes()
    {
        byte[] data = { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var reader = new Blaster.Valve.PacketReader(data);
        
        var slice = reader.ReadSlice(3);
        
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, slice);
        Assert.Equal(3, reader.Position);
    }

    [Fact]
    public void ReadSlice_ThrowsWhenOutOfBounds()
    {
        byte[] data = { 0x01, 0x02 };
        var reader = new Blaster.Valve.PacketReader(data);
        
        var ex = Assert.Throws<Blaster.Valve.PacketException>(() => reader.ReadSlice(5));
        Assert.Contains("out of bounds", ex.Message);
    }

    [Fact]
    public void Skip_AdvancesPosition()
    {
        byte[] data = { 0x01, 0x02, 0x03, 0x04 };
        var reader = new Blaster.Valve.PacketReader(data);
        
        reader.Skip(2);
        
        Assert.Equal(2, reader.Position);
    }

    [Fact]
    public void HasMore_ReturnsTrueWhenDataRemains()
    {
        byte[] data = { 0x01, 0x02 };
        var reader = new Blaster.Valve.PacketReader(data);
        
        Assert.True(reader.HasMore);
        reader.ReadUInt8();
        Assert.True(reader.HasMore);
        reader.ReadUInt8();
        Assert.False(reader.HasMore);
    }

    [Fact]
    public void ReadPort_ReadsPortInBigEndian()
    {
        byte[] data = { 0x69, 0x8F }; // Big-endian: 27023 (0x69 << 8 | 0x8F)
        var reader = new Blaster.Valve.PacketReader(data);
        
        var port = reader.ReadPort();
        
        Assert.Equal(27023u, port);
    }

    [Fact]
    public void ReadIPv4_ReadsAddressCorrectly()
    {
        byte[] data = { 192, 168, 1, 1 };
        var reader = new Blaster.Valve.PacketReader(data);
        
        var ip = reader.ReadIPv4();
        
        Assert.Equal("192.168.1.1", ip.ToString());
        Assert.Equal(4, reader.Position);
    }
}

public class PacketBuilderTests
{
    [Fact]
    public void WriteCString_EncodesStringWithNullTerminator()
    {
        var builder = new Blaster.Valve.PacketBuilder();
        
        builder.WriteCString("test");
        var data = builder.GetBytes();
        
        Assert.Equal(5, data.Length);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("test\0"), data);
    }

    [Fact]
    public void WriteUInt8_EncodesCorrectly()
    {
        var builder = new Blaster.Valve.PacketBuilder();
        
        builder.WriteUInt8(0x42);
        var data = builder.GetBytes();
        
        Assert.Equal(new byte[] { 0x42 }, data);
    }

    [Fact]
    public void WriteUInt16_EncodesLittleEndian()
    {
        var builder = new Blaster.Valve.PacketBuilder();
        
        builder.WriteUInt16(0x1234);
        var data = builder.GetBytes();
        
        Assert.Equal(new byte[] { 0x34, 0x12 }, data);
    }

    [Fact]
    public void WriteUInt32_EncodesLittleEndian()
    {
        var builder = new Blaster.Valve.PacketBuilder();
        
        builder.WriteUInt32(0x12345678);
        var data = builder.GetBytes();
        
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, data);
    }

    [Fact]
    public void WriteBytes_AppendsRawBytes()
    {
        var builder = new Blaster.Valve.PacketBuilder();
        
        builder.WriteBytes(new byte[] { 0x01, 0x02, 0x03 });
        var data = builder.GetBytes();
        
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, data);
    }

    [Fact]
    public void MultipleWrites_CombinesCorrectly()
    {
        var builder = new Blaster.Valve.PacketBuilder();
        
        builder.WriteUInt8(0x01);
        builder.WriteUInt16(0x0302);
        builder.WriteCString("hi");
        var data = builder.GetBytes();
        
        byte[] expected = { 0x01, 0x02, 0x03, 0x68, 0x69, 0x00 }; // 0x68='h', 0x69='i'
        Assert.Equal(expected, data);
    }
}

public class TypeTests
{
    [Fact]
    public void ServerTypeToDisplayString_ReturnsCorrectStrings()
    {
        Assert.Equal("dedicated", Blaster.Valve.ServerType.Dedicated.ToDisplayString());
        Assert.Equal("listen", Blaster.Valve.ServerType.Listen.ToDisplayString());
        Assert.Equal("hltv", Blaster.Valve.ServerType.HLTV.ToDisplayString());
        Assert.Equal("unknown", Blaster.Valve.ServerType.Unknown.ToDisplayString());
    }

    [Fact]
    public void ServerOSToDisplayString_ReturnsCorrectStrings()
    {
        Assert.Equal("windows", Blaster.Valve.ServerOS.Windows.ToDisplayString());
        Assert.Equal("linux", Blaster.Valve.ServerOS.Linux.ToDisplayString());
        Assert.Equal("mac", Blaster.Valve.ServerOS.Mac.ToDisplayString());
        Assert.Equal("unknown", Blaster.Valve.ServerOS.Unknown.ToDisplayString());
    }

    [Fact]
    public void ServerInfoDetermineGameEngine_GoldsrcWhenInfoVersionIsGoldsrc()
    {
        var info = new Blaster.Valve.ServerInfo 
        { 
            InfoVersion = Blaster.Valve.ValveConstants.S2A_INFO_GOLDSRC 
        };
        
        var engine = info.DetermineGameEngine();
        
        Assert.Equal(Blaster.Valve.GameEngine.Goldsrc, engine);
    }

    [Fact]
    public void ServerInfoDetermineGameEngine_GoldsrcWhenExtIsNull()
    {
        var info = new Blaster.Valve.ServerInfo 
        { 
            InfoVersion = Blaster.Valve.ValveConstants.S2A_INFO_SOURCE,
            Ext = null
        };
        
        var engine = info.DetermineGameEngine();
        
        Assert.Equal(Blaster.Valve.GameEngine.Goldsrc, engine);
    }

    [Fact]
    public void ServerInfoDetermineGameEngine_SourceWhenExtAppIdGreaterThan80()
    {
        var info = new Blaster.Valve.ServerInfo 
        { 
            InfoVersion = Blaster.Valve.ValveConstants.S2A_INFO_SOURCE,
            Ext = new Blaster.Valve.ExtendedInfo { AppId = Blaster.Valve.AppId.CSS }
        };
        
        var engine = info.DetermineGameEngine();
        
        Assert.Equal(Blaster.Valve.GameEngine.Source, engine);
    }
}
