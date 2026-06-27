// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace Blaster.Valve;

/// <summary>
/// Callback invoked with each batch of servers from the master server.
/// </summary>
public delegate Task MasterQueryCallback(IEnumerable<MasterServerEntry> servers);

/// <summary>
/// Overwrites the player, bot, and max-player counts on an A2S_INFO result with the values the Steam
/// game master server (GMS) reports for that server. Both sources can be faked, but spoofing the GMS
/// counts takes more effort than the counts a server hands back in its own A2S_INFO reply, so the GMS
/// values are treated as authoritative. The A2S protocol carries these counts as single bytes, so the
/// (in practice always smaller) GMS values are clamped to the byte range.
/// </summary>
internal static class AuthoritativeCounts
{
    public static void Apply(ServerInfo info, uint players, uint bots, uint maxPlayers)
    {
        info.Players = (byte)Math.Min(players, byte.MaxValue);
        info.Bots = (byte)Math.Min(bots, byte.MaxValue);
        info.MaxPlayers = (byte)Math.Min(maxPlayers, byte.MaxValue);
    }
}

/// <summary>
/// A directly-addressable game server returned by the master server, carrying the GMS-reported
/// player/bot/max-player counts alongside the endpoint to query via UDP A2S.
/// </summary>
public sealed record MasterServerEntry(IPEndPoint EndPoint, uint Players, uint Bots, uint MaxPlayers)
{
    /// <summary>
    /// Replaces the player, bot, and max-player counts on <paramref name="info"/> with this entry's GMS
    /// counts, which are harder to spoof than the counts the server returns over A2S_INFO.
    /// </summary>
    public void ApplyAuthoritativeCounts(ServerInfo info) => AuthoritativeCounts.Apply(info, Players, Bots, MaxPlayers);
}

/// <summary>
/// An SDR / fake-IP server (169.254.* address) discovered in the master results. These can't be reached
/// by UDP A2S; they are queried via QueryByFakeIP, which needs the owning <see cref="AppId"/>. The
/// GMS-reported player/bot/max-player counts are carried so they can override the (equally spoofable)
/// counts in the relayed ping reply.
/// </summary>
public sealed record FakeIpServer(IPEndPoint EndPoint, uint AppId, uint Players, uint Bots, uint MaxPlayers)
{
    /// <summary>
    /// Replaces the player, bot, and max-player counts on <paramref name="info"/> with this server's GMS counts.
    /// </summary>
    public void ApplyAuthoritativeCounts(ServerInfo info) => AuthoritativeCounts.Apply(info, Players, Bots, MaxPlayers);
}

/// <summary>
/// Selects how server lists are retrieved: a live Steam connection or the Steam Web API.
/// </summary>
public enum MasterServerTransport
{
    Steam,
    WebApi,
}

/// <summary>
/// Parsing helpers for <see cref="MasterServerTransport"/>.
/// </summary>
public static class MasterServerTransports
{
    /// <summary>
    /// Parses a transport name ("steam" or "web-api"); null/empty defaults to <see cref="MasterServerTransport.Steam"/>.
    /// </summary>
    public static MasterServerTransport Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "steam" => MasterServerTransport.Steam,
        "web-api" or "webapi" or "web_api" => MasterServerTransport.WebApi,
        _ => throw new ArgumentException($"Unknown transport '{value}'. Use 'steam' or 'web-api'."),
    };
}

/// <summary>
/// Source of master-server query results. Abstracts <see cref="SteamConnectionPool"/> so the fan-out
/// in <see cref="MasterServerQuerier"/> can be driven against a simulated master server in tests.
/// </summary>
internal interface IMasterQuerySource
{
    Task EnsureConnectedAsync();
    Task<IReadOnlyList<MasterServerRecord>> QueryWithFilterAsync(uint appId, string filter);

    /// <summary>Queries a fake-IP (SDR) server for info via QueryByFakeIP; null on failure.</summary>
    Task<ServerInfo?> QueryFakeServerInfoAsync(IPEndPoint endpoint, uint appId);

    /// <summary>Queries a fake-IP (SDR) server for rules via QueryByFakeIP; null on failure.</summary>
    Task<Dictionary<string, string>?> QueryFakeServerRulesAsync(IPEndPoint endpoint, uint appId);
}

/// <summary>
/// Retry policy for master-server queries, factored out so it can be unit-tested independently of the
/// Steam connection. A genuinely empty result returns normally (no retry); only thrown exceptions are
/// retried. After all attempts are exhausted it logs and returns an empty list, so one bad leaf doesn't
/// abort the whole run.
/// </summary>
internal static class MasterQueryRetry
{
    public static async Task<IReadOnlyList<MasterServerRecord>> RunAsync(
        Func<Task<IReadOnlyList<MasterServerRecord>>> queryOnce,
        int maxAttempts, int baseDelayMs, ILogger? logger, string filter)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await queryOnce();
            }
            catch (Exception ex)
            {
                if (attempt >= maxAttempts)
                {
                    logger?.LogError("Query failed after {Attempts} attempts (filter: {Filter}): {Error}. Giving up on this query.",
                        attempt, filter, ex.Message);
                    return [];
                }

                var backoff = TimeSpan.FromMilliseconds(baseDelayMs * attempt);
                logger?.LogWarning("Query attempt {Attempt}/{Max} failed (filter: {Filter}): {Error}. Retrying in {Backoff}ms.",
                    attempt, maxAttempts, filter, ex.Message, backoff.TotalMilliseconds);
                await Task.Delay(backoff);
            }
        }

        return [];
    }
}

