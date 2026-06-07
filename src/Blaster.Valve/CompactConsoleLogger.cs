// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Microsoft.Extensions.Logging;

namespace Blaster.Valve;

/// <summary>
/// Registers <see cref="CompactConsoleLoggerProvider"/>, a minimal single-line console logger.
/// </summary>
public static class CompactConsoleLoggerExtensions
{
    /// <summary>
    /// Adds a compact, single-line console logger that writes
    /// <c>HH:mm:ss LVL  message</c> to standard error. Unlike the default console formatter it
    /// omits the redundant category/event-id prefix, and routing to stderr keeps stdout clean for
    /// data output (e.g. the CLI's JSON).
    /// </summary>
    public static ILoggingBuilder AddCompactConsole(this ILoggingBuilder builder)
    {
        builder.AddProvider(new CompactConsoleLoggerProvider());
        return builder;
    }
}

internal sealed class CompactConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => CompactConsoleLogger.Instance;

    public void Dispose() { }
}

internal sealed class CompactConsoleLogger : ILogger
{
    public static readonly CompactConsoleLogger Instance = new();

    private static readonly object WriteLock = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
            return;

        var line = $"{DateTime.Now:HH:mm:ss} {Abbreviate(logLevel)}  {message}";

        lock (WriteLock)
        {
            Console.Error.WriteLine(line);
            if (exception is not null)
                Console.Error.WriteLine(exception);
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };
}
