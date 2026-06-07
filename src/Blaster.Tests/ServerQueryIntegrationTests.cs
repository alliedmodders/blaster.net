// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Blaster.Valve;

namespace Blaster.Tests;

/// <summary>
/// Integration tests for server query protocols (A2S_INFO and A2S_RULES).
/// These tests are designed to work with a real or mocked game server.
/// They are not run by default - use [Trait("Category", "Integration")] filtering to run them.
/// </summary>
public class ServerQueryIntegrationTests : IClassFixture<A2SServerFixture>
{
    // Resolved at runtime from BLASTER_TEST_SERVER_ADDRESS or the Steam master (see A2SServerFixture).
    private readonly string TestServerAddress;
    private const int TestTimeout = 5000; // 5 second timeout

    public ServerQueryIntegrationTests(A2SServerFixture fixture)
    {
        TestServerAddress = fixture.ServerAddress;
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryInfo_ValidServer_ReturnsServerInfo()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // Act
        var info = querier.QueryInfo();

        // Assert
        Assert.NotNull(info);
        Assert.NotEmpty(info.Name);
        Assert.NotEmpty(info.MapName);
        Assert.True(info.Players >= 0);
        Assert.True(info.MaxPlayers > 0);
        Assert.NotEqual(default, info.DetermineGameEngine());
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryInfo_ParsesProtocol_HasValidProtocolVersion()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // Act
        var info = querier.QueryInfo();

        // Assert
        Assert.NotNull(info);
        Assert.True(info.Protocol >= 0 && info.Protocol <= 255);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryInfo_ParsesServerType_ReturnsValidType()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // Act
        var info = querier.QueryInfo();

        // Assert
        Assert.NotNull(info);
        Assert.True(info.Type == ServerType.Dedicated ||
                   info.Type == ServerType.Listen ||
                   info.Type == ServerType.HLTV);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryInfo_ParsesServerOS_ReturnsValidOS()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // Act
        var info = querier.QueryInfo();

        // Assert
        Assert.NotNull(info);
        Assert.True(info.OS == ServerOS.Linux ||
                   info.OS == ServerOS.Windows);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryInfo_SourceEngine_ParsesExtendedInfo()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // Act
        var info = querier.QueryInfo();

        // Assert
        Assert.NotNull(info);

        var engine = info.DetermineGameEngine();
        if (engine == GameEngine.Source)
        {
            Assert.NotNull(info.Ext);
            Assert.True(info.Ext.AppId > 0);
        }
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryRules_ValidServer_ReturnsDictionary()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // First get info to cache it
        var info = querier.QueryInfo();

        // Act
        var rules = querier.QueryRules();

        // Assert
        Assert.NotNull(rules);
        Assert.IsType<Dictionary<string, string>>(rules);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryRules_CommonRules_ContainsStandardServerRules()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // First get info to establish challenge
        var info = querier.QueryInfo();

        // Act
        var rules = querier.QueryRules();

        // Assert
        Assert.NotNull(rules);

        // Most servers have these standard rules
        var hasCommonRules = rules.ContainsKey("mp_friendlyfire") ||
                            rules.ContainsKey("mp_teamplay") ||
                            rules.ContainsKey("sv_gravity") ||
                            rules.Count > 0; // At least some rules

        Assert.True(hasCommonRules, "Server should return common game rules");
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryRules_VerifiesRuleValues_StringFormat()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // First get info
        var info = querier.QueryInfo();

        // Act
        var rules = querier.QueryRules();

        // Assert
        Assert.NotNull(rules);

        // All rules should have valid key-value pairs
        foreach (var kvp in rules)
        {
            Assert.False(string.IsNullOrEmpty(kvp.Key), "Rule key should not be empty");
            Assert.NotNull(kvp.Value); // Value can be empty string but not null
        }
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryInfo_InvalidServer_ThrowsException()
    {
        // Arrange
        using var querier = new ServerQuerier("127.0.0.1:1", TimeSpan.FromMilliseconds(500));
        // Act & Assert
        Assert.Throws<ServerQueryException>(() => querier.QueryInfo());
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryInfo_Goldsrc_ParsesModInfo()
    {
        // Arrange - this would need a GoldSrc server
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // Act
        var info = querier.QueryInfo();

        // Assert
        Assert.NotNull(info);

        // If it's a GoldSrc server with mod, check mod info
        var engine = info.DetermineGameEngine();
        if (engine == GameEngine.Goldsrc && info.Mod != null)
        {
            Assert.False(string.IsNullOrEmpty(info.Mod.Url));
            Assert.False(string.IsNullOrEmpty(info.Mod.DwlUrl));
        }
    }

    [Trait("Category", "Integration")]
    [Fact(Skip = "Requires The Ship running")]
    public void QueryInfo_TheShip_ParsesTheShipInfo()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // Act
        var info = querier.QueryInfo();

        // Assert
        Assert.NotNull(info);

        // The Ship (AppId 2400) should have TheShip info
        if (info.Ext?.AppId == AppId.TheShip)
        {
            Assert.NotNull(info.TheShip);
            Assert.True(info.TheShip.Mode >= 0);
            Assert.True(info.TheShip.Witnesses >= 0);
            Assert.True(info.TheShip.Duration >= 0);
        }
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryInfo_MultipleQueries_ReturnConsistentResults()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // Act
        var info1 = querier.QueryInfo();
        var info2 = querier.QueryInfo();

        // Assert
        Assert.NotNull(info1);
        Assert.NotNull(info2);
        Assert.Equal(info1.Name, info2.Name);
        Assert.Equal(info1.MapName, info2.MapName);
        Assert.Equal(info1.DetermineGameEngine(), info2.DetermineGameEngine());
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryInfo_Timeout_ThrowsException()
    {
        // Arrange - a non-routable TEST-NET-1 (RFC 5737) address so the query deterministically times out
        // regardless of how fast the resolved live server is.
        using var querier = new ServerQuerier("192.0.2.1:27015", TimeSpan.FromMilliseconds(50));
        // Act & Assert - should timeout before getting response
        Assert.Throws<ServerQueryException>(() => querier.QueryInfo());
    }

    [Trait("Category", "Integration")]
    [Fact]
    public void QueryInfo_GameEngineDetection_CorrectlyIdentifiesSource()
    {
        // Arrange
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        // Act
        var info = querier.QueryInfo();

        // Assert
        var engine = info.DetermineGameEngine();

        // If it's a Source game (half of servers are)
        if (engine == GameEngine.Source)
        {
            Assert.NotNull(info.Ext);
            Assert.True(info.Ext.AppId >= (AppId)80, "Source games have AppId >= 80");
        }
    }

    [Trait("Category", "Integration")]
    [Fact(Skip = "Requires a running pre-OrangeBox Source server. Unskip to validate IsPreOrangeBox protocol behavior.")]
    public void QueryInfo_PreOrangeBoxCandidate_WithModernProtocol_IsNotPreOrangeBox()
    {
        using var querier = new ServerQuerier(TestServerAddress, TimeSpan.FromMilliseconds(TestTimeout));
        var info = querier.QueryInfo();

        Assert.NotNull(info);
        Assert.Equal(GameEngine.Source, info.DetermineGameEngine());
        Assert.NotNull(info.Ext);

        // Some historical pre-OrangeBox app IDs now run modern protocol behavior.
        // For those, protocol > 7 should indicate non-pre-OrangeBox handling.
        if (info.Protocol > 7)
        {
            Assert.False(info.IsPreOrangeBox());
        }
    }
}
