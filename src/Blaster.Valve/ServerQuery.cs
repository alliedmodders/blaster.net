// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.IO.Hashing;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace Blaster.Valve;

/// <summary>
/// Queries individual game servers for info, rules, and player data.
/// </summary>
public class ServerQuerier(string hostAndPort, TimeSpan timeout) : IDisposable
{
    private readonly UdpSocket _socket = new(hostAndPort, timeout);
    private readonly TimeSpan _timeout = timeout;
    private ServerInfo? _cachedInfo;

    /// <summary>
    /// Queries the server for info using A2S_INFO.
    /// </summary>
    public ServerInfo QueryInfo()
    {
        var info = new ServerInfo
        {
            Address = _socket.RemoteAddr
        };

        try
        {
            QueryInfoInternal(info);
        }
        catch (ServerQueryException ex) when (ex.Message == "mistaken reply")
        {
            // Half-Life 1 servers often reply with A2S_PLAYERS then newer A2S_INFO
            // Check for better responses with a short timeout
            try
            {
                CheckBadA2SInfo(info);
            }
            catch
            {
                // If that fails too, re-throw original error
                throw;
            }
        }

        _cachedInfo = info;
        return info;
    }

    private void QueryInfoInternal(ServerInfo info)
    {
        var packet = new PacketBuilder();
        packet.WriteBytes([0xff, 0xff, 0xff, 0xff, ValveConstants.A2S_INFO]);
        packet.WriteCString("Source Engine Query");
        _socket.Send(packet.GetBytes());

        byte[] response = _socket.Recv();

        // Check if we got a challenge response
        if (response.Length > 4 && response[4] == ValveConstants.S2C_CHALLENGE)
        {
            // Re-send with challenge
            var challengePacket = new PacketBuilder();
            challengePacket.WriteBytes([0xff, 0xff, 0xff, 0xff, ValveConstants.A2S_INFO]);
            challengePacket.WriteCString("Source Engine Query");
            challengePacket.WriteBytes([response[5], response[6], response[7], response[8]]);
            _socket.Send(challengePacket.GetBytes());

            response = _socket.Recv();
        }

        ParseA2SInfoReply(info, response);
    }

    private void CheckBadA2SInfo(ServerInfo info)
    {
        var oldTimeout = _timeout;
        _socket.SetTimeout(TimeSpan.FromMilliseconds(250));
        try
        {
            byte[] data1 = _socket.Recv();
            byte[] data2 = _socket.Recv();

            // Try to parse either as A2S_INFO
            try
            {
                ParseA2SInfoReply(info, data1);
                return;
            }
            catch { }

            ParseA2SInfoReply(info, data2);
        }
        finally
        {
            _socket.SetTimeout(oldTimeout);
        }
    }

    private static void ParseA2SInfoReply(ServerInfo info, byte[] data)
    {
        var reader = new PacketReader(data);

        if (reader.ReadInt32() != -1)
        {
            throw new ServerQueryException("Bad packet header");
        }

        info.InfoVersion = reader.ReadUInt8();

        switch (info.InfoVersion)
        {
            case ValveConstants.S2A_PLAYER:
                throw new ServerQueryException("mistaken reply");
            case ValveConstants.S2A_INFO_SOURCE:
                ParseNewInfo(reader, info);
                break;
            case ValveConstants.S2A_INFO_GOLDSRC:
                ParseOldInfo(reader, info);
                break;
            default:
                throw new ServerQueryException($"Unknown A2S_INFO version: {info.InfoVersion}");
        }
    }

    private static void ParseNewInfo(PacketReader reader, ServerInfo info)
    {
        info.Protocol = reader.ReadUInt8();
        info.Name = reader.ReadString();
        info.MapName = reader.ReadString();
        info.Folder = reader.ReadString();
        info.Game = reader.ReadString();

        var appId = (AppId)reader.ReadUInt16();

        info.Players = reader.ReadUInt8();
        info.MaxPlayers = reader.ReadUInt8();
        info.Bots = reader.ReadUInt8();

        byte serverType = reader.ReadUInt8();
        info.Type = serverType switch
        {
            (byte)'l' => ServerType.Listen,
            (byte)'d' => ServerType.Dedicated,
            _ => ServerType.Unknown
        };

        byte serverOS = reader.ReadUInt8();
        info.OS = serverOS switch
        {
            (byte)'l' => ServerOS.Linux,
            (byte)'w' => ServerOS.Windows,
            (byte)'m' => ServerOS.Mac,
            _ => ServerOS.Unknown
        };

        info.Visibility = reader.ReadUInt8();
        info.Vac = reader.ReadUInt8();

        // Read The Ship information if applicable
        if (appId == AppId.TheShip)
        {
            info.TheShip = new TheShipInfo
            {
                Mode = reader.ReadUInt8(),
                Witnesses = reader.ReadUInt8(),
                Duration = reader.ReadUInt8()
            };
        }

        info.Ext = new()
        {
            AppId = appId,         // Read extended information
            GameVersion = reader.ReadString()
        };

        if (!reader.HasMore)
        {
            return;
        }

        byte edf = reader.ReadUInt8();

        if ((edf & 0x80) != 0)
        {
            info.Ext.Port = reader.ReadUInt16();
        }

        if ((edf & 0x10) != 0)
        {
            info.Ext.SteamId = reader.ReadUInt64();
        }

        if ((edf & 0x40) != 0)
        {
            info.SpecTv = new SpecTvInfo
            {
                Port = reader.ReadUInt16(),
                Name = reader.ReadString()
            };
        }

        if ((edf & 0x20) != 0)
        {
            info.Ext.GameModeDescription = reader.ReadString();
        }

        if ((edf & 0x01) != 0)
        {
            ulong gameId = reader.ReadUInt64();
            // bits 0-31: true app id (original could be truncated)
            // bits 32-63: mod id
            info.Ext.AppId = (AppId)(gameId & 0xffffffff);
            info.Ext.GameId = gameId;
        }
    }

