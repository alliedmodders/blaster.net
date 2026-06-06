// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Blaster.Batch;
using Blaster.Valve;
using Microsoft.Extensions.Logging;

namespace Blaster.CLI;

/// <summary>
/// Orchestrates master server queries and server info retrieval.
/// </summary>
public class CliServerQuerier
{
    private readonly int _maxConcurrency;
    private readonly MasterServerTransport _transport;
    private readonly string? _steamUsername;
    private readonly string? _steamPassword;
    private readonly string? _webApiKey;
    private readonly ILoggerFactory _loggerFactory;

    public CliServerQuerier(
        int maxConcurrency,
        MasterServerTransport transport,
        string? steamUsername,
        string? steamPassword,
        string? webApiKey,
        ILoggerFactory loggerFactory)
    {
        _maxConcurrency = maxConcurrency;
        _transport = transport;
        _steamUsername = steamUsername;
        _steamPassword = steamPassword;
        _webApiKey = webApiKey;
        _loggerFactory = loggerFactory;
    }

    private MasterServerQuerier CreateMasterQuerier()
    {
        var logger = _loggerFactory.CreateLogger<MasterServerQuerier>();
        return _transport == MasterServerTransport.WebApi
            ? MasterServerQuerier.CreateWebApi(_webApiKey!, logger)
            : new MasterServerQuerier(username: _steamUsername, password: _steamPassword, logger: logger);
    }

    /// <summary>
    /// Queries the master server for servers and retrieves their info/rules.
    /// </summary>
    public async Task<List<QueryResult>> QueryServersAsync(int[] appIds, bool skipInfo = false, bool skipRules = false)
    {
        var results = new List<QueryResult>();
        var resultsLock = new object();

        try
        {
            // Query master server for each app ID
            var allServers = new HashSet<string>();
            var allServersLock = new object();

            foreach (var appId in appIds)
            {
                try
                {
                    using (var querier = CreateMasterQuerier())
                    {
                        querier.FilterAppIds((AppId)appId);
                        await querier.QueryAsync(async (servers) =>
                        {
                            lock (allServersLock)
                            {
                                foreach (var server in servers)
                                {
                                    allServers.Add($"{server.Address}:{server.Port}");
                                }
                            }
                            await Task.CompletedTask;
                        });
                    }
                }
                catch (Exception ex)
                {
                    lock (resultsLock)
                    {
                        results.Add(new QueryResult
                        {
                            AppId = appId,
                            Error = $"Master server query failed: {ex.Message}"
                        });
                    }
                }
            }

            // If no servers found, return
            if (allServers.Count == 0)
            {
                return results;
            }

            // Create server batch for concurrent processing
            var serverList = allServers.ToList();
            var batch = new ServerBatch(serverList, appIds, skipInfo, skipRules);

            // Report progress so the (otherwise silent) A2S pass doesn't look hung.
            var progress = new ProgressLogger(_loggerFactory.CreateLogger<CliServerQuerier>(), serverList.Count);

            // Process servers concurrently
            var processor = new BatchProcessor(item =>
            {
                if (item is ServerQueryItem queryItem)
                {
                    ProcessServer(queryItem, results, resultsLock, progress);
                }
            }, maxTasks: _maxConcurrency);

            processor.AddBatch(batch);
            processor.Finish();
        }
        catch (Exception ex)
        {
            lock (resultsLock)
            {
                results.Add(new QueryResult
                {
                    Error = $"Unexpected error: {ex.Message}"
                });
            }
        }

        return results;
    }

    private void ProcessServer(ServerQueryItem item, List<QueryResult> results, object resultsLock, ProgressLogger progress)
    {
        var result = new QueryResult
        {
            Server = item.Server,
            AppId = item.AppId
        };

        try
        {
            using (var querier = new ServerQuerier(item.Server, TimeSpan.FromSeconds(5)))
            {
                // Query server info
                if (!item.SkipInfo)
                {
                    try
                    {
                        result.Info = querier.QueryInfo();
                    }
                    catch (Exception ex)
                    {
                        result.InfoError = $"Info query failed: {ex.Message}";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = $"Unexpected error: {ex.Message}";
        }

        lock (resultsLock)
        {
            results.Add(result);
        }

        progress.Increment();
    }
}

/// <summary>
/// Result of querying a single server.
/// </summary>
public class QueryResult
{
    public string? Server { get; set; }
    public int AppId { get; set; }
    public ServerInfo? Info { get; set; }
    public string? InfoError { get; set; }
    public Dictionary<string, string>? Rules { get; set; }
    public string? RulesError { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Item representing a single server to query.
/// </summary>
public class ServerQueryItem
{
    public string Server { get; set; } = "";
    public int AppId { get; set; }
    public bool SkipInfo { get; set; }
    public bool SkipRules { get; set; }
}

/// <summary>
/// Batch implementation for server queries.
/// </summary>
public class ServerBatch : IBatch
{
    private readonly List<ServerQueryItem> _items;

    public ServerBatch(List<string> servers, int[] appIds, bool skipInfo, bool skipRules)
    {
        _items = new List<ServerQueryItem>();
        
        foreach (var server in servers)
        {
            foreach (var appId in appIds)
            {
                _items.Add(new ServerQueryItem
                {
                    Server = server,
                    AppId = appId,
                    SkipInfo = skipInfo,
                    SkipRules = skipRules
                });
            }
        }
    }

    public object Item(int index) => _items[index];
    public int Count => _items.Count;
}
