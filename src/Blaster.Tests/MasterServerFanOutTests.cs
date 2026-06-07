// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Blaster.Valve;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Blaster.Tests;

/// <summary>
/// Exercises <see cref="MasterServerQuerier"/>'s fan-out end-to-end against a simulated master server
/// that enforces the same 10k-per-query cap as Steam. These tests guard the properties that broke
/// during development: completeness (every server is delivered), de-duplication, and termination
/// (the fan-out doesn't spin on indistinguishable clusters).
/// </summary>
public class MasterServerFanOutTests
{
    private const uint Cap = ValveConstants.MaxServersPerQuery;
    private static readonly AppId TestApp = (AppId)10;

    private static async Task<(List<IPEndPoint> Delivered, SimulatedMasterServer Sim)> RunAsync(IEnumerable<SimServer> population)
    {
        var sim = new SimulatedMasterServer(population);
        var querier = new MasterServerQuerier(sim);
        querier.FilterAppIds(TestApp);

        var delivered = new List<IPEndPoint>();
        await querier.QueryAsync(servers =>
        {
            delivered.AddRange(servers);
            return Task.CompletedTask;
        });

        return (delivered, sim);
    }

    [Fact]
    public async Task UnderCap_DeliversEverythingInOneQuery()
    {
        var population = Enumerable.Range(0, 300)
            .Select(i => SimServer.Community(10, $"1.2.{i / 256}.{i % 256}", 27015, $"Server {i}", "de_dust2"))
            .ToList();

        var (delivered, sim) = await RunAsync(population);

        Assert.Equal(population.Count, delivered.Count);
        Assert.Equal(population.Count, delivered.Distinct().Count());
        Assert.Equal(1, sim.QueryCount); // tier 1 only; no fan-out needed
    }

    [Fact]
    public async Task OverCap_SplitsByMap_AndDeliversAllMapsIncludingTheCatchAll()
    {
        // 25k empty+linux servers across three maps; one map only shows up in the NOR catch-all.
        var population = new List<SimServer>();
        var idx = 0;
        foreach (var (map, count) in new[] { ("de_dust2", 9000), ("de_inferno", 9000), ("de_nuke", 7000) })
        {
            for (var i = 0; i < count; i++, idx++)
            {
                population.Add(SimServer.Empty(10, $"10.{idx / 65536 % 256}.{idx / 256 % 256}.{idx % 256}", 27015 + idx % 1000, $"{map} #{idx}", map));
            }
        }

        var (delivered, sim) = await RunAsync(population);

        Assert.Equal(population.Count, delivered.Distinct().Count()); // complete
        Assert.Equal(delivered.Count, delivered.Distinct().Count());  // de-duplicated
        Assert.Contains(sim.Filters, f => f.Contains("\\map\\de_dust2"));
        Assert.True(sim.QueryCount < 25, $"expected a handful of queries, got {sim.QueryCount}");
    }

    [Fact]
    public async Task IndistinguishableNameCluster_IsFoldedByIp_Completely_WithoutSpinning()
    {
        // ~23k servers identical except for address: one name, one map, spread over 29 hosts (~800
        // ports each), interleaved across hosts the way Steam returns them — plus a diverse tail.
        const int hosts = 29;
        const int portsPerHost = 800;
        var population = new List<SimServer>();
        for (var port = 0; port < portsPerHost; port++)
        {
            for (var host = 0; host < hosts; host++)
            {
                population.Add(SimServer.Empty(10, $"5.5.0.{host}", 20000 + port, "ROMANIA.CS1.RO", "de_dust2"));
            }
        }

        var romaniaCount = population.Count;
        for (var i = 0; i < 1500; i++)
        {
            population.Add(SimServer.Empty(10, $"6.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}", 27015 + i % 500, $"Diverse #{i}", "de_dust2"));
        }

        var (delivered, sim) = await RunAsync(population);

        // Every server captured, including all of the cluster.
        Assert.Equal(population.Count, delivered.Distinct().Count());
        Assert.Equal(delivered.Count, delivered.Distinct().Count());

        var romaniaDelivered = delivered.Count(e => e.Address.ToString().StartsWith("5.5.0."));
        Assert.Equal(romaniaCount, romaniaDelivered);

        // The cluster was resolved by IP enumeration, not by drilling the name to the bottom.
        Assert.Contains(sim.Filters, f => f.Contains("\\gameaddr\\"));
        Assert.True(sim.QueryCount < 100, $"fan-out should not spin; got {sim.QueryCount} queries");

        // \or\-batching keeps the per-host query count well below the host count (29).
        var gameaddrQueries = sim.Filters.Count(f => f.Contains("\\gameaddr\\"));
        Assert.True(gameaddrQueries < 12, $"gameaddr queries should be batched; got {gameaddrQueries}");
    }