/// <summary>
/// Shared Steam connection pool with rate limiting to minimize load on Steam.
/// Maintains a single persistent connection and coordinates queries across multiple clients.
/// </summary>
internal class SteamConnectionPool : IMasterQuerySource
{
    private readonly SteamClient _client;
    private readonly CallbackManager _callbackManager;
    private readonly GameServersHandler _gameServers;
    private readonly GameServers _gameServersService;
    private readonly SteamUser _steamUser;
    private readonly ILogger? _logger;
    private bool _loggedOn;
    private string? _connectError;
    private TaskCompletionSource<bool>? _loginTask;
    private TaskCompletionSource<ServerQueryResponseCallback>? _currentQueryTask;
    private ulong _currentJobId;
    private TaskCompletionSource<SteamUnifiedMessages.ServiceMethodResponse<CGameServers_GameServerQuery_Response>>? _currentFakeIpTask;
    private ulong _currentFakeIpJobId;
    private DateTime _lastQueryTime = DateTime.MinValue;
    private readonly TimeSpan _rateLimitInterval = TimeSpan.FromMilliseconds(100); // ~10 queries/second
    private readonly Lock _queryLock = new();
    // Serializes fake-IP queries: the single connection (one in-flight slot + callback pump) can't be
    // driven from the consumer's parallel A2S batch concurrently.
    private readonly SemaphoreSlim _fakeIpSemaphore = new(1, 1);
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
        _steamUser = _client.GetHandler<SteamUser>()!;

        // Use our own handler instead of the built-in SteamMasterServer so we can read the full
        // per-server payload (map/gamedir/name/players) the GMS response carries.
        _gameServers = new GameServersHandler();
        _client.AddHandler(_gameServers);

        // SteamKit's generated GameServers unified service routes QueryByFakeIP responses (used to
        // query SDR / fake-IP servers that can't be reached by ordinary UDP A2S).
        _gameServersService = _client.GetHandler<SteamUnifiedMessages>()!.CreateService<GameServers>();

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<ServerQueryResponseCallback>(OnQueryCallback);
        _callbackManager.Subscribe<SteamUnifiedMessages.ServiceMethodResponse<CGameServers_GameServerQuery_Response>>(OnFakeIpResponse);
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

    /// <summary>
    /// Runs a GMS query, retrying on timeout/transient failure (see <see cref="MasterQueryRetry"/>).
    /// </summary>
    public Task<IReadOnlyList<MasterServerRecord>> QueryWithFilterAsync(uint appId, string filter)
        => MasterQueryRetry.RunAsync(
            () => QueryOnceAsync(appId, filter),
            ValveConstants.MasterQueryMaxAttempts,
            ValveConstants.MasterQueryRetryBaseDelayMs,
            _logger,
            filter);

    private async Task<IReadOnlyList<MasterServerRecord>> QueryOnceAsync(uint appId, string filter)
    {
        lock (_queryLock)
        {
            // Rate limiting: wait if needed
            var timeSinceLastQuery = DateTime.UtcNow - _lastQueryTime;
            if (timeSinceLastQuery < _rateLimitInterval)
            {
                var delayMs = (int)(_rateLimitInterval - timeSinceLastQuery).TotalMilliseconds;
                Thread.Sleep(delayMs);
            }
            _lastQueryTime = DateTime.UtcNow;
        }

        var queryTask = new TaskCompletionSource<ServerQueryResponseCallback>();
        _currentQueryTask = queryTask;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                _logger.LogDebug("Querying Steam with filter: {Filter}", filter);
            }

            _currentJobId = _gameServers.ServerQuery(appId, filter, ValveConstants.MaxServersPerQuery);

