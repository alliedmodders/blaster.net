// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using Blaster.Valve;
using Microsoft.Extensions.Logging;

namespace Blaster.CLI;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            var appIds = new List<int>();
            var format = "list";
            var skipInfo = false;
            var skipRules = false;
            var concurrency = 20;
            var logLevel = LogLevel.Information;
            string? steamUsername = null;
            string? steamPassword = null;

            int i = 0;
            while (i < args.Length)
            {
                switch (args[i])
                {
                    case "--appids":
                        i++;
                        while (i < args.Length && !args[i].StartsWith("--"))
                        {
                            if (int.TryParse(args[i], out var appId))
                                appIds.Add(appId);
                            i++;
                        }
                        i--;
                        break;
                    case "--format":
                        if (i + 1 < args.Length)
                            format = args[++i];
                        break;
                    case "--no-info":
                        skipInfo = true;
                        break;
                    case "--no-rules":
                        skipRules = true;
                        break;
                    case "--concurrency":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var c))
                            concurrency = c;
                        break;
                    case "--log-level":
                        if (i + 1 < args.Length)
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
                        break;
                    case "--steam-username":
                        if (i + 1 < args.Length)
                            steamUsername = args[++i];
                        break;
                    case "--steam-password":
                        if (i + 1 < args.Length)
                            steamPassword = args[++i];
                        break;
                    case "--help":
                    case "-h":
                        ShowHelp();
                        return 0;
                }
                i++;
            }

            if (appIds.Count == 0)
            {
                Console.Error.WriteLine("Error: --appids is required");
                ShowHelp();
                return 1;
            }

            steamUsername ??= Environment.GetEnvironmentVariable("BLASTER_STEAM_USERNAME");
            steamPassword ??= Environment.GetEnvironmentVariable("BLASTER_STEAM_PASSWORD");

            if (string.IsNullOrWhiteSpace(steamUsername) || string.IsNullOrWhiteSpace(steamPassword))
            {
                Console.Error.WriteLine(
                    "Error: Steam credentials are required. Set --steam-username/--steam-password " +
                    "or BLASTER_STEAM_USERNAME/BLASTER_STEAM_PASSWORD.");
                return 1;
            }

            using (var loggerFactory = LoggerFactory.Create(builder =>
                builder.SetMinimumLevel(logLevel)
                       .AddConsole()))
            {
                await HandleCommand(appIds.ToArray(), format, skipInfo, skipRules, concurrency, steamUsername, steamPassword, loggerFactory);
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
Blaster - Query Valve game servers

Usage:
  blaster [OPTIONS]

Options:
  --appids <IDS>         Valve application IDs to query (required, space-separated)
  --format <FORMAT>      Output format: list, map, or lines (default: list)
  --steam-username <U>   Steam username (or BLASTER_STEAM_USERNAME)
  --steam-password <P>   Steam password (or BLASTER_STEAM_PASSWORD)
  --log-level <LEVEL>    Log level: trace, debug, info, warn, error, critical (default: info)
  --no-info              Skip server info queries
  --no-rules             Skip rules queries
  --concurrency <N>      Max concurrent servers to query (default: 20)
  --help                 Show this help message
");
    }

    static async Task HandleCommand(
        int[] appIds,
        string format,
        bool skipInfo,
        bool skipRules,
        int concurrency,
        string steamUsername,
        string steamPassword,
        ILoggerFactory loggerFactory)
    {
        var querier = new CliServerQuerier(concurrency, steamUsername, steamPassword, loggerFactory);
        var results = await querier.QueryServersAsync(appIds, skipInfo: skipInfo, skipRules: skipRules);

        var formatter = new OutputFormatter();
        var output = formatter.Format(results, format);
        Console.Write(output);
    }
}
