// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Dapper;
using MySqlConnector;
using System.Data;
using System.IO;
using System.Net.Sockets;

namespace Blaster.AmStats;

/// <summary>
/// Manages database connections and queries using Dapper.
/// Configuration is read from YAML files.
/// </summary>
public class DatabaseConnection : IDisposable
{
    private const int MaxRetryAttempts = 3;
    private readonly string _connectionString;
    private readonly System.Threading.Lock _connectionLock = new();
    private MySqlConnection _connection;

    public DatabaseConnection(string connectionString)
    {
        _connectionString = connectionString;
        _connection = new MySqlConnection(_connectionString);
        _connection.Open();
    }

    public IEnumerable<T> Query<T>(string sql, object? param = null)
    {
        return ExecuteWithConnection(conn => conn.Query<T>(sql, param).AsList());
    }

    public T? QuerySingleOrDefault<T>(string sql, object? param = null)
    {
        return ExecuteWithConnection(conn => conn.QuerySingleOrDefault<T>(sql, param));
    }

    public int Execute(string sql, object? param = null)
    {
        return ExecuteWithConnection(conn => conn.Execute(sql, param));
    }

    public long ExecuteAndReadId(string sql, object? param, string idQuery, object? idQueryParam = null)
    {
        return ExecuteWithConnection(conn =>
        {
            conn.Execute(sql, param);
            return conn.QuerySingleOrDefault<long>(idQuery, idQueryParam ?? param);
        });
    }

    public void Dispose()
    {
        lock (_connectionLock)
        {
            _connection.Dispose();
        }
    }

    private T ExecuteWithConnection<T>(Func<MySqlConnection, T> action)
    {
        lock (_connectionLock)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    EnsureOpenConnection();
                    return action(_connection);
                }
                catch (Exception ex) when (ShouldReconnectAndRetry(ex) && attempt < MaxRetryAttempts)
                {
                    Reconnect();
                    Thread.Sleep(100 * attempt);
                }
            }
        }
    }

    private bool ShouldReconnectAndRetry(Exception ex)
    {
        if (_connection.State is ConnectionState.Broken or ConnectionState.Closed)
            return true;

        if (ex is SocketException socketEx
            && socketEx.SocketErrorCode == SocketError.ConnectionReset)
        {
            return true;
        }

        if (ex is IOException ioEx
            && ioEx.Message.Contains("Connection reset by peer", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ex is InvalidOperationException invalidOperationEx
            && invalidOperationEx.Message.Contains("Connection must be Open", StringComparison.Ordinal))
        {
            return true;
        }

        if (ex is not MySqlException mySqlEx)
            return false;

        if (mySqlEx.IsTransient)
            return true;

        if (mySqlEx.InnerException?.Message.Contains("Connection reset by peer", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        if (mySqlEx.Message.Contains("Connection reset by peer", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private void EnsureOpenConnection()
    {
        if (_connection.State == ConnectionState.Open)
            return;

        if (_connection.State == ConnectionState.Broken)
        {
            Reconnect();
            return;
        }

        _connection.Open();
    }

    private void Reconnect()
    {
        _connection.Dispose();
        _connection = new MySqlConnection(_connectionString);
        _connection.Open();
    }

    /// <summary>
    /// Parses a simple YAML connection string configuration.
    /// Expected format (in YAML):
    /// database:
    ///   host: localhost
    ///   username: user
    ///   password: pass
    ///   dbname: dbname
    /// </summary>
    public static string ParseConnectionString(string host, string username, string password, string dbname)
    {
        var server = host;
        uint port = 3306;
        var hostParts = host.Split(':', 2);
        if (hostParts.Length == 2 && uint.TryParse(hostParts[1], out var parsedPort))
        {
            server = hostParts[0];
            port = parsedPort;
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = server,
            Port = port,
            UserID = username,
            Database = dbname,
            AllowUserVariables = true
        };

        if (!string.IsNullOrEmpty(password))
            builder.Password = password;

        return builder.ConnectionString;
    }
}