    [Fact]
    public async Task PrefixSharingNeighbor_SurvivesClusterExclusion()
    {
        // A dominant cluster plus one distinct server whose name *starts with* the cluster name, on its
        // own host. The exact-match exclusion must not drop the neighbour (regression for the trailing-*
        // over-exclusion bug).
        var population = new List<SimServer>();
        for (var port = 0; port < 800; port++)
        {
            for (var host = 0; host < 20; host++)
            {
                population.Add(SimServer.Empty(10, $"5.5.0.{host}", 20000 + port, "ROMANIA.CS1.RO", "de_dust2"));
            }
        }
        population.Add(SimServer.Empty(10, "7.7.7.7", 27015, "ROMANIA.CS1.RO.VIP", "de_dust2"));

        var (delivered, _) = await RunAsync(population);

        Assert.Equal(population.Count, delivered.Distinct().Count());
        Assert.Contains(delivered, e => e.Address.ToString() == "7.7.7.7");
    }

    [Fact]
    public async Task OverlappingClusters_AreBothFoldedCompletely()
    {
        // Two dominant clusters whose names share a prefix ("CS" vs "CSDM"), on disjoint hosts. Exact-match
        // exclusion must let the second fold succeed (regression for prefix-overlap exclusion).
        var population = new List<SimServer>();
        for (var port = 0; port < 600; port++)
        {
            for (var host = 0; host < 10; host++)
            {
                population.Add(SimServer.Empty(10, $"4.4.0.{host}", 20000 + port, "CS", "de_dust2"));
                population.Add(SimServer.Empty(10, $"8.8.0.{host}", 20000 + port, "CSDM", "de_dust2"));
            }
        }

        var (delivered, _) = await RunAsync(population);

        Assert.Equal(population.Count, delivered.Distinct().Count());
        Assert.Contains(delivered, e => e.Address.ToString().StartsWith("4.4.0."));
        Assert.Contains(delivered, e => e.Address.ToString().StartsWith("8.8.0."));
    }

    [Fact]
    public async Task FanOut_LogsPerAppQueryMetrics_MatchingActualQueryCount()
    {
        // Over-cap, multi-map population so the fan-out issues several queries.
        var population = new List<SimServer>();
        var idx = 0;
        foreach (var (map, count) in new[] { ("de_dust2", 9000), ("de_inferno", 9000), ("de_nuke", 7000) })
        {
            for (var i = 0; i < count; i++, idx++)
            {
                population.Add(SimServer.Empty(10, $"10.{idx / 65536 % 256}.{idx / 256 % 256}.{idx % 256}", 27015 + idx % 1000, $"{map} #{idx}", map));
            }
        }

        var sim = new SimulatedMasterServer(population);
        var logger = new CapturingLogger<MasterServerQuerier>();
        var querier = new MasterServerQuerier(sim, logger);
        querier.FilterAppIds(TestApp);
        await querier.QueryAsync(_ => Task.CompletedTask);

        var perApp = logger.Messages.SingleOrDefault(m => m.Contains("fan-out:"));
        Assert.NotNull(perApp);
        // The instrumented total must equal the queries the simulator actually served.
        Assert.Contains($"{sim.QueryCount} queries", perApp!);

        var complete = logger.Messages.SingleOrDefault(m => m.Contains("Query complete"));
        Assert.NotNull(complete);
        Assert.Contains($"{sim.QueryCount} master queries", complete!);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}

/// <summary>A synthetic game server used by <see cref="SimulatedMasterServer"/>.</summary>
internal sealed record SimServer(
    uint AppId, string Ip, int Port, string Name, string Map, bool Linux, int Players, int MaxPlayers, string[] Tags)
{
    /// Community (non-"valve"-tagged) server with players on it.
    public static SimServer Community(uint appId, string ip, int port, string name, string map)
        => new(appId, ip, port, name, map, Linux: true, Players: 5, MaxPlayers: 32, Tags: ["cs"]);

    /// Empty (no players) linux community server — the bucket tier 2 routes to its empty+linux spec.
    public static SimServer Empty(uint appId, string ip, int port, string name, string map)
        => new(appId, ip, port, name, map, Linux: true, Players: 0, MaxPlayers: 32, Tags: ["cs"]);
}

/// <summary>
/// In-memory stand-in for the Steam master server. Evaluates the subset of the Valve filter language
/// that <see cref="MasterServerQuerier"/> emits and enforces the 10k result cap, so the fan-out logic
/// can be tested without a network connection.
/// </summary>
internal sealed class SimulatedMasterServer : IMasterQuerySource
{
    private readonly List<SimServer> _servers;

