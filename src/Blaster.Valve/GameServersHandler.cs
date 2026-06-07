// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.Net;
using System.Net.Sockets;
using SteamKit2;
using SteamKit2.Internal;

namespace Blaster.Valve;

/// <summary>
/// A single game server record as returned by the Steam game master server.
/// Unlike SteamKit's built-in <see cref="SteamMasterServer.QueryCallback"/> (which surfaces only
/// the endpoint), this carries the full per-server metadata in the GMS response — notably the
/// current <see cref="Map"/> — which lets the fan-out partition by map without any per-server
/// A2S probing.
/// </summary>
internal sealed class MasterServerRecord
{
    public required IPEndPoint EndPoint { get; init; }
    public string Map { get; init; } = "";
    public string GameDir { get; init; } = "";
    public string Name { get; init; } = "";
    public string GameType { get; init; } = "";
    public uint Players { get; init; }
    public uint MaxPlayers { get; init; }
    public uint Bots { get; init; }
    public uint AppId { get; init; }
}

/// <summary>
/// Custom SteamKit handler that issues the same GMS server query as
/// <see cref="SteamMasterServer"/> but surfaces the complete response payload
/// (<see cref="CMsgGMSClientServerQueryResponse"/>), including the per-response string table used
/// to de-duplicate map/gamedir/name values across servers.
/// </summary>
internal sealed class GameServersHandler : ClientMsgHandler
{
    /// <summary>
    /// Sends a server-list query. The response arrives as a <see cref="ServerQueryResponseCallback"/>
    /// whose <see cref="CallbackMsg.JobID"/> matches the returned id.
    /// </summary>
    public JobID ServerQuery(uint appId, string filter, uint maxServers)
    {
        var query = new ClientMsgProtobuf<CMsgClientGMSServerQuery>(EMsg.ClientGMSServerQuery)
        {
            SourceJobID = Client.GetNextJobID()
        };

        query.Body.app_id = appId;
        query.Body.filter_text = filter;
        query.Body.region_code = 0xFF; // all regions
        query.Body.max_servers = maxServers;

        Client.Send(query);
        return query.SourceJobID;
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType == EMsg.GMSClientServerQueryResponse)
        {
            Client.PostCallback(new ServerQueryResponseCallback(packetMsg));
        }
    }
}

/// <summary>
/// Full result of a <see cref="GameServersHandler.ServerQuery"/>, with the response's string table
/// resolved into each record.
/// </summary>
internal sealed class ServerQueryResponseCallback : CallbackMsg
{
    public IReadOnlyList<MasterServerRecord> Servers { get; }

    internal ServerQueryResponseCallback(IPacketMsg packetMsg)
    {
        var response = new ClientMsgProtobuf<CMsgGMSClientServerQueryResponse>(packetMsg);
        var body = response.Body;
        JobID = response.TargetJobID;

        var strings = body.server_strings;
        var list = new List<MasterServerRecord>(body.servers.Count);

        foreach (var s in body.servers)
        {
            if (!TryGetAddress(s, out var address))
            {
                continue;
            }

            list.Add(new MasterServerRecord
            {
                EndPoint = new IPEndPoint(address, (int)s.query_port),
                Map = GmsParse.ResolveString(s.map_str, s.map_strindex, s.ShouldSerializemap_strindex(), strings),
                GameDir = GmsParse.ResolveString(s.gamedir_str, s.gamedir_strindex, s.ShouldSerializegamedir_strindex(), strings),
                Name = GmsParse.ResolveString(s.name_str, s.name_strindex, s.ShouldSerializename_strindex(), strings),
                GameType = GmsParse.ResolveString(s.gametype_str, s.gametype_strindex, s.ShouldSerializegametype_strindex(), strings),
                Players = s.players,
                MaxPlayers = s.max_players,
                Bots = s.bots,
                AppId = s.app_id,
            });
        }

        Servers = list;
    }

    private static bool TryGetAddress(CMsgGMSClientServerQueryResponse.Server s, out IPAddress address)
    {
        if (s.server_ip != null)
        {
            address = s.server_ip.ShouldSerializev6()
                ? new IPAddress(s.server_ip.v6)
                : GmsParse.IPv4FromHostUInt(s.server_ip.v4);
            return true;
        }

        if (s.ShouldSerializedeprecated_server_ip())
        {
            address = GmsParse.IPv4FromHostUInt(s.deprecated_server_ip);
            return true;
        }

        address = IPAddress.None;
        return false;
    }
}

/// <summary>
/// Pure parsing helpers for GMS responses, factored out so they can be unit-tested without a live
/// Steam connection.
/// </summary>
internal static class GmsParse
{
    /// <summary>
    /// A response field is sent inline (<paramref name="inline"/>) or, when the same value repeats
    /// across servers, as an index (<paramref name="index"/>) into the response's shared string table.
    /// </summary>
    public static string ResolveString(string inline, uint index, bool hasIndex, IReadOnlyList<string> table)
        => hasIndex && index < (uint)table.Count ? table[(int)index] : inline;

    /// <summary>
    /// Mirrors SteamKit's internal <c>NetHelpers.GetIPAddress(uint)</c>: the wire value is a host-order
    /// 32-bit address that must be byte-swapped into the order <see cref="IPAddress"/> expects.
    /// </summary>
    public static IPAddress IPv4FromHostUInt(uint ip) => new(
        ((ip & 0xFF000000) >> 24) |
        ((ip & 0x00FF0000) >> 8) |
        ((ip & 0x0000FF00) << 8) |
        ((ip & 0x000000FF) << 24));
}
