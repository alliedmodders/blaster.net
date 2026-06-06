// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using Blaster.Valve;
using SteamKit2;
using Xunit;

namespace Blaster.Tests;

/// <summary>
/// Tests the Web API GetServerList VDF -> MasterServerRecord mapping (the part that doesn't need an
/// HTTP round-trip), modelled on a real IGameServersService/GetServerList response.
/// </summary>
public class WebApiQuerySourceTests
{
    private static KeyValue ParseVdf(string vdf)
    {
        var kv = new KeyValue();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(vdf));
        Assert.True(kv.ReadAsText(stream));
        return kv;
    }

    [Fact]
    public void ParseServerList_MapsFieldsAndSkipsUnparseableAddrs()
    {
        // Mirrors the live response shape: root "response" -> "servers" -> indexed entries.
        const string vdf = """
        "response"
        {
            "servers"
            {
                "0"
                {
                    "addr"        "34.162.187.87:27383"
                    "gameport"    "27383"
                    "name"        "AWPLOUNGE.CSCLASSIC.NET # AWP only"
                    "appid"       "10"
                    "gamedir"     "cstrike"
                    "players"     "5"
                    "max_players" "255"
                    "bots"        "2"
                    "map"         "de_dust2"
                    "secure"      "0"
                    "dedicated"   "1"
                    "os"          "l"
                }
                "1"
                {
                    "addr"        "5.6.7.8:27016"
                    "name"        "Another Server"
                    "appid"       "10"
                    "map"         "cs_office"
                    "players"     "0"
                    "max_players" "16"
                }
                "2"
                {
                    "addr"        "not-an-endpoint"
                    "name"        "Broken"
                    "map"         "de_nuke"
                }
            }
        }
        """;

        var records = WebApiQuerySource.ParseServerList(ParseVdf(vdf));

        // The third entry has an unparseable addr and is dropped.
        Assert.Equal(2, records.Count);

        var first = records[0];
        Assert.Equal("34.162.187.87:27383", first.EndPoint.ToString());
        Assert.Equal("de_dust2", first.Map);
        Assert.Equal("cstrike", first.GameDir);
        Assert.Equal("AWPLOUNGE.CSCLASSIC.NET # AWP only", first.Name);
        Assert.Equal(5u, first.Players);
        Assert.Equal(255u, first.MaxPlayers);
        Assert.Equal(2u, first.Bots);
        Assert.Equal(10u, first.AppId);

        var second = records[1];
        Assert.Equal("5.6.7.8:27016", second.EndPoint.ToString());
        Assert.Equal("cs_office", second.Map);
        Assert.Equal(0u, second.Players);
        Assert.Equal("", second.GameDir); // absent field -> empty
    }

    [Fact]
    public void ParseServerList_EmptyResponse_ReturnsEmpty()
    {
        var records = WebApiQuerySource.ParseServerList(ParseVdf("\"response\"\n{\n}\n"));
        Assert.Empty(records);
    }

    [Fact]
    public void ParseRetryAfter_NullHeader_ReturnsNull()
    {
        Assert.Null(WebApiQuerySource.ParseRetryAfter(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ParseRetryAfter_DeltaSeconds_ReturnsDelta()
    {
        var header = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), WebApiQuerySource.ParseRetryAfter(header, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ParseRetryAfter_FutureDate_ReturnsPositiveDelay()
    {
        var now = DateTimeOffset.UtcNow;
        var header = new RetryConditionHeaderValue(now.AddSeconds(45));
        var delay = WebApiQuerySource.ParseRetryAfter(header, now);
        Assert.NotNull(delay);
        Assert.InRange(delay!.Value, TimeSpan.FromSeconds(44), TimeSpan.FromSeconds(46));
    }

    [Fact]
    public void ParseRetryAfter_PastDate_ReturnsZero()
    {
        var now = DateTimeOffset.UtcNow;
        var header = new RetryConditionHeaderValue(now.AddSeconds(-10));
        Assert.Equal(TimeSpan.Zero, WebApiQuerySource.ParseRetryAfter(header, now));
    }
}
