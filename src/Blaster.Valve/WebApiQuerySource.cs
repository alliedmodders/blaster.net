// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SteamKit2;

namespace Blaster.Valve;

/// <summary>
/// <see cref="IMasterQuerySource"/> backed by the Steam Web API
/// (<c>IGameServersService/GetServerList</c>) instead of a live Steam connection. It speaks the same
/// Master Server filter language and is subject to the same 10k-per-query cap, so the
/// <see cref="MasterServerQuerier"/> fan-out works against it unchanged. The Web API response also
/// carries <c>os</c> and <c>dedicated</c>, which the GMS message lacks.
/// </summary>
internal sealed class WebApiQuerySource : IMasterQuerySource, IDisposable
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    // Process-wide throttle slot: the documented 200-req/5-min limit is per key, so spacing must hold
    // across every querier instance that shares it (e.g. the CLI's per-appid loop).
    private static readonly Lock _throttleLock = new();
    private static DateTime _nextSlotUtc = DateTime.MinValue;

    private readonly WebAPI.AsyncInterface _gameServers;
    private readonly ILogger? _logger;

    public WebApiQuerySource(string apiKey, ILogger? logger = null)
    {
        _logger = logger;
        _gameServers = WebAPI.GetAsyncInterface("IGameServersService", apiKey);
    }

    // The Web API is stateless HTTP; there is nothing to connect.
    public Task EnsureConnectedAsync() => Task.CompletedTask;

    public async Task<IReadOnlyList<MasterServerRecord>> QueryWithFilterAsync(uint appId, string filter)
    {
        var errorAttempts = 0;
        var rateLimitWaits = 0;

        while (true)
        {
            await ThrottleAsync().ConfigureAwait(false);

            try
            {
                return await QueryOnceAsync(filter).ConfigureAwait(false);
            }
            catch (SteamKitWebRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (++rateLimitWaits > ValveConstants.WebApiMaxRateLimitWaits)
                {
                    _logger?.LogError("Web API still rate limited (429) after {Waits} waits (filter: {Filter}); giving up on this query.",
                        rateLimitWaits - 1, filter);
                    return [];
                }

                var delay = ParseRetryAfter(ex.Headers.RetryAfter, DateTimeOffset.UtcNow)
                    ?? TimeSpan.FromMilliseconds(ValveConstants.WebApiMinIntervalMs * rateLimitWaits);
                if (delay > MaxRetryAfter)
                {
                    delay = MaxRetryAfter;
                }

                _logger?.LogWarning("Web API rate limited (429); waiting {Delay} before retry (filter: {Filter}).", delay, filter);
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (++errorAttempts >= ValveConstants.MasterQueryMaxAttempts)
                {
                    _logger?.LogError("Web API query failed after {Attempts} attempts (filter: {Filter}): {Error}. Giving up on this query.",
                        errorAttempts, filter, ex.Message);
                    return [];
                }

                var backoff = TimeSpan.FromMilliseconds(ValveConstants.MasterQueryRetryBaseDelayMs * errorAttempts);
                _logger?.LogWarning("Web API query attempt {Attempt} failed (filter: {Filter}): {Error}. Retrying in {Backoff}.",
                    errorAttempts, filter, ex.Message, backoff);
                await Task.Delay(backoff).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reserves the next throttle slot (process-wide) and waits for it, so requests are spaced to the
    /// documented Web API rate limit even across concurrent/sequential querier instances.
    /// </summary>
    private static async Task ThrottleAsync()
    {
        TimeSpan wait;
        lock (_throttleLock)
        {
            var now = DateTime.UtcNow;
            var slot = now < _nextSlotUtc ? _nextSlotUtc : now;
            wait = slot - now;
            _nextSlotUtc = slot + TimeSpan.FromMilliseconds(ValveConstants.WebApiMinIntervalMs);
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Converts an HTTP <c>Retry-After</c> header (delta seconds or an absolute date) into a delay.
    /// Returns null when the header is absent.
    /// </summary>
    internal static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
    {
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - now;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private async Task<IReadOnlyList<MasterServerRecord>> QueryOnceAsync(string filter)
    {
        var args = new Dictionary<string, object?>
        {
            { "filter", filter },
            { "limit", ValveConstants.MaxServersPerQuery },
        };

        var response = await _gameServers
            .CallAsync(HttpMethod.Get, "GetServerList", 1, args)
            .ConfigureAwait(false);

        var servers = ParseServerList(response);
        _logger?.LogInformation("Web API returned {Count} servers (filter: {Filter})", servers.Count, filter);
        return servers;
    }

    /// <summary>
    /// Maps a <c>GetServerList</c> VDF response (<c>response { servers { 0 {..} 1 {..} } }</c>) to
    /// <see cref="MasterServerRecord"/>s. Factored out so it can be unit-tested without an HTTP call.
    /// </summary>
    internal static IReadOnlyList<MasterServerRecord> ParseServerList(KeyValue response)
    {
        // The dynamic interface hands back the "response" node itself; tolerate an extra wrapping level.
        var servers = response["servers"].Children.Count > 0
            ? response["servers"]
            : response["response"]["servers"];

        var list = new List<MasterServerRecord>(servers.Children.Count);
        foreach (var s in servers.Children)
        {
            // "addr" is "ip:port"; skip anything we can't parse (e.g. SDR/fake-IP entries).
            if (!IPEndPoint.TryParse(s["addr"].Value ?? "", out var endpoint))
            {
                continue;
            }

            list.Add(new MasterServerRecord
            {
                EndPoint = endpoint,
                Map = s["map"].Value ?? "",
                GameDir = s["gamedir"].Value ?? "",
                Name = s["name"].Value ?? "",
                GameType = s["gametype"].Value ?? "",
                Players = s["players"].AsUnsignedInteger(),
                MaxPlayers = s["max_players"].AsUnsignedInteger(),
                Bots = s["bots"].AsUnsignedInteger(),
                AppId = s["appid"].AsUnsignedInteger(),
            });
        }

        return list;
    }

    public void Dispose() => _gameServers.Dispose();
}