            while (!queryTask.Task.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(15))
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(10));
                await Task.Delay(10);
            }

            if (!queryTask.Task.IsCompleted)
            {
                throw new TimeoutException("Query timeout waiting for Steam response");
            }

            var callback = await queryTask.Task.ConfigureAwait(false);
            var serverCount = callback.Servers.Count;
            stopwatch.Stop();

            _logger?.LogDebug("Query returned {Count} servers in {Elapsed}ms (filter: {Filter})",
                serverCount, stopwatch.ElapsedMilliseconds, filter);

            if (serverCount >= ValveConstants.MaxServersPerQuery)
            {
                _logger?.LogDebug("Query hit server limit ({MaxServers}) for filter: {Filter}; will fan out further.",
                    ValveConstants.MaxServersPerQuery, filter);
            }

            return callback.Servers;
        }
        finally
        {
            _currentQueryTask = null;
        }
    }

    /// <summary>
    /// Queries a fake-IP server for info (ping data) and maps it to <see cref="ServerInfo"/>; null on
    /// failure. Safe to call from the consumer's parallel A2S batch (serialized on the connection).
    /// </summary>
    public async Task<ServerInfo?> QueryFakeServerInfoAsync(IPEndPoint endpoint, uint appId)
    {
        var response = await QueryByFakeIpAsync(
            FakeIpMapper.ToFakeIpValue(endpoint.Address), (uint)endpoint.Port, appId,
            CGameServers_QueryByFakeIP_Request.EQueryType.Query_Ping);
        return response?.ping_data is { } ping ? FakeIpMapper.ToServerInfo(ping, endpoint) : null;
    }

    /// <summary>
    /// Queries a fake-IP server for rules (cvars); null on failure.
    /// </summary>
    public async Task<Dictionary<string, string>?> QueryFakeServerRulesAsync(IPEndPoint endpoint, uint appId)
    {
        var response = await QueryByFakeIpAsync(
            FakeIpMapper.ToFakeIpValue(endpoint.Address), (uint)endpoint.Port, appId,
            CGameServers_QueryByFakeIP_Request.EQueryType.Query_Rules);
        return response?.rules_data is { } rules ? FakeIpMapper.ToRules(rules) : null;
    }

    /// <summary>
    /// Queries one SDR / fake-IP server via the unified <c>GameServers.QueryByFakeIP</c> method (these
    /// servers can't be reached by ordinary UDP A2S). <paramref name="queryType"/> selects ping (info),
    /// players, or rules. Returns null on failure/timeout. Requires an authenticated login. Serialized
    /// via a semaphore so it is safe to call from a parallel batch.
    /// </summary>
    public async Task<CGameServers_GameServerQuery_Response?> QueryByFakeIpAsync(
        uint fakeIp, uint fakePort, uint appId, CGameServers_QueryByFakeIP_Request.EQueryType queryType)
    {
        await _fakeIpSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            return await QueryByFakeIpCoreAsync(fakeIp, fakePort, appId, queryType);
        }
        finally
        {
            _fakeIpSemaphore.Release();
        }
    }

    private async Task<CGameServers_GameServerQuery_Response?> QueryByFakeIpCoreAsync(
        uint fakeIp, uint fakePort, uint appId, CGameServers_QueryByFakeIP_Request.EQueryType queryType)
    {
        lock (_queryLock)
        {
            var timeSinceLastQuery = DateTime.UtcNow - _lastQueryTime;
            if (timeSinceLastQuery < _rateLimitInterval)
            {
                Thread.Sleep((int)(_rateLimitInterval - timeSinceLastQuery).TotalMilliseconds);
            }
            _lastQueryTime = DateTime.UtcNow;
        }

        var responseTask = new TaskCompletionSource<SteamUnifiedMessages.ServiceMethodResponse<CGameServers_GameServerQuery_Response>>();
        _currentFakeIpTask = responseTask;

        try
        {
            var request = new CGameServers_QueryByFakeIP_Request
            {
                fake_ip = fakeIp,
                fake_port = fakePort,
                app_id = appId,
                query_type = queryType,
            };
            var job = _gameServersService.QueryByFakeIP(request);
            _currentFakeIpJobId = job.JobID;

            var stopwatch = Stopwatch.StartNew();
            while (!responseTask.Task.IsCompleted && stopwatch.Elapsed < TimeSpan.FromSeconds(15))
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(10));
                await Task.Delay(10);
            }

            if (!responseTask.Task.IsCompleted)
            {
                _logger?.LogDebug("QueryByFakeIP {Ip}:{Port} (app {AppId}, {Type}) timed out", fakeIp, fakePort, appId, queryType);
                return null;
            }

            var callback = await responseTask.Task.ConfigureAwait(false);
            if (callback.Result != EResult.OK)
            {
                _logger?.LogDebug("QueryByFakeIP {Ip}:{Port} (app {AppId}, {Type}) -> {Result}", fakeIp, fakePort, appId, queryType, callback.Result);
                return null;
            }

            return callback.Body;
        }
        finally
        {
            _currentFakeIpTask = null;
        }
    }

    private void OnFakeIpResponse(SteamUnifiedMessages.ServiceMethodResponse<CGameServers_GameServerQuery_Response> callback)
    {
        var task = _currentFakeIpTask;
        if (task != null && !task.Task.IsCompleted && (ulong)callback.JobID == _currentFakeIpJobId)
        {
            task.TrySetResult(callback);
        }
    }

    private async void OnConnected(SteamClient.ConnectedCallback callback)
    {
        // Login with credentials if provided, otherwise anonymous.
        if (string.IsNullOrEmpty(_username) || string.IsNullOrEmpty(_password))
        {
            _steamUser.LogOnAnonymous();
            return;
        }

        // Steam no longer accepts raw username/password via SteamUser.LogOn (it returns
        // InvalidPassword). Exchange the credentials for a refresh token through the authentication
        // service, then log on with that token. This runs on the callback pump thread; the awaits
        // are driven by the RunWaitCallbacks loop in EnsureConnectedAsync.
        try
        {
            // No Authenticator is supplied: we don't support Steam Guard (2FA). If the account ever
            // requires a guard code, polling throws and the error is surfaced below rather than
            // blocking on console input (this tool runs headless).
            var authSession = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = _username,
                    Password = _password,
                    IsPersistentSession = false,
                }).ConfigureAwait(false);

            var pollResponse = await authSession.PollingWaitForResultAsync().ConfigureAwait(false);

            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = pollResponse.AccountName,
                AccessToken = pollResponse.RefreshToken,
            });
        }
        catch (Exception ex)
        {
            // Surface to EnsureConnectedAsync's wait loop the same way a failed LoggedOnCallback would.
            _connectError = $"authentication failed: {ex.Message}";
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

    private void OnQueryCallback(ServerQueryResponseCallback callback)
    {
        var task = _currentQueryTask;
        if (task != null && !task.Task.IsCompleted && (ulong)callback.JobID == _currentJobId)
        {
            task.TrySetResult(callback);
        }
    }

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

    private readonly IMasterQuerySource _source;
    private readonly bool _ownsSource;
    private readonly List<uint> _appIds = [];
    private readonly List<FakeIpServer> _fakeIpServers = [];
    private bool _includeFakeIp;
    private int _maxServersPerHost = ValveConstants.DefaultMaxServersPerHost;
    private SpamHostTracker _spamTracker = new(ValveConstants.DefaultMaxServersPerHost, ValveConstants.MaxRealisticPlayers);
    private readonly ILogger<MasterServerQuerier>? _logger;
    private FanOutStats? _stats;
    private int _runQueries;
    private int _runCapHits;
    private bool _disposed;

    /// <summary>Per-app-id fan-out query counters, categorised by selector kind, for tuning.</summary>
    private sealed class FanOutStats
    {
        public int TotalQueries;
        public int CapHits;
        public int TierQueries;
        public int MapQueries;
        public int NameQueries;
        public int GameAddrQueries;
        public int OrBatchQueries;
    }

    /// <summary>
    /// When true, SDR / fake-IP (169.254.*) servers are not dropped; they are collected (with their app
    /// id) into <see cref="FakeIpServers"/> during <see cref="QueryAsync"/> for querying via
    /// <see cref="QueryFakeServerInfoAsync"/> / <see cref="QueryFakeServerRulesAsync"/> instead of UDP A2S.
    /// </summary>
    public bool IncludeFakeIp
    {
        get => _includeFakeIp;
        set => _includeFakeIp = value;
    }

    /// <summary>Fake-IP servers collected during the most recent <see cref="QueryAsync"/> run.</summary>
    public IReadOnlyList<FakeIpServer> FakeIpServers => _fakeIpServers;

    /// <summary>
    /// A host (IP) advertising more than this many servers is treated as a redirect/spam farm: its
    /// servers are dropped and the host is fed back into the fan-out as a NOR exclusion. Set to 0 (or
    /// less) to disable the spam filter. Defaults to <see cref="ValveConstants.DefaultMaxServersPerHost"/>.
    /// </summary>
    public int MaxServersPerHost
    {
        get => _maxServersPerHost;
        set => _maxServersPerHost = value;
    }

    /// <summary>
    /// Hosts identified as spam farms during the most recent <see cref="QueryAsync"/> run. Consumers
    /// should drop any servers they collected on these IPs — a host can be flagged after some of its
    /// servers were already streamed to the callback.
    /// </summary>
    public IReadOnlyCollection<IPAddress> SpamHosts => _spamTracker.SpamHosts;

    public MasterServerQuerier(string? cellIdFilePath = null, string? username = null, string? password = null, ILogger<MasterServerQuerier>? logger = null)
    {
        _logger = logger;
        lock (_poolLock)
        {
            _sharedPool ??= new SteamConnectionPool(cellIdFilePath, username, password, _logger);
            _source = _sharedPool;
        }
    }

    /// <summary>
    /// Creates a querier that fetches server lists from the Steam Web API
    /// (<c>IGameServersService/GetServerList</c>) instead of a live Steam connection.
    /// </summary>
    public static MasterServerQuerier CreateWebApi(string apiKey, ILogger<MasterServerQuerier>? logger = null)
        => new(new WebApiQuerySource(apiKey, logger), logger, ownsSource: true);

    /// <summary>
    /// Drives the fan-out against an arbitrary <see cref="IMasterQuerySource"/> — a Web API source
    /// (<paramref name="ownsSource"/> true, disposed with this querier) or a simulated master server
    /// in tests. The shared Steam connection pool is never owned here, so it survives reuse.
    /// </summary>
    internal MasterServerQuerier(IMasterQuerySource source, ILogger<MasterServerQuerier>? logger = null, bool ownsSource = false)
    {
        _logger = logger;
        _source = source;
        _ownsSource = ownsSource;
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
    /// Queries a fake-IP server (from <see cref="FakeIpServers"/>) for info via QueryByFakeIP. Returns
    /// null on failure. Safe to call concurrently with UDP A2S queries.
    /// </summary>
    public Task<ServerInfo?> QueryFakeServerInfoAsync(FakeIpServer server)
        => _source.QueryFakeServerInfoAsync(server.EndPoint, server.AppId);

    /// <summary>
    /// Queries a fake-IP server (from <see cref="FakeIpServers"/>) for rules via QueryByFakeIP. Returns
    /// null on failure.
    /// </summary>
    public Task<Dictionary<string, string>?> QueryFakeServerRulesAsync(FakeIpServer server)
        => _source.QueryFakeServerRulesAsync(server.EndPoint, server.AppId);

    // Map tier tunables. When a bucket overflows, maps appearing at least MapPopularityThreshold
    // times in that bucket's (capped) response get their own \map\ query; the top MaxMapsPerLayer
    // are peeled off per layer, and the NOR catch-all recurses at most MaxMapTierDepth times before
    // handing the remainder to the name fan-out.
    private const int MapPopularityThreshold = 100;
    private const int MaxMapsPerLayer = 40;
    private const int MaxMapTierDepth = 3;

    // When a single server name accounts for at least this fraction of a capped bucket, that name
    // can't be split further (the servers are indistinguishable except by address), so we peel it off
    // and enumerate it by host IP instead of drilling its name character by character.
    // MaxIpEnumerationQueries bounds how many \gameaddr\ queries any one enumeration may issue.
    private const double NameClusterFoldFraction = 0.3;
    private const int MaxIpEnumerationQueries = 3000;

    // The "\name_match\" filter token, used to recover the prefix from a batched selector condition.
    private const string NameMatchToken = "\\name_match\\";

    // When enumerating many maps or hosts, several selectors are combined into one query with the
    // \or\ filter to cut the query count. A batch is packed until its estimated server total reaches
    // this fraction of the cap; a batch that still overflows is binary-split and retried.
    private const double OrBatchSafetyFraction = 0.75;

    // Also cap the number of conditions per \or\ batch: many low-estimate selectors would otherwise
    // pack into a single query with an over-long filter string that the master server rejects.
    private const int MaxOrBatchConditions = 48;

    // Tier 2 filter specs: {empty/non-empty} × {linux/non-linux}.
    // Each entry is (NOR conditions, AND conditions); \appid and \nor header are added by BuildFilter.
    private static readonly (string[] Nor, string[] And)[] Tier2Specs =
    [
        (["\\gametype\\valve", "\\empty\\1"],                 ["\\linux\\1"]),              // empty, linux
        (["\\gametype\\valve"],                               ["\\empty\\1", "\\linux\\1"]), // non-empty, linux
        (["\\gametype\\valve", "\\linux\\1", "\\empty\\1"],   []),                           // empty, non-linux
        (["\\gametype\\valve", "\\linux\\1"],                 ["\\empty\\1"]),               // non-empty, non-linux
    ];

    /// <summary>
    /// Queries Steam for game servers using a self-escalating tiered fan-out strategy to maximize
    /// coverage against the 10 000-server per-query cap:
    /// <list type="bullet">
    ///   <item>Tier 1 – single broad query; done if under cap.</item>
    ///   <item>Tier 2 – splits on {empty/non-empty} × {linux/non-linux}; done per bucket if under cap.</item>
    ///   <item>Map tier – any bucket that hits the cap is partitioned by map. The GMS response already
    ///     carries each server's current map, so the popular maps are read straight from the bucket we
    ///     just received (no per-server probing); each gets a <c>\map\</c> query, and a NOR catch-all
    ///     covers the long tail, recursing to peel off the next layer of popular maps.</item>
    ///   <item>Name fan-out – a single map that still exceeds the cap (or the catch-all once no map is
    ///     popular enough) fans out recursively by server-name prefix (a–z, 0–9, common specials).</item>
    /// </list>
    /// All results are deduplicated via a shared <c>seen</c> set.
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
        _runQueries = 0;
        _runCapHits = 0;
        _fakeIpServers.Clear();
        _spamTracker = new SpamHostTracker(_maxServersPerHost, ValveConstants.MaxRealisticPlayers);

        _logger?.LogInformation("Starting query for {Count} app IDs: {AppIds}",
            _appIds.Count, string.Join(", ", _appIds));

        await _source.EnsureConnectedAsync();

        var totalServersReceived = 0;

        foreach (var appId in _appIds)
        {
            totalServersReceived += await QueryAppIdTieredAsync(appId, seen, callback);
        }

        stopwatch.Stop();
        _logger?.LogInformation("Query complete: {Total} unique servers via {Queries} master queries ({CapHits} hit cap) in {Elapsed}ms",
            totalServersReceived, _runQueries, _runCapHits, stopwatch.ElapsedMilliseconds);
    }

    private async Task<int> QueryAppIdTieredAsync(uint appId, HashSet<string> seen, MasterQueryCallback callback)
    {
        var stats = new FanOutStats();
        _stats = stats;
        var stopwatch = Stopwatch.StartNew();
        var totalNew = 0;

        try
        {
            // Tier 1: single broad query.
            var tier1Results = await QuerySourceAsync(appId, ["\\gametype\\valve"], []);
            totalNew += await SendNewAsync(appId,tier1Results, seen, callback);

            if (tier1Results.Count < ValveConstants.MaxServersPerQuery)
                return totalNew;

            _logger?.LogDebug("App {AppId} tier 1 hit cap, expanding to tier 2 combos", appId);

            // Tier 2: split by empty×linux; any bucket that overflows escalates to the map tier.
            foreach (var (nor, and) in Tier2Specs)
            {
                var bucket = await QuerySourceAsync(appId, nor, and);
                totalNew += await SendNewAsync(appId,bucket, seen, callback);

                if (bucket.Count >= ValveConstants.MaxServersPerQuery)
                {
                    _logger?.LogDebug("App {AppId} tier 2 bucket hit cap, starting map fan-out", appId);
                    var handledMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    totalNew += await FanOutByMapAsync(appId, nor, and, bucket, handledMaps, 0, seen, callback);
                }
            }

            return totalNew;
        }
        finally
        {
            stopwatch.Stop();
            _stats = null;
            _runQueries += stats.TotalQueries;
            _runCapHits += stats.CapHits;
            _logger?.LogTrace(
                "App {AppId} fan-out: {Total} queries ({Tier} tier, {Map} map, {Name} name, {Addr} gameaddr; {OrBatch} \\or\\-batched), {CapHits} hit cap → {Servers} servers in {Elapsed}ms",
                appId, stats.TotalQueries, stats.TierQueries, stats.MapQueries, stats.NameQueries, stats.GameAddrQueries,
                stats.OrBatchQueries, stats.CapHits, totalNew, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Issues a master query through the source and records it against the current per-app
    /// <see cref="FanOutStats"/>, categorising by the kind of selector in the filter.
    /// </summary>
    private async Task<IReadOnlyList<MasterServerRecord>> QuerySourceAsync(uint appId, string[] nor, string[] and)
    {
        // Feed discovered spam farms back into the query as a NOR exclusion so deeper-tier queries stop
        // returning them (and are less likely to hit the cap). Skipped when the query already positively
        // selects hosts by \gameaddr\ (IP enumeration), to avoid NOR-ing a host we are enumerating.
        var andHasGameAddr = Array.Exists(and, a => a.Contains("\\gameaddr\\"));
        var spamNor = SpamNorConditions(andHasGameAddr);
        var effectiveNor = spamNor.Length > 0 ? [.. nor, .. spamNor] : nor;

        var results = await _source.QueryWithFilterAsync(appId, BuildFilter(appId, effectiveNor, and));

        if (_stats is { } stats)
        {
            stats.TotalQueries++;
            if (results.Count >= ValveConstants.MaxServersPerQuery)
            {
                stats.CapHits++;
            }

            // Categorise on the caller's own conditions, not the spam-augmented wire filter, so the
            // injected \gameaddr\ NOR doesn't make every query look like an IP-enumeration query.
            var selectors = string.Concat(nor) + string.Concat(and);
            if (selectors.Contains("\\or\\"))
            {
                stats.OrBatchQueries++;
            }

            if (selectors.Contains("\\gameaddr\\"))
            {
                stats.GameAddrQueries++;
            }
            else if (selectors.Contains("\\name_match\\"))
            {
                stats.NameQueries++;
            }
            else if (selectors.Contains("\\map\\"))
            {
                stats.MapQueries++;
            }
            else
            {
                stats.TierQueries++;
            }
        }

        return results;
    }

    /// <summary>
    /// Up to <see cref="ValveConstants.MaxSpamNorHosts"/> <c>\gameaddr\</c> NOR conditions for the spam
    /// farms found so far — or none when the filter is disabled, when the query already selects by
    /// <c>\gameaddr\</c>, or when fewer than two farms are known (a single-IP gameaddr NOR is silently
    /// dropped by the master server and returns nothing).
    /// </summary>
    private string[] SpamNorConditions(bool andHasGameAddr)
    {
        if (!_spamTracker.Enabled || andHasGameAddr)
        {
            return [];
        }

        var hosts = _spamTracker.TopSpamHosts(ValveConstants.MaxSpamNorHosts);
        var conditions = new string[hosts.Count];
        for (var i = 0; i < hosts.Count; i++)
        {
            conditions[i] = $"\\gameaddr\\{hosts[i]}";
        }
        return conditions;
    }

    /// <summary>
    /// Partitions an overflowing bucket by map. The popular maps are taken from <paramref name="sample"/>
    /// (the capped response that triggered this call — its records already carry each server's map) and
    /// queried via <c>\map\</c> filters, several at a time with an <c>\or\</c> batch to keep the query
    /// count down; a NOR catch-all covers everything not on a handled map. If the catch-all still
    /// overflows it recurses (its own response reveals the next layer of popular maps); once no remaining
    /// map clears the popularity threshold — or the depth limit is hit — the remainder falls through to
    /// name fan-out. A single map that exceeds the cap is itself split by name.
    /// </summary>
    private async Task<int> FanOutByMapAsync(
        uint appId, string[] nor, string[] and,
        IReadOnlyList<MasterServerRecord> sample,
        HashSet<string> handledMaps, int depth,
        HashSet<string> seen, MasterQueryCallback callback)
    {
        var popularMaps = sample
            .Where(r => !string.IsNullOrEmpty(r.Map) && IsFilterSafe(r.Map) && !handledMaps.Contains(r.Map))
            .GroupBy(r => r.Map, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Map: g.Key, Count: g.Count()))
            .Where(x => x.Count >= MapPopularityThreshold)
            .OrderByDescending(x => x.Count)
            .Take(MaxMapsPerLayer)
            .ToList();

        if (popularMaps.Count == 0)
        {
            // Nothing dominant left to peel off; split the remaining bucket by name.
            _logger?.LogDebug("App {AppId} no map over threshold at depth {Depth}, name fan-out", appId, depth);
            return await FanOutByNameAsync(appId, nor, and, "", sample, seen, callback);
        }

        var totalNew = 0;

        foreach (var (map, _) in popularMaps)
        {
            handledMaps.Add(map);
        }

        // Query the popular maps, \or\-batched; a single map that still overflows is split by name.
        var mapSelectors = popularMaps.Select(p => ($"\\map\\{p.Map}", p.Count)).ToList();
        totalNew += await QueryOrBatchedAsync(appId, nor, and, mapSelectors,
            onSingleOverflow: (mapCondition, mapResults) =>
            {
                _logger?.LogDebug("App {AppId} {Map} still over cap, name fan-out within map", appId, mapCondition);
                return FanOutByNameAsync(appId, nor, [..and, mapCondition], "", mapResults, seen, callback);
            },
            seen, callback);

        // Catch-all: everything in this bucket that isn't on one of the handled maps.
        string[] catchAllNor = [..nor, ..handledMaps.Select(m => $"\\map\\{m}")];
        var rest = await QuerySourceAsync(appId, catchAllNor, and);
        totalNew += await SendNewAsync(appId,rest, seen, callback);

        if (rest.Count >= ValveConstants.MaxServersPerQuery)
        {
            if (depth + 1 < MaxMapTierDepth)
            {
                // Tail still overflows; rest's response reveals the next batch of popular maps.
                totalNew += await FanOutByMapAsync(appId, nor, and, rest, handledMaps, depth + 1, seen, callback);
            }
            else
            {
                _logger?.LogDebug("App {AppId} map fan-out hit max depth, name fan-out for remainder", appId);
                totalNew += await FanOutByNameAsync(appId, catchAllNor, and, "", rest, seen, callback);
            }
        }

        return totalNew;
    }

    /// <summary>
    /// Narrows an over-cap bucket by server name, using the bucket's own <paramref name="sample"/> to
    /// stay efficient:
    /// <list type="number">
    ///   <item>Any single name that makes up a large fraction of the bucket can't be split by name
    ///     (the servers are identical except for address), so it is peeled off and
    ///     <see cref="EnumerateByIpAsync">enumerated by host IP</see>, then excluded and the remainder
    ///     re-queried.</item>
    ///   <item>The rest is fanned out only over the name prefixes that actually occur in the sample
    ///     (not a blind a–z/0–9 sweep), with a catch-all NOR backstop for any prefix the sample missed.</item>
    /// </list>
    /// </summary>
    private async Task<int> FanOutByNameAsync(
        uint appId, string[] nor, string[] and, string namePrefix,
        IReadOnlyList<MasterServerRecord> sample, HashSet<string> seen, MasterQueryCallback callback)
    {
        // The exact filter that produced `sample` (so IP enumeration stays scoped to this bucket).
        string[] stuckAnd = namePrefix.Length > 0 ? [..and, $"\\name_match\\{namePrefix}*"] : and;

        // Step 1: peel off dominant name clusters (a single name >= NameClusterFoldFraction of the
        // capped bucket) by IP — they overflow the cap but sit on few hosts.
        var foldThreshold = (int)(ValveConstants.MaxServersPerQuery * NameClusterFoldFraction);
        var clusters = sample
            .Where(r => !string.IsNullOrEmpty(r.Name) && IsFilterSafe(r.Name))
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .Where(g => g.Count() >= foldThreshold)
            .Select(g => (Name: g.Key, Records: (IReadOnlyList<MasterServerRecord>)g.ToList()))
            .ToList();

        if (clusters.Count > 0)
        {
            var totalNew = 0;
            var effectiveNor = nor;

            foreach (var (clusterName, records) in clusters)
            {
                _logger?.LogInformation(
                    "App {AppId} folding name cluster '{Name}' ({Count}+ in sample) by host IP.",
                    appId, clusterName, records.Count);
                // Exact-match (no trailing '*') so we capture and exclude only this name, not distinct
                // servers whose name merely starts with it.
                string[] clusterAnd = [..stuckAnd, $"\\name_match\\{clusterName}"];
                totalNew += await EnumerateByIpAsync(appId, effectiveNor, clusterAnd, records, seen, callback);
                effectiveNor = [..effectiveNor, $"\\name_match\\{clusterName}"];
            }

            // Re-query the bucket with the folded clusters excluded to see what remains.
            var remainder = await QuerySourceAsync(appId, effectiveNor, stuckAnd);
            totalNew += await SendNewAsync(appId,remainder, seen, callback);
            if (remainder.Count >= ValveConstants.MaxServersPerQuery)
            {
                // Still over cap after folding — recurse to peel more clusters or prefix-split the rest.
                totalNew += await FanOutByNameAsync(appId, effectiveNor, and, namePrefix, remainder, seen, callback);
            }

            return totalNew;
        }

        // Step 2: prefix fan-out, driven by the next characters actually present in the sample. The
        // per-character selectors are \or\-batched so the (usually many) sparse prefixes collapse into a
        // few queries instead of one tiny query each; a prefix that itself overflows recurses by name.
        var pos = namePrefix.Length;
        var charGroups = sample
            .Select(r => r.Name)
            .Where(n => !string.IsNullOrEmpty(n) && n.Length > pos)
            .GroupBy(n => char.ToLowerInvariant(n[pos]))
            .Where(g => g.Key != '\\' && g.Key != '*' && !char.IsControl(g.Key))
            .Select(g => (Ch: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        var selectors = charGroups.Select(g => ($"\\name_match\\{namePrefix}{g.Ch}*", g.Count)).ToList();

        var total = await QueryOrBatchedAsync(appId, nor, and, selectors,
            onSingleOverflow: (condition, results) =>
            {
                var childPrefix = condition[NameMatchToken.Length..^1]; // strip "\name_match\" … "*"
                _logger?.LogDebug("App {AppId} name prefix '{Prefix}' hit cap, fanning out deeper", appId, childPrefix);
                return FanOutByNameAsync(appId, nor, and, childPrefix, results, seen, callback);
            },
            seen, callback);

        // Catch-all: names under this prefix whose next char wasn't in the sample (long tail), plus
        // names exactly equal to namePrefix.
        string[] catchAllNor = [..nor, ..selectors.Select(s => s.Item1)];
        var catchAll = await QuerySourceAsync(appId, catchAllNor, stuckAnd);
        total += await SendNewAsync(appId,catchAll, seen, callback);
        if (catchAll.Count >= ValveConstants.MaxServersPerQuery)
        {
            // Leftover names share no sampled prefix character; enumerate them by host IP.
            _logger?.LogDebug("App {AppId} name catch-all for prefix '{Prefix}' hit cap; enumerating by host IP",
                appId, namePrefix);
            total += await EnumerateByIpAsync(appId, catchAllNor, stuckAnd, catchAll, seen, callback);
        }

        return total;
    }

    /// <summary>
    /// Last-resort strategy for a bucket that can't be narrowed by map or name: enumerate it by host
    /// IP. Servers must differ by address, and a single <c>\gameaddr\{ip}</c> query returns every port
    /// on that host (well under the cap), so enumerating the distinct IPs present in <paramref name="sample"/>
    /// captures the whole cluster. (Steam's GMS transport doesn't honour <c>collapse_addr_hash</c> or a
    /// NOR over <c>gameaddr</c>, so the sample's own IPs are the available source — complete for any
    /// realistically shaped cluster, which packs many ports onto few hosts.)
    /// </summary>
    private async Task<int> EnumerateByIpAsync(
        uint appId, string[] nor, string[] and,
        IReadOnlyList<MasterServerRecord> sample, HashSet<string> seen, MasterQueryCallback callback)
    {
        // Distinct hosts with their sample counts (a lower bound on the host's true server count, used
        // to pack \or\ batches). gameaddr is IPv4-shaped, so IPv6 hosts are left to the sample/earlier
        // tiers rather than enumerated here.
        var hosts = sample
            .Where(r => r.EndPoint.Address.AddressFamily == AddressFamily.InterNetwork)
            .GroupBy(r => r.EndPoint.Address)
            .Select(g => (Ip: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        if (hosts.Count > MaxIpEnumerationQueries)
        {
            _logger?.LogWarning(
                "App {AppId} cluster spans {Hosts} hosts (> {Max}); enumerating the {Max} largest, some servers may be missing.",
                appId, hosts.Count, MaxIpEnumerationQueries, MaxIpEnumerationQueries);
            hosts = hosts.Take(MaxIpEnumerationQueries).ToList();
        }

        var selectors = hosts.Select(h => ($"\\gameaddr\\{h.Ip}", h.Count)).ToList();

        var totalNew = await QueryOrBatchedAsync(appId, nor, and, selectors,
            onSingleOverflow: (host, _) =>
            {
                _logger?.LogWarning("App {AppId} host {Host} alone exceeds the cap; some of its servers may be missing.",
                    appId, host);
                return Task.FromResult(0);
            },
            seen, callback);

        _logger?.LogDebug("App {AppId} IP enumeration over {Hosts} hosts captured {New} new servers.",
            appId, hosts.Count, totalNew);
        return totalNew;
    }

    /// <summary>
    /// Issues queries over a set of single-condition selectors (e.g. <c>\map\X</c> or <c>\gameaddr\IP</c>),
    /// combining several into one query with the <c>\or\</c> filter to cut the query count. Selectors are
    /// packed (largest first) until the estimated server total reaches <see cref="OrBatchSafetyFraction"/>
    /// of the cap; a batch that still overflows is binary-split and retried (graceful degradation when the
    /// sample under-counts). <paramref name="onSingleOverflow"/> handles a lone selector that itself
    /// exceeds the cap.
    /// </summary>
    private async Task<int> QueryOrBatchedAsync(
        uint appId, string[] nor, string[] baseAnd,
        IReadOnlyList<(string Condition, int Estimate)> selectors,
        Func<string, IReadOnlyList<MasterServerRecord>, Task<int>> onSingleOverflow,
        HashSet<string> seen, MasterQueryCallback callback)
    {
        var threshold = (int)(ValveConstants.MaxServersPerQuery * OrBatchSafetyFraction);
        var total = 0;
        var batch = new List<(string Condition, int Estimate)>();
        var batchEstimate = 0;

        foreach (var selector in selectors)
        {
            if (batch.Count > 0 && (batch.Count >= MaxOrBatchConditions || batchEstimate + selector.Estimate > threshold))
            {
                total += await FlushOrBatchAsync(appId, nor, baseAnd, batch, onSingleOverflow, seen, callback);
                batch = [];
                batchEstimate = 0;
            }

            batch.Add(selector);
            batchEstimate += selector.Estimate;
        }

        if (batch.Count > 0)
        {
            total += await FlushOrBatchAsync(appId, nor, baseAnd, batch, onSingleOverflow, seen, callback);
        }

        return total;
    }

    private async Task<int> FlushOrBatchAsync(
        uint appId, string[] nor, string[] baseAnd,
        List<(string Condition, int Estimate)> batch,
        Func<string, IReadOnlyList<MasterServerRecord>, Task<int>> onSingleOverflow,
        HashSet<string> seen, MasterQueryCallback callback)
    {
        string[] and = batch.Count == 1
            ? [..baseAnd, batch[0].Condition]
            : [..baseAnd, $"\\or\\{batch.Count}{string.Concat(batch.Select(b => b.Condition))}"];

        var results = await QuerySourceAsync(appId, nor, and);
        var newCount = await SendNewAsync(appId,results, seen, callback);

        if (results.Count < ValveConstants.MaxServersPerQuery)
        {
            return newCount;
        }

        // The batch overflowed the cap — the sample under-estimated it.
        if (batch.Count == 1)
        {
            return newCount + await onSingleOverflow(batch[0].Condition, results);
        }

        var mid = batch.Count / 2;
        return newCount
            + await FlushOrBatchAsync(appId, nor, baseAnd, batch.GetRange(0, mid), onSingleOverflow, seen, callback)
            + await FlushOrBatchAsync(appId, nor, baseAnd, batch.GetRange(mid, batch.Count - mid), onSingleOverflow, seen, callback);
    }

    // A value is only safe to splice into a Valve filter if it contains neither the backslash
    // delimiter nor a '*' (which name_match/map would interpret as a wildcard). Unsafe values are left
    // for the prefix catch-all / IP enumeration to sweep up rather than corrupting the filter.
    internal static bool IsFilterSafe(string value) => !value.Contains('\\') && !value.Contains('*');

    private async Task<int> SendNewAsync(uint appId, IReadOnlyList<MasterServerRecord> servers, HashSet<string> seen, MasterQueryCallback callback)
    {
        var newServers = FilterNewServers(appId, servers, seen);
        if (newServers.Count > 0)
        {
            _logger?.LogDebug("Callback: {Count} new servers ({Dupes} duplicates filtered)",
                newServers.Count, servers.Count - newServers.Count);
            await callback(newServers);
        }
        return newServers.Count;
    }

    internal static string BuildFilter(uint appId, string[] nor, string[] and)
    {
        var norPart = nor.Length > 0 ? $"\\nor\\{nor.Length}{string.Concat(nor)}" : "";
        return $"\\appid\\{appId}{norPart}{string.Concat(and)}";
    }

    /// <summary>
    /// Deduplicates servers and returns the new directly-addressable entries (endpoint plus GMS
    /// player/bot counts) for the callback. SDR / fake-IP (169.254.*) servers can't be reached by UDP
    /// A2S: when <see cref="IncludeFakeIp"/> is set they're collected (with their app id and GMS counts)
    /// into <see cref="FakeIpServers"/> for QueryByFakeIP instead; otherwise they're dropped.
    /// </summary>
    private List<MasterServerEntry> FilterNewServers(uint appId, IEnumerable<MasterServerRecord> servers, HashSet<string> seen)
    {
        var newServers = new List<MasterServerEntry>();

        foreach (var record in servers)
        {
            var server = record.EndPoint;
            var key = server.ToString();

            // SDR / fake-IP (169.254.0.0/16) servers are handled here and never reach the spam filter
            // below: they legitimately share addresses across the SDR network, so per-host server counts
            // are meaningless for them and they must not be culled as "farms".
            if (FakeIpMapper.IsFakeIp(server.Address))
            {
                if (_includeFakeIp && seen.Add(key))
                {
                    _fakeIpServers.Add(new FakeIpServer(server, appId, record.Players, record.Bots, record.MaxPlayers));
                }
                continue;
            }

            // Count this newly-seen host and drop it if it's a spam farm (or a single server reporting an
            // impossible player count). A host can be flagged after earlier servers were already emitted;
            // those are pruned by the consumer via SpamHosts.
            if (seen.Add(key) && _spamTracker.Observe(server.Address, record.Players))
            {
                newServers.Add(new MasterServerEntry(server, record.Players, record.Bots, record.MaxPlayers));
            }
        }

        return newServers;
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

        // Dispose a source we own (e.g. the Web API HttpClient); never the shared Steam pool, which
        // stays alive for reuse.
        if (_ownsSource && _source is IDisposable disposableSource)
        {
            disposableSource.Dispose();
        }
    }
}
