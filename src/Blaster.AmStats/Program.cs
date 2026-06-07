// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Blaster.AmStats;
using Blaster.Valve;
using Microsoft.Extensions.Logging;

namespace Blaster.AmStats;

internal class Program
{
    static int Main(string[] args)
    {
        try
        {
            string config = "config.yml";
            string? game = null;
            var logLevel = LogLevel.Information;
            string? steamUsername = null;
            string? steamPassword = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length)
                    config = args[++i];
                else if (args[i] == "--game" && i + 1 < args.Length)
                    game = args[++i];
                else if (args[i] == "--log-level" && i + 1 < args.Length)
                {
                    logLevel = args[++i] switch
                    {
                        "trace" => LogLevel.Trace,
                        "debug" => LogLevel.Debug,
                        "info" => LogLevel.Information,
                        "warn" => LogLevel.Warning,
                        "error" => LogLevel.Error,
                        "critical" => LogLevel.Critical,
                        _ => LogLevel.Information
                    };
                }
                else if (args[i] == "--steam-username" && i + 1 < args.Length)
                    steamUsername = args[++i];
                else if (args[i] == "--steam-password" && i + 1 < args.Length)
                    steamPassword = args[++i];
                else if (args[i] == "--help")
                {
                    ShowHelp();
                    return 0;
                }
            }

            if (string.IsNullOrEmpty(game))
            {
                Console.Error.WriteLine("Error: --game is required");
                ShowHelp();
                return 1;
            }

            using (var loggerFactory = LoggerFactory.Create(builder =>
                builder.SetMinimumLevel(logLevel)
                       .AddCompactConsole()))
            {
                HandleCommand(config, game, logLevel, steamUsername, steamPassword, loggerFactory);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static void ShowHelp()
    {
        Console.WriteLine(@"
AmStats - Collect Valve game server statistics

Usage:
  amstats [OPTIONS]

Options:
  --config <PATH>         Config file path (default: config.yml)
  --game <GAME>           Game to query: hl1 or hl2 (required)
  --steam-username <U>    Steam username (overrides config/env)
  --steam-password <P>    Steam password (overrides config/env)
  --log-level <LEVEL>     Log level: trace, debug, info, warn, error, critical (default: info)
  --help                  Show this help message
");
    }

    static void HandleCommand(string config, string game, LogLevel logLevel, string? steamUsername, string? steamPassword, ILoggerFactory loggerFactory)
    {
        var gameId = game switch
        {
            "hl1" => 1L,
            "hl2" => 2L,
            _ => throw new ArgumentException($"Unrecognized game: {game}")
        };

        var collector = new StatsCollector(config, gameId, steamUsername, steamPassword, loggerFactory);
        collector.Collect();
        collector.Finish();
    }
}
