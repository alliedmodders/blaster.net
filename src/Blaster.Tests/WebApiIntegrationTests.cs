// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Blaster.Valve;
using Xunit;

namespace Blaster.Tests;

/// <summary>
/// Live end-to-end test of the Web API query source against IGameServersService/GetServerList.
/// Skipped unless BLASTER_TEST_STEAM_WEBAPI_KEY is set. Validates the SteamKit CallAsync -> VDF ->
/// MasterServerRecord path against the real service.
/// </summary>
public class WebApiIntegrationTests
{
    [Trait("Category", "Integration")]
    [Fact]
    public async Task WebApiSource_QueryWithFilter_ReturnsParsedServers()
    {
        var key = Environment.GetEnvironmentVariable("BLASTER_TEST_STEAM_WEBAPI_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Environment variable 'BLASTER_TEST_STEAM_WEBAPI_KEY' is required for this test.");
        }

        using var source = new WebApiQuerySource(key);

        // A narrow filter so the single query stays well under the cap.
        var filter = MasterServerQuerier.BuildFilter(10, [], ["\\name_match\\dust*"]);
        var results = await source.QueryWithFilterAsync(10, filter);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(AddressFamily.InterNetwork, r.EndPoint.Address.AddressFamily));
        Assert.All(results, r => Assert.InRange(r.EndPoint.Port, 1, 65535));
        Assert.Contains(results, r => !string.IsNullOrEmpty(r.Name));
    }
}
