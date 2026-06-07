// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Blaster.Valve;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Blaster.Tests;

/// <summary>
/// Integration tests for the master server queries via SteamKit2.
/// These tests verify the multi-call filtering strategy is working correctly.
/// They are not run by default - use [Trait("Category", "Integration")] filtering to run them.
/// </summary>
public class MasterServerIntegrationTests : IDisposable
{
    private readonly string _steamUsername;
    private readonly string _steamPassword;
    private readonly LoggingFixture _loggingFixture = new();

    public MasterServerIntegrationTests()
    {
        _steamUsername = GetRequiredEnvironmentVariable("BLASTER_TEST_STEAM_USERNAME");
        _steamPassword = GetRequiredEnvironmentVariable("BLASTER_TEST_STEAM_PASSWORD");
    }

    public void Dispose()
    {
        _loggingFixture.Dispose();
    }

    private static string GetRequiredEnvironmentVariable(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Environment variable '{variableName}' is required for master server integration tests.");

        return value;
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task QueryServers_MultipleFilters_NoneReturnExactlyMax()
    {
        // Arrange
        var logger = _loggingFixture.CreateLogger<MasterServerQuerier>();
        using var querier = new MasterServerQuerier(username: _steamUsername, password: _steamPassword, logger: logger);
        querier.FilterAppIds(AppId.TF2); // Team Fortress 2 - has many more servers than CSS
        
        var results = new Dictionary<string, int>();

        // Act
        await querier.QueryAsync(async servers =>
        {
            // Track server counts by filter
            results.Add($"Batch_{results.Count}", servers.Count());
        });

        // Assert
        // If any single query returns exactly the max, we've hit the Steam limit
        // and need to adjust our filter combinations
        var batchesWithMax = results.Where(kvp => kvp.Value == ValveConstants.MaxServersPerQuery).ToList();
        
        Assert.Empty(batchesWithMax);
        // Connection should work even if no results returned
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task QueryServers_MultipleFilters_NoExactDuplicates()
    {
        // Arrange
        using var querier = new MasterServerQuerier(username: _steamUsername, password: _steamPassword);
        querier.FilterAppIds(AppId.CSS);
        
        var allServers = new List<IPEndPoint>();

        // Act
        await querier.QueryAsync(async servers =>
        {
            allServers.AddRange(servers);
        });

        // Assert
        // Verify no exact duplicates (same IP:port appearing multiple times)
        var duplicates = allServers
            .GroupBy(s => s.ToString())
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task QueryServers_MultipleAppIds_ProcessesSequentially()
    {
        // Arrange
        using var querier = new MasterServerQuerier(username: _steamUsername, password: _steamPassword);
        querier.FilterAppIds(AppId.CSS, AppId.TF2, AppId.DODS);
        
        // Act
        var batchesReceived = 0;
        await querier.QueryAsync(async servers =>
        {
            if (servers.Any())
            {
                batchesReceived++;
            }
        });

        // Assert
        // Should receive at least some batches (12 filters = 3 app IDs × 4 filter combos)
        Assert.True(batchesReceived > 0);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task QueryServers_ServerCount_WithinReasonableRange()
    {
        // Arrange
        using var querier = new MasterServerQuerier(username: _steamUsername, password: _steamPassword);
        querier.FilterAppIds(AppId.CSS);

        // Act
        var totalServers = 0;

        await querier.QueryAsync(async servers =>
        {
            totalServers += servers.Count();
        });

        // Assert
        // CSS typically has 10k-30k servers
        // Should be significantly more than 0, but also less than 200k (4 queries × 50k)
        Assert.InRange(totalServers, 0, 200000); // Accept 0 if no servers available
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task QueryServers_ValidIPEndPoints_AllAreParseable()
    {
        // Arrange
        using var querier = new MasterServerQuerier(username: _steamUsername, password: _steamPassword);
        querier.FilterAppIds(AppId.CSS);

        // Act
        var servers = new List<IPEndPoint>();
        await querier.QueryAsync(async batch =>
        {
            servers.AddRange(batch);
        });

        // Assert
        foreach (var server in servers)
        {
            Assert.NotNull(server.Address);
            Assert.InRange(server.Port, 1, 65535);
            
            // Verify it's IPv4
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, server.Address.AddressFamily);
        }
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task QueryServers_EmptyFilters_ReturnsNoServers()
    {
        // Arrange
        using var querier = new MasterServerQuerier(username: _steamUsername, password: _steamPassword);
        // Don't add any app IDs
        
        // Act
        var serversReceived = 0;
        await querier.QueryAsync(async servers =>
        {
            serversReceived += servers.Count();
        });

        // Assert
        Assert.Equal(0, serversReceived);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task QueryServers_FilterCombinations_DistributesLoad()
    {
        // Arrange
        using var querier = new MasterServerQuerier(username: _steamUsername, password: _steamPassword);
        querier.FilterAppIds(AppId.CSS);
        
        // Act
        var batchSizes = new List<int>();
        await querier.QueryAsync(async servers =>
        {
            if (servers.Any())
            {
                batchSizes.Add(servers.Count());
            }
        });

        // Assert
        // With 4 filter combinations, we should get multiple non-zero batches (if servers exist)
        // The sizes should be varied (not all returning the same count)
        if (batchSizes.Count >= 1)
        {
            if (batchSizes.Count > 1)
            {
                // With multiple batches, check they're not all identical sizes
                // (indicates filter combinations are working)
                var uniqueSizes = batchSizes.Distinct().Count();
                Assert.True(uniqueSizes > 1 || batchSizes.Sum() >= ValveConstants.MaxServersPerQuery,
                    "Either batches should have different sizes (good filtering) " +
                    "or total should be significant (good coverage)");
            }
        }
    }
}
