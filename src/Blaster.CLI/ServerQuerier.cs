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
    private readonly bool _includeFakeIp;
    private readonly ILoggerFactory _loggerFactory;

    public CliServerQuerier(
        int maxConcurrency,
        MasterServerTransport transport,
        string? steamUsername,
        string? steamPassword,
        string? webApiKey,
        bool includeFakeIp,
        ILoggerFactory loggerFactory)
    {
        _maxConcurrency = maxConcurrency;
        _transport = transport;
        _steamUsername = steamUsername;
        _steamPassword = steamPassword;
        _webApiKey = webApiKey;
        _includeFakeIp = includeFakeIp;
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
            var allServers = new HashSet<string>();
            var fakeIpServers = new List<FakeIpServer>();

            // One querier kept alive for the whole run so fake-IP servers can be queried after the
            // master fan-out (and so an owned Web API source isn't disposed mid-run).
            using var querier = CreateMasterQuerier();
            querier.IncludeFakeIp = _includeFakeIp;

            foreach (var appId in appIds)
            {
                try
                {
                    querier.ClearFilters();
                    querier.FilterAppIds((AppId)appId);
                    await querier.QueryAsync(servers =>
                    {
                        foreach (var server in servers)
                        {
                            allServers.Add($"{server.Address}:{server.Port}");
                        }
                        return Task.CompletedTask;
                    });

                    if (_includeFakeIp)
                    {
                        fakeIpServers.AddRange(querier.FakeIpServers);
                    }
                }
                catch (Exception ex)
                {
                    lock (resultsLock)
                    {
                        results.Add(new QueryResult { AppId = appId, Error = $"Master server query failed: {ex.Message}" });
                    }
                }
            }

            if (allServers.Count == 0 && fakeIpServers.Count == 0)
            {
                return results;
            }

            // Directly-addressable servers go over UDP A2S; fake-IP servers over QueryByFakeIP. Run both
            // concurrently.
            var directTask = Task.Run(() =>
            {
                if (allServers.Count == 0)
                {
                    return;
                }

                var serverList = allServers.ToList();
                var batch = new ServerBatch(serverList, appIds, skipInfo, skipRules);

                // Report progress so the (otherwise silent) A2S pass doesn't look hung.
                var progress = new ProgressLogger(_loggerFactory.CreateLogger<CliServerQuerier>(), serverList.Count);

                var processor = new BatchProcessor(item =>
                {
                    if (item is ServerQueryItem queryItem)
                    {
                        ProcessServer(queryItem, results, resultsLock, progress);
                    }
                }, maxTasks: _maxConcurrency);

                processor.AddBatch(batch);
                processor.Finish();
            });

            var fakeProgress = new ProgressLogger(
                _loggerFactory.CreateLogger<CliServerQuerier>(), fakeIpServers.Count, "fake-IP servers");
            var fakeIpTask = ProcessFakeIpServersAsync(querier, fakeIpServers, skipInfo, results, resultsLock, fakeProgress);

            await Task.WhenAll(directTask, fakeIpTask);
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

    /// <summary>
    /// Queries fake-IP (SDR) servers via QueryByFakeIP. Runs concurrently with the UDP A2S batch; the
    /// underlying transport serializes/throttles the calls itself.
    /// </summary>
    private static async Task ProcessFakeIpServersAsync(
        MasterServerQuerier querier, List<FakeIpServer> servers, bool skipInfo, List<QueryResult> results, object resultsLock, ProgressLogger progress)
    {
        foreach (var fake in servers)
        {
            var result = new QueryResult { Server = fake.EndPoint.ToString(), AppId = (int)fake.AppId };

            if (!skipInfo)
            {
                try
                {
                    var info = await querier.QueryFakeServerInfoAsync(fake);
                    if (info != null)
                    {
                        result.Info = info;
                    }
                    else
                    {
                        result.InfoError = "QueryByFakeIP returned no data";
                    }
                }
                catch (Exception ex)
                {
                    result.InfoError = $"QueryByFakeIP failed: {ex.Message}";
                }
            }

            lock (resultsLock)
            {
                results.Add(result);
            }

            progress.Increment();
        }
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
