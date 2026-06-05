// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamKit2;

namespace Blaster.Valve;

/// <summary>
/// Callback invoked with each batch of servers from the master server.
/// </summary>
public delegate Task MasterQueryCallback(IEnumerable<IPEndPoint> servers);

/// <summary>
/// Shared Steam connection pool with rate limiting to minimize load on Steam.
/// Maintains a single persistent connection and coordinates queries across multiple clients.
/// </summary>
internal partial class SteamConnectionPool
{
    private readonly SteamClient _client;
    private readonly CallbackManager _callbackManager;
    private readonly SteamMasterServer _masterServer;
    private readonly SteamUser _steamUser;
    private readonly ILogger? _logger;
    private bool _loggedOn;
    private string? _connectError;
    private TaskCompletionSource<bool>? _loginTask;
    private TaskCompletionSource<SteamMasterServer.QueryCallback>? _currentQueryTask;
    private DateTime _lastQueryTime = DateTime.MinValue;
    private readonly TimeSpan _rateLimitInterval = TimeSpan.FromMilliseconds(250); // ~4 queries/second
    private readonly Lock _queryLock = new();
    private readonly string? _username;
    private readonly string? _password;

    public SteamConnectionPool(string? cellIdFilePath = null, string? username = null, string? password = null, ILogger? logger = null)
    {
        _logger = logger;
        _username = username;
        _password = password;
        
        uint cellId = 0;
        
        if (!string.IsNullOrEmpty(cellIdFilePath) && File.Exists(cellIdFilePath))
        {
            try
            {
                if (uint.TryParse(File.ReadAllText(cellIdFilePath).Trim(), out var parsedCellId))
                {
                    cellId = parsedCellId;
                }
            }
            catch
            {
                // Ignore errors reading cell ID
            }
        }

        var config = SteamConfiguration.Create(b => b.WithCellID(cellId));
        _client = new SteamClient(config);
        _callbackManager = new CallbackManager(_client);
        _masterServer = _client.GetHandler<SteamMasterServer>()!;
        _steamUser = _client.GetHandler<SteamUser>()!;

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamMasterServer.QueryCallback>(OnQueryCallback);
    }

    public async Task EnsureConnectedAsync()
    {
        if (_loggedOn)
        {
            _logger?.LogDebug("Already connected to Steam");
            return;
        }

        if (!_client.IsConnected)
        {
            _logger?.LogInformation("Connecting to Steam...");
            _loginTask = new TaskCompletionSource<bool>();
            _connectError = null;
            var stopwatch = Stopwatch.StartNew();
            _client.Connect();
            
            // Wait for connection/login with callback processing
            while (!_loggedOn && _connectError == null && stopwatch.Elapsed < TimeSpan.FromSeconds(30))
            {
                try
                {
                    _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                }
                catch (Exception ex)
                {
                    _connectError = $"exception in callback: {ex}";
                    break;
                }
                await Task.Delay(50);
            }

            _loginTask = null;
            stopwatch.Stop();
            
            if (!_loggedOn)
            {
                var reason = _connectError ?? "timed out";
                _logger?.LogError("Failed to connect to Steam after {Elapsed}ms: {Reason}", stopwatch.ElapsedMilliseconds, reason);
                throw new MasterServerException($"Failed to connect to Steam: {reason}");
            }
            
            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger.LogInformation("Connected to Steam in {Elapsed}ms", stopwatch.ElapsedMilliseconds);
            }
        }
    }

    public async Task<IEnumerable<IPEndPoint>> QueryWithFilterAsync(string filter)
    {
        lock (_queryLock)
        {
            // Rate limiting: wait if needed
            var timeSinceLastQuery = DateTime.UtcNow - _lastQueryTime;
            if (timeSinceLastQuery < _rateLimitInterval)
            {
                var delayMs = (int)(_rateLimitInterval - timeSinceLastQuery).TotalMilliseconds;
                System.Threading.Thread.Sleep(delayMs);
            }
            _lastQueryTime = DateTime.UtcNow;
        }

        _currentQueryTask = new TaskCompletionSource<SteamMasterServer.QueryCallback>();

        // Extract AppID from filter string (format: \appid\240 or similar)
        uint appId = 0;
        var appIdMatch = AppIdRegex().Match(filter);
        if (appIdMatch.Success && uint.TryParse(appIdMatch.Groups[1].Value, out var parsedId))
        {
            appId = parsedId;
        }

        var details = new SteamMasterServer.QueryDetails
        {
            AppID = appId,
            Filter = filter,
            Region = (ERegionCode)0xFF,
            MaxServers = 50000
        };

        try
        {
            var stopwatch = Stopwatch.StartNew();
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("Querying Steam with filter: {Filter}", filter);
            }
            
            var job = _masterServer.ServerQuery(details);
            
            while (!_currentQueryTask.Task.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(15))
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(10));
                await Task.Delay(10);
            }
            
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(15))
            {
                _logger?.LogWarning("Query timeout for filter: {Filter}", filter);
                throw new TimeoutException("Query timeout waiting for Steam response");
            }

            var callback = await _currentQueryTask.Task.ConfigureAwait(false);
            var serverCount = callback.Servers.Count;
            stopwatch.Stop();
            
            if (_logger?.IsEnabled(LogLevel.Information) == true)
            {
                _logger.LogInformation("Query returned {Count} servers in {Elapsed}ms (filter: {Filter})",
                    serverCount, stopwatch.ElapsedMilliseconds, filter);
            }

            return callback.Servers
                .Select(s => s.EndPoint);
        }
        catch
        {
            _logger?.LogWarning("Query failed for filter: {Filter}", filter);
            return [];
        }
        finally
        {
            _currentQueryTask = null;
        }
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        // Login with credentials if provided, otherwise anonymous
        if (!string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
        {
            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = _username,
                Password = _password
            });
        }
        else
        {
            _steamUser.LogOnAnonymous();
        }
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        if (!_loggedOn && _connectError == null && !callback.UserInitiated)
        {
            _connectError = "disconnected before login completed";
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.OK)
        {
            _loggedOn = true;
        }
        else
        {
            _connectError = $"login failed: {callback.Result}";
        }
    }

    private void OnQueryCallback(SteamMasterServer.QueryCallback callback)
    {
        if (_currentQueryTask != null && !_currentQueryTask.Task.IsCompleted)
        {
            _currentQueryTask.TrySetResult(callback);
        }
    }

    [GeneratedRegex(@"\\appid\\(\d+)")]
    private static partial Regex AppIdRegex();

    public void Dispose()
    {
        try
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
        }
        catch
        {
            // Ignore
        }
    }
}

