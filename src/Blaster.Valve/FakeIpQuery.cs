// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.Net;
using SteamKit2.Internal;

namespace Blaster.Valve;

/// <summary>
/// Maps <c>QueryByFakeIP</c> responses (for SDR / fake-IP servers that can't be reached by UDP A2S)
/// into the same <see cref="ServerInfo"/> / rules shapes the rest of the pipeline uses.
/// </summary>
internal static class FakeIpMapper
{
    /// <summary>
    /// Projects ping query data into a <see cref="ServerInfo"/>. Note: QueryByFakeIP does not report
    /// the host OS, so <see cref="ServerInfo.OS"/> is left <see cref="ServerOS.Unknown"/>.
    /// </summary>
    public static ServerInfo ToServerInfo(CMsgGameServerPingQueryData ping, IPEndPoint endpoint)
    {
        var info = new ServerInfo
        {
            Address = endpoint.ToString(),
            Name = ping.server_name,
            MapName = ping.map,
            Folder = ping.gamedir,
            Game = ping.game_description,
            Players = (byte)ping.num_players,
            MaxPlayers = (byte)ping.max_players,
            Bots = (byte)ping.num_bots,
            Visibility = (byte)(ping.password ? 1 : 0),
            Vac = (byte)(ping.secure ? 1 : 0),
            Type = ping.dedicated ? ServerType.Dedicated : ServerType.Unknown,
            OS = ServerOS.Unknown,
            Ext = new ExtendedInfo
            {
                AppId = (AppId)ping.app_id,
                GameVersion = ping.version,
                SteamId = ping.steamid,
                GameModeDescription = ping.gametype,
                Port = (ushort)ping.game_port,
            },
        };

        if (ping.spectator_port != 0 || !string.IsNullOrEmpty(ping.spectator_server_name))
        {
            info.SpecTv = new SpecTvInfo
            {
                Port = (ushort)ping.spectator_port,
                Name = ping.spectator_server_name,
            };
        }

        return info;
    }

    /// <summary>
    /// Projects rules query data into a key/value dictionary (last value wins on duplicate keys).
    /// </summary>
    public static Dictionary<string, string> ToRules(CMsgGameServerRulesQueryData rules)
    {
        var result = new Dictionary<string, string>();
        foreach (var rule in rules.rules)
        {
            result[rule.rule] = rule.value;
        }
        return result;
    }

    /// <summary>
    /// True for the 169.254.0.0/16 link-local range Steam uses for SDR / fake-IP servers.
    /// </summary>
    public static bool IsFakeIp(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>
    /// Converts a 169.254.x.y address into the big-endian uint32 the QueryByFakeIP request expects.
    /// </summary>
    public static uint ToFakeIpValue(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
