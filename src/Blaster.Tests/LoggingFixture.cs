// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Blaster.Tests;

/// <summary>
/// Fixture providing structured logging for tests using Serilog.
/// Logs are output to the console during test execution.
/// </summary>
public class LoggingFixture
{
    private readonly ILoggerFactory _loggerFactory;

    public LoggingFixture()
    {
        // Configure Serilog with console output for tests
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff}] [{Level}] {SourceContext}: {Message:j}{NewLine}{Exception}")
            .CreateLogger();

        _loggerFactory = new SerilogLoggerFactory(Log.Logger, dispose: false);
    }

    public ILogger<T> CreateLogger<T>() where T : class
    {
        return _loggerFactory.CreateLogger<T>();
    }

    public void Dispose()
    {
        _loggerFactory?.Dispose();
        Log.CloseAndFlush();
    }
}