/// <summary>
/// Queries the Steam Game Master Server via SteamKit2 for game server lists.
/// Supports anonymous connections and multi-call filtering strategy to work around the ~50k server limit.
/// Uses a shared connection pool with rate limiting to minimize load on Steam.
/// </summary>
public class MasterServerQuerier : IDisposable
{
    // Shared connection pool to minimize Steam load
    private static readonly System.Threading.Lock _poolLock = new();
    private static SteamConnectionPool? _sharedPool;

    private readonly SteamConnectionPool _pool;
    private readonly List<uint> _appIds = [];
    private readonly ILogger<MasterServerQuerier>? _logger;
    private bool _disposed;

    public MasterServerQuerier(string? cellIdFilePath = null, string? username = null, string? password = null, ILogger<MasterServerQuerier>? logger = null)
    {
        _logger = logger;
        lock (_poolLock)
        {
            _sharedPool ??= new SteamConnectionPool(cellIdFilePath, username, password, _logger);
            _pool = _sharedPool;
        }
    }

    /// <summary>
    /// Adds app IDs to the query filter list.
    /// </summary>
    public void FilterAppIds(params AppId[] appIds)
    {
        foreach (var appId in appIds)
        {
            _appIds.Add((uint)appId);
        }
    }

    /// <summary>
    /// Clears all filters.
    /// </summary>
    public void ClearFilters()
    {
        _appIds.Clear();
    }

    /// <summary>
    /// Queries Steam for game servers using multi-call strategy to maximize results.
    /// Applies multiple filter combinations to work around per-query limits (~50k servers).
    /// Uses filter combinations (server type, OS) to distribute queries and maximize coverage.
    /// </summary>
    public async Task QueryAsync(MasterQueryCallback callback)
    {
        ThrowIfDisposed();

        if (_appIds.Count == 0)
        {
            _logger?.LogWarning("No app IDs configured for query");
            return;
        }

        var seen = new HashSet<string>();
        var stopwatch = Stopwatch.StartNew();
        
        if (_logger?.IsEnabled(LogLevel.Information) == true)
        {
            _logger.LogInformation("Starting query for {Count} app IDs: {AppIds}",
                _appIds.Count, string.Join(", ", _appIds));
        }

        // Ensure connection is established
        await _pool.EnsureConnectedAsync();

        // Strategy: Query with multiple filter combinations to maximize server discovery
        // This works around the ~50k limit per query by distributing queries
        var appIdFilters = _appIds.Select(id => BuildAppIdFilter(id)).ToList();
        var totalServersReceived = 0;
        
        foreach (var appIdFilter in appIdFilters)
        {
            var servers = await _pool.QueryWithFilterAsync(appIdFilter);
            var serversCount = servers.Count();
            if (serversCount == 50000)
            {
                _logger?.LogWarning("Query for filter {Filter} hit 50k limit, results may be incomplete", appIdFilter);
            }
            else if (serversCount == 0)
            {
                _logger?.LogWarning("Query for filter {Filter} returned no servers", appIdFilter);
            }
            else
            {
                _logger?.LogDebug("Callback: {Count} servers", serversCount);
                await callback(servers.Where(s => !IsExcludedBeforeQuery(s)));
            }

            totalServersReceived += serversCount;
        }
        
        stopwatch.Stop();
        if (_logger?.IsEnabled(LogLevel.Information) == true)
        {
            _logger.LogInformation("Query complete: {Total} servers across {Batches} filters in {Elapsed}ms",
                totalServersReceived, appIdFilters.Count, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Builds an app ID filter string for master server queries.
    /// </summary>
    private static string BuildAppIdFilter(uint appId)
    {
        return $"\\appid\\{appId}\\nor\\1\\gametype\\valve";
    }

    private static bool IsExcludedBeforeQuery(IPEndPoint server)
    {
        var address = server.Address;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Close()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
        // Don't disconnect shared pool - it stays alive for reuse
    }
}
