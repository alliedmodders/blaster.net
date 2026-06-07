// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Blaster.Valve;
using System;
using Xunit;

namespace Blaster.Tests;

/// <summary>
/// Edge case tests for protocol parsing across different engine variants (GoldSrc, Source, The Ship).
/// These tests ensure robustness when dealing with malformed packets, boundary conditions, and engine-specific quirks.
/// </summary>
public class ProtocolEdgeCaseTests
{
    #region PacketReader Edge Cases

    [Fact]
    public void ReadString_WithEmptyString_ReturnsEmptyStringAndAdvancesPosition()
    {
        byte[] data = { 0x00 }; // Just null terminator
        var reader = new PacketReader(data);
        
        var value = reader.ReadString();
        
        Assert.Equal("", value);
        Assert.Equal(1, reader.Position);
    }

    [Fact]
    public void ReadString_WithUTF8Multibyte_ParsesCorrectly()
    {
        // UTF-8 encoded: "café" (é is multi-byte: 0xC3 0xA9)
        byte[] data = System.Text.Encoding.UTF8.GetBytes("café\0");
        var reader = new PacketReader(data);
        
        var value = reader.ReadString();
        
        Assert.Equal("café", value);
    }

    [Fact]
    public void ReadString_WithSpecialCharacters_PreservesContent()
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("test\tstring\nwith\rspecial\0");
        var reader = new PacketReader(data);
        
        var value = reader.ReadString();
        