    private static void ParseOldInfo(PacketReader reader, ServerInfo info)
    {
        info.Address = reader.ReadString();
        info.Name = reader.ReadString();
        info.MapName = reader.ReadString();
        info.Folder = reader.ReadString();
        info.Game = reader.ReadString();
        info.Players = reader.ReadUInt8();
        info.MaxPlayers = reader.ReadUInt8();
        info.Protocol = reader.ReadUInt8();

        byte serverType = reader.ReadUInt8();
        info.Type = serverType switch
        {
            (byte)'l' => ServerType.Listen,
            (byte)'d' => ServerType.Dedicated,
            _ => ServerType.Unknown
        };

        byte serverOS = reader.ReadUInt8();
        info.OS = serverOS switch
        {
            (byte)'l' => ServerOS.Linux,
            (byte)'w' => ServerOS.Windows,
            _ => ServerOS.Unknown
        };

        info.Visibility = reader.ReadUInt8();

        byte isMod = reader.ReadUInt8();
        if (isMod == 1)
        {
            info.Mod = new ModInfo
            {
                Url = reader.ReadString(),
                DwlUrl = reader.ReadString()
            };
            reader.ReadUInt8(); // Ignore null byte
            info.Mod.Version = reader.ReadUInt32();
            info.Mod.Size = reader.ReadUInt32();
            info.Mod.Type = reader.ReadUInt8();
            info.Mod.Dll = reader.ReadUInt8();
        }

        info.Vac = reader.ReadUInt8();
        info.Bots = reader.ReadUInt8();
    }

    /// <summary>
    /// Queries the server for rules using A2S_RULES.
    /// </summary>
    public Dictionary<string, string> QueryRules()
    {
        byte[] data = QueryRulesInternal();

        // Check packet header type
        int header = BitConverter.ToInt32(data, 0);

        return header switch
        {
            -1 => ProcessRules(data, compressed: false),
            -2 => ProcessMultiPacketRules(data),
            _ => throw new ServerQueryException("Bad packet header in rules response")
        };
    }

    private byte[] QueryRulesInternal()
    {
        // Try to get a successful challenge, with up to 3 rechallenges
        byte[] data = QueryRulesWithChallenge();

        for (int i = 0; i < 3; i++)
        {
            try
            {
                return data;
            }
            catch (ServerQueryException ex) when (ex.Message == "confused challenge")
            {
                data = QueryRulesWithChallenge();
            }
        }

        return data;
    }

    private byte[] QueryRulesWithChallenge()
    {
        var query = new byte[]
        {
            0xff, 0xff, 0xff, 0xff,
            ValveConstants.A2S_RULES,
            0xff, 0xff, 0xff, 0xff
        };

        _socket.Send(query);
        byte[] data = _socket.Recv();

        // Sanity check the header
        int header = BitConverter.ToInt32(data, 0);
        if (header == -2)
        {
            return data; // Immediate rules reply
        }

        if (header != -1)
        {
            throw new ServerQueryException($"Bad packet header: {header}");
        }

        if (data.Length < 5)
        {
            throw new ServerQueryException("Rules response too short");
        }

        // Check for challenge byte
        switch (data[4])
        {
            case ValveConstants.S2A_RULES:
                // Immediate rules response
                return data;
            case ValveConstants.S2A_INFO_SOURCE:
            case ValveConstants.S2A_PLAYER:
                // Wrong reply type - retry
                throw new ServerQueryException("confused challenge");
            case ValveConstants.S2C_CHALLENGE:
                // Ok, continue with challenge
                break;
            default:
                throw new ServerQueryException($"Bad challenge response: {data[4]}");
        }

        // Send rules query with challenge
        var challengeReply = new byte[]
        {
            0xff, 0xff, 0xff, 0xff,
            ValveConstants.A2S_RULES,
            data[5], data[6], data[7], data[8]
        };

        _socket.Send(challengeReply);
        return _socket.Recv();
    }

    private Dictionary<string, string> ProcessMultiPacketRules(byte[] data)
    {
        var (fullData, compressed) = WaitForMultiPacketReply(data);
        return ProcessRules(fullData, compressed);
    }

