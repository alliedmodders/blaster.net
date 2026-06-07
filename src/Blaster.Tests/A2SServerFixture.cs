// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.Net;
using Blaster.Valve;

namespace Blaster.Tests;

/// <summary>
/// Resolves a live, directly-addressable A2S server for the integration tests. Prefers an explicit
/// <c>BLASTER_TEST_SERVER_ADDRESS</c> (host:port); otherwise pulls one from the Steam master using
/// <c>BLASTER_TEST_STEAM_USERNAME</c>/<c>BLASTER_TEST_STEAM_PASSWORD</c> (a single master query, taking
/// the first non-SDR server). Only constructed when the Integration-category tests actually run.
/// </summary>
public sealed class A2SServerFixture : IAsyncLifetime
{
    public string ServerAddress { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("BLASTER_TEST_SERVER_ADDRESS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ServerAddress = configured.Trim();
            return;
        }

        var user = Environment.GetEnvironmentVariable("BLASTER_TEST_STEAM_USERNAME");
        var pass = Environment.GetEnvironmentVariable("BLASTER_TEST_STEAM_PASSWORD");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
        {
            throw new InvalidOperationException(
                "A2S integration tests need BLASTER_TEST_SERVER_ADDRESS (host:port), or " +
                "BLASTER_TEST_STEAM_USERNAME/PASSWORD to pull a live server from the Steam master.");
        }

        var pool = new SteamConnectionPool(null, user, pass, null);
        try
        {
            await pool.EnsureConnectedAsync();

            // A single direct master query (no fan-out); take the first directly-addressable (non-SDR) server.
            var appId = (uint)AppId.CSS;
            var records = await pool.QueryWithFilterAsync(appId, $"\\appid\\{appId}");
            var direct = records.FirstOrDefault(r => !IsFakeIp(r.EndPoint.Address));
            if (direct is null)
            {
                throw new InvalidOperationException("Steam master returned no directly-addressable servers to test against.");
            }

            ServerAddress = direct.EndPoint.ToString();
        }
        finally
        {
            pool.Dispose();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static bool IsFakeIp(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