    public int QueryCount { get; private set; }
    public List<string> Filters { get; } = [];

    public SimulatedMasterServer(IEnumerable<SimServer> servers) => _servers = servers.ToList();

    public Task EnsureConnectedAsync() => Task.CompletedTask;

    public Task<IReadOnlyList<MasterServerRecord>> QueryWithFilterAsync(uint appId, string filter)
    {
        QueryCount++;
        Filters.Add(filter);

        var (ands, nors) = ParseFilter(filter);

        IReadOnlyList<MasterServerRecord> matches = _servers
            .Where(s => ands.All(t => EvalTerm(t, s)) && !nors.Any(c => Matches(s, c)))
            .Take((int)ValveConstants.MaxServersPerQuery)
            .Select(s => new MasterServerRecord
            {
                EndPoint = new IPEndPoint(IPAddress.Parse(s.Ip), s.Port),
                Name = s.Name,
                Map = s.Map,
                AppId = s.AppId,
            })
            .ToList();

        return Task.FromResult(matches);
    }

    // An AND-term: a single condition, or an \or\ group (match any), or an \and\ group (match all).
    private sealed record Term(string Op, List<(string Key, string Val)> Conds);

    private static bool EvalTerm(Term t, SimServer s) => t.Op switch
    {
        "or" => t.Conds.Any(c => Matches(s, c)),
        "and" => t.Conds.All(c => Matches(s, c)),
        _ => Matches(s, t.Conds[0]),
    };

    private static (List<Term> Ands, List<(string Key, string Val)> Nors) ParseFilter(string filter)
    {
        var parts = filter.Split('\\', System.StringSplitOptions.RemoveEmptyEntries);
        var ands = new List<Term>();
        var nors = new List<(string, string)>();

        var i = 0;
        while (i < parts.Length)
        {
            var key = parts[i++];
            if (key is "nor" or "and" or "or")
            {
                var k = int.Parse(parts[i++]);
                var group = new List<(string, string)>();
                for (var j = 0; j < k && i + 1 < parts.Length; j++)
                {
                    group.Add((parts[i], parts[i + 1]));
                    i += 2;
                }

                if (key == "nor")
                {
                    nors.AddRange(group); // \nor\ excludes a server matching ANY listed condition
                }
                else
                {
                    ands.Add(new Term(key, group));
                }
            }
            else
            {
                var val = i < parts.Length ? parts[i++] : "";
                ands.Add(new Term("single", [(key, val)]));
            }
        }

        return (ands, nors);
    }

    private static bool Matches(SimServer s, (string Key, string Val) c) => c.Key switch
    {
        "appid" => s.AppId == uint.Parse(c.Val),
        "gametype" => s.Tags.Contains(c.Val, System.StringComparer.OrdinalIgnoreCase),
        "linux" => s.Linux,                                   // value is always "1"
        "empty" => s.Players > 0,                             // \empty\1 == "not empty"
        "full" => s.Players < s.MaxPlayers,                   // \full\1 == "not full"
        "map" => string.Equals(s.Map, c.Val, System.StringComparison.OrdinalIgnoreCase),
        "name_match" => NameMatches(s.Name, c.Val),
        "gameaddr" => string.Equals(s.Ip, c.Val.Split(':')[0], System.StringComparison.Ordinal),
        _ => false,
    };

    private static bool NameMatches(string name, string pattern)
        => pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], System.StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, pattern, System.StringComparison.OrdinalIgnoreCase);
}