    private (byte[], bool) WaitForMultiPacketReply(byte[] data)
    {
        if (_cachedInfo == null)
        {
            throw new ServerQueryException("Must query A2S_INFO first");
        }

        var header = DecodeMultiPacketHeader(_cachedInfo, data);
        var packets = new MultiPacketHeader?[header.TotalPackets];
        int received = 0;
        int fullSize = 0;

        while (true)
        {
            if (header.PacketNumber >= packets.Length)
            {
                throw new ServerQueryException("Packet number out of sequence");
            }

            if (packets[header.PacketNumber] != null)
            {
                throw new ServerQueryException("Duplicate packet");
            }

            packets[header.PacketNumber] = header;
            fullSize += header.Payload.Length;
            received++;

            if (received == packets.Length)
            {
                break;
            }

            byte[] nextData = _socket.Recv();
            header = DecodeMultiPacketHeader(_cachedInfo, nextData);
        }

        byte[] payload = new byte[fullSize];
        int cursor = 0;
        foreach (var pkt in packets)
        {
            if (pkt == null) continue;
            Array.Copy(pkt.Value.Payload, 0, payload, cursor, pkt.Value.Payload.Length);
            cursor += pkt.Value.Payload.Length;
        }

        return (payload, packets[0]?.Compressed ?? false);
    }

    private static MultiPacketHeader DecodeMultiPacketHeader(ServerInfo info, byte[] data)
    {
        var reader = new PacketReader(data);
        int header = reader.ReadInt32();

        if (header != -2)
        {
            throw new ServerQueryException("Bad packet header");
        }

        var multiHeader = new MultiPacketHeader
        {
            SequenceId = reader.ReadUInt32()
        };

        switch (info.DetermineGameEngine())
        {
            case GameEngine.Goldsrc:
                byte pkt = reader.ReadUInt8();
                multiHeader.PacketNumber = (byte)((pkt >> 4) & 0xf);
                multiHeader.TotalPackets = (byte)(pkt & 0xf);
                break;

            case GameEngine.Source:
                multiHeader.Compressed = (multiHeader.SequenceId & 0x80000000) != 0;
                multiHeader.TotalPackets = reader.ReadUInt8();
                multiHeader.PacketNumber = reader.ReadUInt8();
                if (!info.IsPreOrangeBox())
                {
                    multiHeader.PacketSize = reader.ReadUInt16();
                }
                break;

            default:
                throw new ServerQueryException("Unknown game engine");
        }

        multiHeader.HeaderSize = reader.Position;
        multiHeader.Payload = reader.RemainingBytes.ToArray();

        return multiHeader;
    }

    private static Dictionary<string, string> ProcessRules(byte[] data, bool compressed)
    {
        if (compressed)
        {
            var reader = new PacketReader(data);
            uint decompressedSize = reader.ReadUInt32();
            uint expectedChecksum = reader.ReadUInt32();

            // Sanity check to prevent massive allocations
            if (decompressedSize > 1024 * 1024)
            {
                throw new ServerQueryException("Decompressed size too large");
            }

            using var compressedStream = new MemoryStream(reader.RemainingBytes.ToArray());
            using var bzipStream = new BZip2Stream(compressedStream, CompressionMode.Decompress, false);
            using var decompressedStream = new MemoryStream((int)decompressedSize);
            bzipStream.CopyTo(decompressedStream);
            data = decompressedStream.ToArray();

            if ((uint)data.Length != decompressedSize)
            {
                throw new ServerQueryException("Decompressed rules size mismatch");
            }

            uint actualChecksum = Crc32.HashToUInt32(data);
            if (actualChecksum != expectedChecksum)
            {
                throw new ServerQueryException("Decompressed rules checksum mismatch");
            }
        }

        var rulesReader = new PacketReader(data);

        if (rulesReader.ReadInt32() != -1)
        {
            throw new ServerQueryException("Bad packet header in rules");
        }

        if (rulesReader.ReadUInt8() != ValveConstants.S2A_RULES)
        {
            throw new ServerQueryException("Bad rules reply");
        }

        int count = rulesReader.ReadUInt16();
        var rules = new Dictionary<string, string>();

        for (int i = 0; i < count; i++)
        {
            if (!rulesReader.TryReadString(out var key))
            {
                break;
            }

            if (!rulesReader.TryReadString(out var value))
            {
                break;
            }

            rules[key] = value;
        }

        return rules;
    }

    private struct MultiPacketHeader
    {
        public uint SequenceId { get; set; }
        public byte PacketNumber { get; set; }
        public byte TotalPackets { get; set; }
        public ushort PacketSize { get; set; }
        public bool Compressed { get; set; }
        public int HeaderSize { get; set; }
        public byte[] Payload { get; set; }
    }

    public void Close()
    {
        _socket?.Close();
    }

    public void Dispose()
    {
        _socket?.Dispose();
        GC.SuppressFinalize(this);
    }
}
