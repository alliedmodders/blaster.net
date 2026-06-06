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
            string? webApiKey = null;
            var transport = "steam";

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
                    case "--transport":
                        if (i + 1 < args.Length)
                            transport = args[++i];
                        break;
                    case "--steam-webapi-key":
                        if (i + 1 < args.Length)
                            webApiKey = args[++i];
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

            var transportMode = MasterServerTransports.Parse(transport);
            webApiKey ??= Environment.GetEnvironmentVariable("BLASTER_STEAM_WEBAPI_KEY");
            steamUsername ??= Environment.GetEnvironmentVariable("BLASTER_STEAM_USERNAME");
            steamPassword ??= Environment.GetEnvironmentVariable("BLASTER_STEAM_PASSWORD");

            if (transportMode == MasterServerTransport.WebApi)
            {
                if (string.IsNullOrWhiteSpace(webApiKey))
                {
                    Console.Error.WriteLine(
                        "Error: --transport web-api requires a Web API key. Set --steam-webapi-key or BLASTER_STEAM_WEBAPI_KEY.");
                    return 1;
                }
            }
            else if (string.IsNullOrWhiteSpace(steamUsername) || string.IsNullOrWhiteSpace(steamPassword))
            {
                Console.Error.WriteLine(
                    "Error: the Steam connection requires credentials. Set --steam-username/--steam-password " +
                    "or BLASTER_STEAM_USERNAME/BLASTER_STEAM_PASSWORD (or use --transport web-api with a Web API key).");
                return 1;
            }

            using (var loggerFactory = LoggerFactory.Create(builder =>
                builder.SetMinimumLevel(logLevel)
                       .AddCompactConsole()))
            {
                await HandleCommand(appIds.ToArray(), format, skipInfo, skipRules, concurrency, transportMode, steamUsername, steamPassword, webApiKey, loggerFactory);
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
  --transport <T>        Master-server transport: steam or web-api (default: steam)
  --steam-username <U>   Steam username (or BLASTER_STEAM_USERNAME); steam transport
  --steam-password <P>   Steam password (or BLASTER_STEAM_PASSWORD); steam transport
  --steam-webapi-key <K> Steam Web API key (or BLASTER_STEAM_WEBAPI_KEY); web-api transport
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
        MasterServerTransport transport,
        string? steamUsername,
        string? steamPassword,
        string? webApiKey,
        ILoggerFactory loggerFactory)
    {
        var querier = new CliServerQuerier(concurrency, transport, steamUsername, steamPassword, webApiKey, loggerFactory);
        var results = await querier.QueryServersAsync(appIds, skipInfo: skipInfo, skipRules: skipRules);

        var formatter = new OutputFormatter();
        using var stdout = Console.OpenStandardOutput();
        formatter.Write(results, format, stdout);
    }
}
