// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Blaster.Batch;
using Blaster.Valve;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Blaster.AmStats;

/// <summary>
/// Collects statistics from Valve game servers and writes to database.
/// Queries the master server for available servers, then collects rules and stats.
/// </summary>
public class StatsCollector
{
    private readonly DatabaseConnection _db;
    private readonly long _gameId;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(3);
    private readonly string _steamUsername;
    private readonly string _steamPassword;
    private readonly ILoggerFactory _loggerFactory;

    private readonly Dictionary<(string ModString, ulong ModGameId), GameMod> _mods = new();
    private readonly HashSet<long> _modsSeenThisRun = new();
    private readonly Dictionary<string, GameAddonVar> _addonVars = new();
    private readonly Dictionary<(long, string), GameVarValue> _values = new();
    private readonly Dictionary<StatsKey, Stats> _statsRows = new();
    private GameStat? _globalStats;
    private long _runStamp;

    public StatsCollector(
        string configPath,
        long gameId,
        string? steamUsernameOverride = null,
        string? steamPasswordOverride = null,
        ILoggerFactory? loggerFactory = null)
    {
        var config = ConfigParser.Parse(configPath);
        var connStr = DatabaseConnection.ParseConnectionString(
            config["database.host"],
            config["database.username"],
            config["database.password"],
            config["database.dbname"]
        );
        
        _db = new DatabaseConnection(connStr);
        _gameId = gameId;
        _loggerFactory = loggerFactory ?? LoggerFactory.Create(builder => builder.AddConsole());
        _steamUsername = ResolveCredential(
            steamUsernameOverride,
            config,
            "steam.username",
            "BLASTER_STEAM_USERNAME");
        _steamPassword = ResolveCredential(
            steamPasswordOverride,
            config,
            "steam.password",
            "BLASTER_STEAM_PASSWORD");
        
        LoadGameData();
    }

