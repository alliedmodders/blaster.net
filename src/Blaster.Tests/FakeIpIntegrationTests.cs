// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Blaster.Valve;
using SteamKit2.Internal;
using Xunit;
using Xunit.Abstractions;

namespace Blaster.Tests;

/// <summary>
/// Live test that SDR / fake-IP (169.254.*) servers — which ordinary UDP A2S can't reach — can be
/// queried for info over the CM connection via GameServers.QueryByFakeIP. Requires authenticated Steam
/// credentials (BLASTER_TEST_STEAM_USERNAME/PASSWORD).
/// </summary>
public class FakeIpIntegrationTests
{
    private readonly ITestOutputHelper _out;
    public FakeIpIntegrationTests(ITestOutputHelper output) => _out = output;

    [Trait("Category", "Integration")]
    [Fact]
    public async Task QueryByFakeIP_OverCm_ReturnsPingData()
    {
        var user = Environment.GetEnvironmentVariable("BLASTER_TEST_STEAM_USERNAME");
        var pass = Environment.GetEnvironmentVariable("BLASTER_TEST_STEAM_PASSWORD");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            throw new InvalidOperationException("BLASTER_TEST_STEAM_USERNAME/PASSWORD are required (QueryByFakeIP needs auth).");
        }

        var pool = new SteamConnectionPool(null, user, pass, null);
        await pool.EnsureConnectedAsync();

        // TF2 (440) has a large fraction of fake-IP servers; pull one straight from the master.
        var records = await pool.QueryWithFilterAsync(440, "\\appid\\440");
        var fake = records.FirstOrDefault(r => IsFakeIp(r.EndPoint.Address));
        Assert.NotNull(fake);

        var bytes = fake!.EndPoint.Address.GetAddressBytes();
        var fakeIp = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);

        var response = await pool.QueryByFakeIpAsync(
            fakeIp, (uint)fake.EndPoint.Port, 440, CGameServers_QueryByFakeIP_Request.EQueryType.Query_Ping);

        Assert.NotNull(response);
        Assert.NotNull(response!.ping_data);
        _out.WriteLine($"{fake.EndPoint} -> name='{response.ping_data.server_name}' map='{response.ping_data.map}' " +
                       $"players={response.ping_data.num_players}/{response.ping_data.max_players} secure={response.ping_data.secure}");
        Assert.False(string.IsNullOrEmpty(response.ping_data.map) && string.IsNullOrEmpty(response.ping_data.server_name));

        // The mapped path used by the CLI/AmStats consumers.
        var info = await pool.QueryFakeServerInfoAsync(fake.EndPoint, 440);
        Assert.NotNull(info);
        _out.WriteLine($"mapped ServerInfo: name='{info!.Name}' map='{info.MapName}' folder='{info.Folder}' " +
                       $"players={info.Players}/{info.MaxPlayers} appid={(int)(info.Ext?.AppId ?? 0)}");
        Assert.False(string.IsNullOrEmpty(info.MapName) && string.IsNullOrEmpty(info.Name));
    }

    private static bool IsFakeIp(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