        Assert.Equal("test\tstring\nwith\rspecial", value);
    }

    [Fact]
    public void ReadString_WithVeryLongString_ReadsSuccessfully()
    {
        // 10KB string
        string longString = new string('x', 10000);
        byte[] data = System.Text.Encoding.UTF8.GetBytes(longString + "\0");
        var reader = new PacketReader(data);
        
        var value = reader.ReadString();
        
        Assert.Equal(longString, value);
    }

    [Fact]
    public void ReadSlice_WithZeroLength_ReturnsEmptyArray()
    {
        byte[] data = { 0x01, 0x02, 0x03 };
        var reader = new PacketReader(data);
        
        var slice = reader.ReadSlice(0);
        
        Assert.Empty(slice);
        Assert.Equal(0, reader.Position);
    }

    [Fact]
    public void ReadSlice_WithExactDataLength_ReadsAll()
    {
        byte[] data = { 0x01, 0x02, 0x03 };
        var reader = new PacketReader(data);
        
        var slice = reader.ReadSlice(3);
        
        Assert.Equal(data, slice);
        Assert.False(reader.HasMore);
    }

    [Fact]
    public void Skip_PastEndOfData_ThrowsException()
    {
        byte[] data = { 0x01, 0x02 };
        var reader = new PacketReader(data);
        
        var ex = Assert.Throws<PacketException>(() => reader.Skip(5));
        Assert.Contains("out of bounds", ex.Message);
    }

    [Fact]
    public void ReadUInt32_AtExactBoundary_Succeeds()
    {
        byte[] data = { 0x78, 0x56, 0x34, 0x12 };
        var reader = new PacketReader(data);
        reader.Skip(0);
        
        var value = reader.ReadUInt32();
        
        Assert.Equal(0x12345678u, value);
        Assert.False(reader.HasMore);
    }

    [Fact]
    public void ReadIPv4_AllZeros_ReturnsZeroAddress()
    {
        byte[] data = { 0, 0, 0, 0 };
        var reader = new PacketReader(data);
        
        var ip = reader.ReadIPv4();
        
        Assert.Equal("0.0.0.0", ip.ToString());
    }

    [Fact]
    public void ReadIPv4_AllOnes_ReturnsMaxAddress()
    {
        byte[] data = { 255, 255, 255, 255 };
        var reader = new PacketReader(data);
        
        var ip = reader.ReadIPv4();
        
        Assert.Equal("255.255.255.255", ip.ToString());
    }

    [Fact]
    public void ReadPort_WithZero_SucceedsWithZero()
    {
        byte[] data = { 0x00, 0x00 };
        var reader = new PacketReader(data);
        
        var port = reader.ReadPort();
        
        Assert.Equal(0u, port);
    }

    [Fact]
    public void ReadPort_WithMaxValue_SucceedsWithMaxPort()
    {
        byte[] data = { 0xFF, 0xFF }; // 65535
        var reader = new PacketReader(data);
        
        var port = reader.ReadPort();
        
        Assert.Equal(65535u, port);
    }

    [Fact]
    public void TryReadString_WithMultipleNulls_StopsAtFirst()
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("test\0\0more");
        var reader = new PacketReader(data);
        
        var success = reader.TryReadString(out var value);
        
        Assert.True(success);
        Assert.Equal("test", value);
        Assert.Equal(5, reader.Position);
    }

    #endregion

    #region PacketBuilder Edge Cases

    [Fact]
    public void WriteCString_WithEmptyString_WritesOnlyNullTerminator()
    {
        var builder = new PacketBuilder();
        
        builder.WriteCString("");
        var data = builder.GetBytes();
        
        Assert.Single(data);
        Assert.Equal(0x00, data[0]);
    }

    [Fact]
    public void WriteCString_WithSpecialCharacters_PreservesContent()
    {
        var builder = new PacketBuilder();
        
        builder.WriteCString("test\tstring\nwith\rspecial");
        var data = builder.GetBytes();
        
        var result = System.Text.Encoding.UTF8.GetString(data, 0, data.Length - 1);
        Assert.Equal("test\tstring\nwith\rspecial", result);
        Assert.Equal(0x00, data[data.Length - 1]);
    }

    [Fact]
    public void WriteCString_WithUTF8Multibyte_EncodesCorrectly()
    {
        var builder = new PacketBuilder();
        
        builder.WriteCString("café");
        var data = builder.GetBytes();
        
        var expected = System.Text.Encoding.UTF8.GetBytes("café\0");
        Assert.Equal(expected, data);
    }

    [Fact]
    public void WriteUInt16_WithZero_EncodesAsZeros()
    {
        var builder = new PacketBuilder();
        
        builder.WriteUInt16(0);
        var data = builder.GetBytes();
        
        Assert.Equal(new byte[] { 0x00, 0x00 }, data);
    }

    [Fact]
    public void WriteUInt16_WithMaxValue_EncodesCorrectly()
    {
        var builder = new PacketBuilder();
        
        builder.WriteUInt16(0xFFFF);
        var data = builder.GetBytes();
        
        Assert.Equal(new byte[] { 0xFF, 0xFF }, data);
    }

    [Fact]
    public void WriteUInt32_WithZero_EncodesAsZeros()
    {
        var builder = new PacketBuilder();
        
        builder.WriteUInt32(0);
        var data = builder.GetBytes();
        
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, data);
    }

    [Fact]
    public void WriteUInt32_WithMaxValue_EncodesCorrectly()
    {
        var builder = new PacketBuilder();
        
        builder.WriteUInt32(0xFFFFFFFF);
        var data = builder.GetBytes();
        
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, data);
    }

    [Fact]
    public void WriteBytes_WithEmpty_DoesNotChangeBuffer()
    {
        var builder = new PacketBuilder();
        builder.WriteUInt8(0x42);
        
        builder.WriteBytes(Array.Empty<byte>());
        var data = builder.GetBytes();
        
        Assert.Single(data);
        Assert.Equal(0x42, data[0]);
    }

    [Fact]
    public void ChainedWrites_WithLargeData_CombinesCorrectly()
    {
        var builder = new PacketBuilder();
        
        for (int i = 0; i < 100; i++)
        {
            builder.WriteUInt8((byte)i);
        }
        var data = builder.GetBytes();
        
        Assert.Equal(100, data.Length);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal((byte)i, data[i]);
        }
    }

    #endregion

    #region Engine-Specific Protocol Variants

    [Fact]
    public void GoldsrcServerInfo_WithInfoVersion_IsIdentifiedCorrectly()
    {
        var info = new ServerInfo
        {
            InfoVersion = ValveConstants.S2A_INFO_GOLDSRC
        };
        
        var engine = info.DetermineGameEngine();
        
        Assert.Equal(GameEngine.Goldsrc, engine);
    }

    [Fact]
    public void SourceServerInfo_WithSourceVersion_RequiresExtendedInfo()
    {
        var info = new ServerInfo
        {
            InfoVersion = ValveConstants.S2A_INFO_SOURCE,
            Ext = null // No extended info
        };
        
        var engine = info.DetermineGameEngine();
        
        // Falls back to Goldsrc when ext is missing
        Assert.Equal(GameEngine.Goldsrc, engine);
    }

    [Fact]
    public void SourceServerInfo_WithSourceVersionAndAppId_IsIdentifiedCorrectly()
    {
        var info = new ServerInfo
        {
            InfoVersion = ValveConstants.S2A_INFO_SOURCE,
            Ext = new ExtendedInfo
            {
                AppId = AppId.CSS
            }
        };
        
        var engine = info.DetermineGameEngine();
        
        Assert.Equal(GameEngine.Source, engine);
    }

    [Fact]
    public void TheShipServerInfo_WithTheShipAppId_DetectsCorrectly()
    {
        var info = new ServerInfo
        {
            InfoVersion = ValveConstants.S2A_INFO_SOURCE,
            Ext = new ExtendedInfo
            {
                AppId = AppId.TheShip
            },
            TheShip = new TheShipInfo()
        };
        
        var engine = info.DetermineGameEngine();
        
        Assert.Equal(GameEngine.Source, engine);
        Assert.NotNull(info.TheShip);
    }

    #endregion

    #region Type Conversion Edge Cases

    [Fact]
    public void ServerTypeToDisplayString_AllValues_ReturnValidStrings()
    {
        var types = new[] 
        { 
            ServerType.Unknown, 
            ServerType.Dedicated, 
            ServerType.Listen, 
            ServerType.HLTV 
        };
        
        foreach (var type in types)
        {
            var display = type.ToDisplayString();
            Assert.NotNull(display);
            Assert.NotEmpty(display);
        }
    }

    [Fact]
    public void ServerOSToDisplayString_AllValues_ReturnValidStrings()
    {
        var oses = new[]
        {
            ServerOS.Unknown,
            ServerOS.Windows,
            ServerOS.Linux,
            ServerOS.Mac
        };
        
        foreach (var os in oses)
        {
            var display = os.ToDisplayString();
            Assert.NotNull(display);
            Assert.NotEmpty(display);
        }
    }

    #endregion

    #region Boundary and Stress Tests

    [Fact]
    public void PacketReader_SequentialReads_MaintainsCorrectPosition()
    {
        byte[] data = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var reader = new PacketReader(data);
        
        Assert.Equal(0x01, reader.ReadUInt8());
        Assert.Equal(1, reader.Position);
        
        Assert.Equal(0x0302u, reader.ReadUInt16());
        Assert.Equal(3, reader.Position);
        
        Assert.Equal(0x07060504u, reader.ReadUInt32());
        Assert.Equal(7, reader.Position);
    }

    [Fact]
    public void PacketReader_MultipleStringReads_TracksPositionCorrectly()
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes("first\0second\0third\0");
        var reader = new PacketReader(data);
        
        var str1 = reader.ReadString();
        var str2 = reader.ReadString();
        var str3 = reader.ReadString();
        
        Assert.Equal("first", str1);
        Assert.Equal("second", str2);
        Assert.Equal("third", str3);
        Assert.False(reader.HasMore);
    }

    [Fact]
    public void PacketBuilder_LargePacket_BuildsSuccessfully()
    {
        var builder = new PacketBuilder();
        
        // Build a large packet with 1000 uint32s
        for (int i = 0; i < 1000; i++)
        {
            builder.WriteUInt32((uint)i);
        }
        
        var data = builder.GetBytes();
        
        Assert.Equal(4000, data.Length);
    }

    [Fact]
    public void PacketRoundTrip_WriteAndRead_PreservesData()
    {
        var builder = new PacketBuilder();
        builder.WriteUInt8(0x42);
        builder.WriteUInt16(0x1234);
        builder.WriteUInt32(0x56789ABC);
        builder.WriteCString("test");
        
        byte[] data = builder.GetBytes();
        var reader = new PacketReader(data);
        
        Assert.Equal(0x42, reader.ReadUInt8());
        Assert.Equal(0x1234u, reader.ReadUInt16());
        Assert.Equal(0x56789ABCu, reader.ReadUInt32());
        Assert.Equal("test", reader.ReadString());
    }

    #endregion

    #region Protocol Constants Validation

    [Fact]
    public void ValveConstants_PacketTypes_AreNonZero()
    {
        Assert.NotEqual(0u, ValveConstants.A2S_INFO);
        Assert.NotEqual(0u, ValveConstants.A2S_RULES);
        Assert.NotEqual(0u, ValveConstants.S2C_CHALLENGE);
        Assert.NotEqual(0u, ValveConstants.S2A_PLAYER);
    }

    [Fact]
    public void ValveConstants_InfoVersions_AreDifferent()
    {
        Assert.NotEqual(ValveConstants.S2A_INFO_GOLDSRC, ValveConstants.S2A_INFO_SOURCE);
    }

    [Fact]
    public void ValveConstants_MaxPacketSize_IsPositive()
    {
        Assert.True(ValveConstants.MaxPacketSize > 0);
    }

    #endregion
}
