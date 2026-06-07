// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.Net;
using System.Net.Sockets;

namespace Blaster.Valve;

/// <summary>
/// Exception thrown by master server queries.
/// </summary>
public class MasterServerException : Exception
{
    public MasterServerException(string message) : base(message) { }
}

/// <summary>
/// Exception thrown by server queries.
/// </summary>
public class ServerQueryException : Exception
{
    public ServerQueryException(string message) : base(message) { }
}

/// <summary>
/// Wrapper around UDP socket for Valve protocol communication with rate limiting.
/// </summary>
public class UdpSocket : IDisposable
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _remoteEndPoint;
    private TimeSpan _timeout = TimeSpan.FromMinutes(5);
    private TimeSpan _rateLimitWait = TimeSpan.Zero;
    private DateTime _nextAllowedTime = DateTime.MinValue;
    private byte[] _receiveBuffer = new byte[ValveConstants.MaxPacketSize];

    public UdpSocket(string hostAndPort, TimeSpan timeout)
    {
        _timeout = timeout;
        
        var parts = hostAndPort.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
        {
            throw new ArgumentException($"Invalid host:port format: {hostAndPort}");
        }

        var addresses = Dns.GetHostAddresses(parts[0]);
        if (addresses.Length == 0)
        {
            throw new ArgumentException($"Could not resolve host: {parts[0]}");
        }

        _remoteEndPoint = new IPEndPoint(addresses[0], port);
        _client = new UdpClient();
        _client.Connect(_remoteEndPoint);
    }

    /// <summary>
    /// Sets the timeout for socket operations.
    /// </summary>
    public void SetTimeout(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    /// <summary>
    /// Gets the remote address this socket is connected to.
    /// </summary>
    public string RemoteAddr => _remoteEndPoint.ToString();

    /// <summary>
    /// Sets the rate limit in queries per minute.
    /// </summary>
    public void SetRateLimit(int queriesPerMinute)
    {
        if (queriesPerMinute > 0)
        {
            _rateLimitWait = TimeSpan.FromMilliseconds(60000.0 / queriesPerMinute) + TimeSpan.FromSeconds(1);
        }
    }

    /// <summary>
    /// Sends data to the remote endpoint, respecting rate limits.
    /// </summary>
    public async Task SendAsync(byte[] data)
    {
        EnforceRateLimit();
        try
        {
            await _client.SendAsync(data, data.Length);
        }
        finally
        {
            SetNextQueryTime();
        }
    }

    /// <summary>
    /// Sends data synchronously.
    /// </summary>
    public void Send(byte[] data)
    {
        EnforceRateLimit();
        try
        {
            _client.Send(data, data.Length);
        }
        finally
        {
            SetNextQueryTime();
        }
    }

    /// <summary>
    /// Receives data from the remote endpoint.
    /// </summary>
    public async Task<byte[]> RecvAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(_timeout);
            var result = await _client.ReceiveAsync(cts.Token);
            return result.Buffer;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Socket operation timed out after {_timeout.TotalSeconds} seconds");
        }
        finally
        {
            SetNextQueryTime();
        }
    }

    /// <summary>
    /// Receives data synchronously.
    /// </summary>
    public byte[] Recv()
    {
        try
        {
            _client.Client.ReceiveTimeout = (int)_timeout.TotalMilliseconds;
            int bytesRead = _client.Client.Receive(_receiveBuffer, 0, ValveConstants.MaxPacketSize, SocketFlags.None);
            byte[] result = new byte[bytesRead];
            Array.Copy(_receiveBuffer, result, bytesRead);
            return result;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            throw new TimeoutException($"Socket operation timed out after {_timeout.TotalSeconds} seconds", ex);
        }
        finally
        {
            SetNextQueryTime();
        }
    }

    private void EnforceRateLimit()
    {
        if (_rateLimitWait == TimeSpan.Zero)
        {
            return;
        }

        TimeSpan wait = _nextAllowedTime - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            System.Threading.Thread.Sleep(wait);
        }
    }

    private void SetNextQueryTime()
    {
        if (_rateLimitWait != TimeSpan.Zero)
        {
            _nextAllowedTime = DateTime.UtcNow + _rateLimitWait;
        }
    }

    public void Close()
    {
        _client?.Close();
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}