    private static string ResolveCredential(
        string? overrideValue,
        Dictionary<string, string> config,
        string configKey,
        string environmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue))
            return overrideValue;

        if (config.TryGetValue(configKey, out var configValue) && !string.IsNullOrWhiteSpace(configValue))
            return configValue;

        var envValue = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(envValue))
            return envValue;

        throw new InvalidOperationException(
            $"Steam credential is required. Set '{configKey}' in config file or '{environmentVariable}' environment variable.");
    }

    private void LoadGameData()
    {
        // Load mods
        var mods = _db.Query<GameMod>(@"
            SELECT
                id AS Id,
                game_id AS GameId,
                valve_game_id AS ValveGameId,
                last_seen AS LastSeen,
                CONVERT(modstring USING utf8mb4) AS ModString,
                CONVERT(description USING utf8mb4) AS Description,
                CONVERT(url USING utf8mb4) AS Url,
                is_verified AS IsVerified
            FROM games_mods
            WHERE game_id = @GameId
        ", new { GameId = _gameId });
        foreach (var mod in mods)
        {
            _mods[(mod.ModString, mod.ValveGameId)] = mod;
        }

        // Load addon vars
        var addonVars = _db.Query<GameAddonVar>(@"
            SELECT
                gav.addon_id AS AddonId,
                gav.var_id AS VariableId,
                CONVERT(gv.name USING utf8mb4) AS Name
            FROM games_addons_vars gav
            JOIN games_vars gv ON gav.var_id = gv.id
            JOIN games_addons ga ON gav.addon_id = ga.id
            WHERE ga.game_id = @GameId
        ", new { GameId = _gameId });
        foreach (var av in addonVars)
        {
            _addonVars[$"{av.AddonId}:{av.VariableId}"] = av;
        }

        // Load var values
        var values = _db.Query<GameVarValue>(@"
            SELECT
                gvv.id AS Id,
                gvv.variable_id AS VariableId,
                CONVERT(gvv.value USING utf8mb4) AS Value,
                gvv.first_known AS FirstKnown
            FROM games_vars_values gvv
            JOIN games_vars gv ON gvv.variable_id = gv.id
            WHERE gv.game_id = @GameId
        ", new { GameId = _gameId });
        foreach (var val in values)
        {
            _values[(val.VariableId, val.Value)] = val;
        }
    }

    public void Collect()
    {
        // Initialize global stats
        _globalStats = new GameStat
        {
            Stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            GameId = _gameId
        };
        _runStamp = _globalStats.Stamp;

        // Query master server and collect stats
        var allServers = new HashSet<string>();
        var lockObj = new object();

        try
        {
            using (var master = new MasterServerQuerier(username: _steamUsername, password: _steamPassword, logger: _loggerFactory.CreateLogger<MasterServerQuerier>()))
            {
                // Set up filters based on game
                if (_gameId == 1)
                    master.FilterAppIds(AppIdHelper.HL1Apps.ToArray());
                else if (_gameId == 2)
                    master.FilterAppIds(AppIdHelper.HL2Apps.ToArray());

                // Process servers from master server in batches
                master.QueryAsync(async (servers) =>
                {
                    lock (lockObj)
                    {
                        foreach (var server in servers)
                        {
                            allServers.Add($"{server.Address}:{server.Port}");
                        }
                    }
                    await Task.CompletedTask;
                }).Wait();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Master server error: {ex.Message}");
            throw;
        }

        // Create batch processor for concurrent server queries
        var serverList = allServers.ToList();
        var batch = new ServerStatsBatch(serverList);
        var liveServers = new ConcurrentBag<Server>();

        var processor = new BatchProcessor(item =>
        {
            if (item is ServerStatsItem serverItem)
            {
                ProcessServer(serverItem, liveServers);
            }
        }, maxTasks: 20);

        processor.AddBatch(batch);
        processor.Finish();

        var preferredValveGameIds = DetermineMostPopulousValveGameIds(liveServers);
        foreach (var server in liveServers)
        {
            ProcessServerStats(server, preferredValveGameIds);
        }
    }

    private void ProcessServer(ServerStatsItem item, ConcurrentBag<Server> liveServers)
    {
        try
        {
            using (var querier = new ServerQuerier(item.Server, _timeout))
            {
                var info = querier.QueryInfo();
                
                // Query rules
                var rules = querier.QueryRules();

                var server = new Server { Info = info, Rules = rules };
                liveServers.Add(server);
                
                lock (_globalStats!)
                {
                    _globalStats.AliveCount++;
                }
            }
        }
        catch
        {
            lock (_globalStats!)
            {
                _globalStats.DeadCount++;
            }
        }
    }

    private void ProcessServerStats(Server server, IReadOnlyDictionary<string, ulong> preferredValveGameIds)
    {
        if (server.Info == null)
            return;

        var modId = GetMod(server, preferredValveGameIds);

        // Get or create stats rows for various aggregations
        var tables = new List<Stats>
        {
            GetStats(new StatsKey { ModId = modId, Type = server.DbType() })
        };

        // Find applicable addons
        var addons = new Dictionary<long, bool>();
        foreach (var (ruleKey, ruleValue) in server.Rules)
        {
            var addonVar = GetAddonVar(ruleKey);
            if (addonVar == null)
                continue;

            if (!addons.ContainsKey(addonVar.AddonId))
            {
                tables.Add(GetStats(new StatsKey { AddonId = addonVar.AddonId }));
                tables.Add(GetStats(new StatsKey { ModId = modId, Type = server.DbType(), AddonId = addonVar.AddonId }));
                addons[addonVar.AddonId] = true;
            }

            var valueId = GetValue(addonVar.VariableId, ruleValue);
            tables.Add(GetStats(new StatsKey { ValueId = valueId }));
            tables.Add(GetStats(new StatsKey { ModId = modId, Type = server.DbType(), ValueId = valueId }));
        }

        // Aggregate stats
        foreach (var table in tables)
        {
            table.ServerCount++;
            table.TotalPlayers += server.Info.Players;
            table.MaxPlayers += server.Info.MaxPlayers;
            table.TotalBots += server.Info.Bots;
        }

        lock (_globalStats!)
        {
            _globalStats.TotalPlayers += server.Info.Players;
            _globalStats.MaxPlayers += server.Info.MaxPlayers;
            _globalStats.TotalBots += server.Info.Bots;

            if (server.Info.OS == ServerOS.Linux)
                _globalStats.LinuxServers++;
            else if (server.Info.OS == ServerOS.Windows)
                _globalStats.WindowsServers++;

            if (server.Info.Type == ServerType.Listen)
                _globalStats.ListenServers++;
        }
    }

    private long GetMod(Server server, IReadOnlyDictionary<string, ulong> preferredValveGameIds)
    {
        if (server.Info == null)
            return 0;

        var modString = server.Info.Folder ?? string.Empty;
        var valveGameId = GetModGameId(server.Info);

        if (_mods.TryGetValue((modString, valveGameId), out var mod))
        {
            MarkModSeen(mod);
            return mod.Id;
        }

        var existingModId = _db.QuerySingleOrDefault<long>(@"
            SELECT id
            FROM games_mods
            WHERE game_id = @GameId AND valve_game_id = @ValveGameId AND modstring = @ModString
            LIMIT 1
        ", new
        {
            GameId = _gameId,
            ValveGameId = valveGameId,
            ModString = modString
        });
        if (existingModId != 0)
        {
            var existingMod = new GameMod
            {
                Id = existingModId,
                GameId = _gameId,
                ValveGameId = valveGameId,
                LastSeen = _runStamp,
                ModString = modString,
                Description = server.Info.Game ?? string.Empty,
                Url = string.Empty,
                IsVerified = 0
            };
            _mods[(modString, valveGameId)] = existingMod;
            MarkModSeen(existingMod);
            return existingMod.Id;
        }

        if (valveGameId != 0
            && preferredValveGameIds.TryGetValue(modString, out var mostPopulousValveGameId)
            && mostPopulousValveGameId == valveGameId)
        {
            var legacyModId = _db.QuerySingleOrDefault<long>(@"
                SELECT id
                FROM games_mods
                WHERE game_id = @GameId AND valve_game_id = 0 AND modstring = @ModString
                LIMIT 1
            ", new
            {
                GameId = _gameId,
                ModString = modString
            });
            if (legacyModId != 0)
            {
                _db.Execute(@"
                    UPDATE games_mods
                    SET valve_game_id = @ValveGameId, last_seen = @LastSeen
                    WHERE id = @Id
                ", new
                {
                    ValveGameId = valveGameId,
                    LastSeen = _runStamp,
                    Id = legacyModId
                });

                var promotedLegacyMod = new GameMod
                {
                    Id = legacyModId,
                    GameId = _gameId,
                    ValveGameId = valveGameId,
                    LastSeen = _runStamp,
                    ModString = modString,
                    Description = server.Info.Game ?? string.Empty,
                    Url = string.Empty,
                    IsVerified = 0
                };
                _mods[(modString, valveGameId)] = promotedLegacyMod;
                MarkModSeen(promotedLegacyMod);
                return promotedLegacyMod.Id;
            }
        }

        var description = server.Info.Game ?? string.Empty;
        var modId = _db.ExecuteAndReadId(@"
            INSERT INTO games_mods (game_id, valve_game_id, last_seen, modstring, description, url, is_verified)
            VALUES (@GameId, @ValveGameId, @LastSeen, @ModString, @Description, @Url, @IsVerified)
        ", new
        {
            GameId = _gameId,
            ValveGameId = valveGameId,
            LastSeen = _runStamp,
            ModString = modString,
            Description = description,
            Url = string.Empty,
            IsVerified = 0
        }, @"
            SELECT id
            FROM games_mods
            WHERE game_id = @GameId AND valve_game_id = @ValveGameId AND modstring = @ModString
            LIMIT 1
        ", new
        {
            GameId = _gameId,
            ValveGameId = valveGameId,
            ModString = modString
        });
        if (modId == 0)
            return 0;

        var newMod = new GameMod
        {
            Id = modId,
            GameId = _gameId,
            ValveGameId = valveGameId,
            LastSeen = _runStamp,
            ModString = modString,
            Description = description,
            Url = string.Empty,
            IsVerified = 0
        };
        _mods[(modString, valveGameId)] = newMod;
        MarkModSeen(newMod);
        return newMod.Id;
    }

    private void MarkModSeen(GameMod mod)
    {
        if (mod.Id == 0 || !_modsSeenThisRun.Add(mod.Id))
            return;

        _db.Execute(@"
            UPDATE games_mods
            SET last_seen = @LastSeen
            WHERE id = @Id
        ", new
        {
            LastSeen = _runStamp != 0 ? _runStamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Id = mod.Id
        });
    }

    private static Dictionary<string, ulong> DetermineMostPopulousValveGameIds(IEnumerable<Server> servers)
    {
        var counts = new Dictionary<string, Dictionary<ulong, int>>(StringComparer.Ordinal);

        foreach (var server in servers)
        {
            if (server.Info == null)
                continue;

            var valveGameId = GetModGameId(server.Info);
            if (valveGameId == 0)
                continue;

            var modString = server.Info.Folder ?? string.Empty;
            if (!counts.TryGetValue(modString, out var perModCounts))
            {
                perModCounts = new Dictionary<ulong, int>();
                counts[modString] = perModCounts;
            }

            if (perModCounts.TryGetValue(valveGameId, out var currentCount))
                perModCounts[valveGameId] = currentCount + 1;
            else
                perModCounts[valveGameId] = 1;
        }

        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var (modString, perModCounts) in counts)
        {
            var bestValveGameId = 0UL;
            var bestCount = -1;
            foreach (var (candidateValveGameId, count) in perModCounts)
            {
                if (count > bestCount || (count == bestCount && candidateValveGameId < bestValveGameId))
                {
                    bestCount = count;
                    bestValveGameId = candidateValveGameId;
                }
            }

            if (bestValveGameId != 0)
                result[modString] = bestValveGameId;
        }

        return result;
    }

    private static ulong GetModGameId(ServerInfo info)
    {
        if (info.Ext != null)
        {
            if (info.Ext.GameId != 0)
                return info.Ext.GameId;

            if (info.Ext.AppId != AppId.Unknown)
                return (ulong)info.Ext.AppId;
        }

        return 0;
    }

    private GameAddonVar? GetAddonVar(string varName)
    {
        foreach (var av in _addonVars.Values)
        {
            if (av.Name == varName)
                return av;
        }
        return null;
    }

    private long GetValue(long variableId, string value)
    {
        if (_values.TryGetValue((variableId, value), out var val))
            return val.Id;

        var firstKnown = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var valueId = _db.ExecuteAndReadId(@"
            INSERT INTO games_vars_values (variable_id, value, first_known)
            VALUES (@VariableId, @Value, @FirstKnown)
        ", new
        {
            VariableId = variableId,
            Value = value,
            FirstKnown = firstKnown
        }, @"
            SELECT id
            FROM games_vars_values
            WHERE variable_id = @VariableId AND value = @Value
            LIMIT 1
        ", new
        {
            VariableId = variableId,
            Value = value
        });
        if (valueId == 0)
            return 0;

        var newValue = new GameVarValue
        {
            Id = valueId,
            VariableId = variableId,
            Value = value,
            FirstKnown = firstKnown
        };
        _values[(variableId, value)] = newValue;
        return newValue.Id;
    }

    private Stats GetStats(StatsKey key)
    {
        if (!_statsRows.TryGetValue(key, out var stats))
        {
            stats = new Stats { Key = key };
            _statsRows[key] = stats;
        }
        return stats;
    }

    public void Finish()
    {
        long statsId = 0;

        // Write global stats
        if (_globalStats != null)
        {
            _db.Execute(@"
                INSERT INTO stats_games 
                (stamp, game_id, alive_count, dead_count, linux_servers, windows_servers, listen_servers, 
                 total_players, max_players, total_bots)
                VALUES (@Stamp, @GameId, @AliveCount, @DeadCount, @LinuxServers, @WindowsServers, @ListenServers,
                        @TotalPlayers, @MaxPlayers, @TotalBots)
            ", _globalStats);

            statsId = _db.QuerySingleOrDefault<long>("SELECT LAST_INSERT_ID();");
            if (statsId != 0)
            {
                _db.Execute("UPDATE stats_games SET stamp = UNIX_TIMESTAMP() WHERE id = @StatsId", new { StatsId = statsId });
            }
        }

        // Write per-mod/addon/value stats
        foreach (var (key, stats) in _statsRows)
        {
            switch (key.GetTableName())
            {
                case "stats_mods":
                    _db.Execute(@"
                        INSERT INTO stats_mods
                        (stats_id, mod_id, server_type, server_count, max_players, total_players, total_bots)
                        VALUES (@StatsId, @ModId, @ServerType, @ServerCount, @MaxPlayers, @TotalPlayers, @TotalBots)
                    ", new
                    {
                        StatsId = statsId,
                        ModId = key.ModId,
                        ServerType = key.Type,
                        stats.ServerCount,
                        stats.MaxPlayers,
                        stats.TotalPlayers,
                        stats.TotalBots
                    });
                    break;
                case "stats_games_addons":
                    _db.Execute(@"
                        INSERT INTO stats_games_addons
                        (stats_id, object_id, server_count, max_players, total_players, total_bots)
                        VALUES (@StatsId, @ObjectId, @ServerCount, @MaxPlayers, @TotalPlayers, @TotalBots)
                    ", new
                    {
                        StatsId = statsId,
                        ObjectId = key.AddonId,
                        stats.ServerCount,
                        stats.MaxPlayers,
                        stats.TotalPlayers,
                        stats.TotalBots
                    });
                    break;
                case "stats_games_values":
                    _db.Execute(@"
                        INSERT INTO stats_games_values
                        (stats_id, object_id, server_count, max_players, total_players, total_bots)
                        VALUES (@StatsId, @ObjectId, @ServerCount, @MaxPlayers, @TotalPlayers, @TotalBots)
                    ", new
                    {
                        StatsId = statsId,
                        ObjectId = key.ValueId,
                        stats.ServerCount,
                        stats.MaxPlayers,
                        stats.TotalPlayers,
                        stats.TotalBots
                    });
                    break;
                case "stats_mods_addons":
                    _db.Execute(@"
                        INSERT INTO stats_mods_addons
                        (stats_id, object_id, mod_id, server_type, server_count, max_players, total_players, total_bots)
                        VALUES (@StatsId, @ObjectId, @ModId, @ServerType, @ServerCount, @MaxPlayers, @TotalPlayers, @TotalBots)
                    ", new
                    {
                        StatsId = statsId,
                        ObjectId = key.AddonId,
                        ModId = key.ModId,
                        ServerType = key.Type,
                        stats.ServerCount,
                        stats.MaxPlayers,
                        stats.TotalPlayers,
                        stats.TotalBots
                    });
                    break;
                case "stats_mods_values":
                    _db.Execute(@"
                        INSERT INTO stats_mods_values
                        (stats_id, object_id, mod_id, server_type, server_count, max_players, total_players, total_bots)
                        VALUES (@StatsId, @ObjectId, @ModId, @ServerType, @ServerCount, @MaxPlayers, @TotalPlayers, @TotalBots)
                    ", new
                    {
                        StatsId = statsId,
                        ObjectId = key.ValueId,
                        ModId = key.ModId,
                        ServerType = key.Type,
                        stats.ServerCount,
                        stats.MaxPlayers,
                        stats.TotalPlayers,
                        stats.TotalBots
                    });
                    break;
            }
        }

        _db.Dispose();
    }
}

/// <summary>
/// Represents a row of statistics to be aggregated.
/// </summary>
internal class Stats
{
    public StatsKey Key { get; set; } = new();
    public long ServerCount { get; set; }
    public long TotalPlayers { get; set; }
    public long MaxPlayers { get; set; }
    public long TotalBots { get; set; }
}

/// <summary>
/// Key for aggregating statistics across different dimensions.
/// </summary>
internal class StatsKey
{
    public long ModId { get; set; }
    public long Type { get; set; }
    public long AddonId { get; set; }
    public long ValueId { get; set; }

    public string GetTableName() => (AddonId, ValueId) switch
    {
        (0, 0) => "stats_mods",
        (_, 0) => ModId == 0 ? "stats_games_addons" : "stats_mods_addons",
        (0, _) => ModId == 0 ? "stats_games_values" : "stats_mods_values",
        _ => throw new InvalidOperationException("Invalid stats key")
    };

    public override bool Equals(object? obj)
    {
        return obj is StatsKey key && key.ModId == ModId && key.Type == Type &&
               key.AddonId == AddonId && key.ValueId == ValueId;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ModId, Type, AddonId, ValueId);
    }
}

/// <summary>
/// Batch item for server statistics collection.
/// </summary>
internal class ServerStatsItem
{
    public string Server { get; set; } = "";
}

/// <summary>
/// Batch implementation for server statistics collection.
/// </summary>
internal class ServerStatsBatch : IBatch
{
    private readonly List<ServerStatsItem> _items;

    public ServerStatsBatch(List<string> servers)
    {
        _items = servers.Select(s => new ServerStatsItem { Server = s }).ToList();
    }

    public object Item(int index) => _items[index];
    public int Count => _items.Count;
}
