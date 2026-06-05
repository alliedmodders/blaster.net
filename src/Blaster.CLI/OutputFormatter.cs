// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System.Text.Json;
using System.Text.Json.Nodes;
using Blaster.Valve;

namespace Blaster.CLI;

/// <summary>
/// Formats query results as JSON in various output modes.
/// </summary>
public class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Formats results according to the specified format.
    /// </summary>
    public string Format(List<QueryResult> results, string format)
    {
        return format.ToLower() switch
        {
            "list" => FormatList(results),
            "map" => FormatMap(results),
            "lines" => FormatLines(results),
            _ => throw new ArgumentException($"Unknown format: {format}")
        };
    }

    private string FormatList(List<QueryResult> results)
    {
        var output = new JsonArray();

        foreach (var result in results)
        {
            output.Add((JsonNode?)FormatResult(result));
        }

        return output.ToJsonString(JsonOptions);
    }

    private string FormatMap(List<QueryResult> results)
    {
        var output = new JsonObject();

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var key = result.Server ?? $"error_{i}";
            output[key] = FormatResult(result);
        }

        return output.ToJsonString(JsonOptions);
    }

    private string FormatLines(List<QueryResult> results)
    {
        var lines = new List<string>();

        foreach (var result in results)
        {
            var obj = FormatResult(result);
            lines.Add(obj.ToJsonString(JsonOptions));
        }

        return string.Join("\n", lines);
    }

    private JsonObject FormatResult(QueryResult result)
    {
        var obj = new JsonObject();

        if (result.Server != null)
            obj["server"] = result.Server;

        if (result.AppId != 0)
            obj["appid"] = result.AppId;

        if (result.Info != null)
            obj["info"] = FormatServerInfo(result.Info);
        
        if (result.InfoError != null)
            obj["info_error"] = result.InfoError;

        if (result.Rules != null && result.Rules.Count > 0)
        {
            var rules = new JsonObject();
            foreach (var (key, value) in result.Rules)
            {
                rules[key] = value;
            }

            obj["rules"] = rules;
        }
        
        if (result.RulesError != null)
            obj["rules_error"] = result.RulesError;

        if (result.Error != null)
            obj["error"] = result.Error;

        return obj;
    }

    private JsonObject FormatServerInfo(ServerInfo info)
    {
        var obj = new JsonObject();

        obj["protocol"] = info.Protocol;
        
        if (info.Name != null)
            obj["name"] = info.Name;
        
        if (info.MapName != null)
            obj["map"] = info.MapName;
        
        if (info.Folder != null)
            obj["folder"] = info.Folder;
        
        if (info.Game != null)
            obj["game"] = info.Game;
        
        obj["appid"] = JsonValue.Create((int)(info.Ext?.AppId ?? AppId.Unknown));
        obj["players"] = info.Players;
        obj["max_players"] = info.MaxPlayers;
        obj["bots"] = info.Bots;

        var typeValue = info.Type switch
        {
            ServerType.Dedicated => "d",
            ServerType.Listen => "l",
            ServerType.HLTV => "p",
            _ => "u"
        };
        obj["type"] = typeValue;

        var osValue = info.OS switch
        {
            ServerOS.Linux => "l",
            ServerOS.Windows => "w",
            _ => "u"
        };
        obj["os"] = osValue;

        obj["visibility"] = info.Visibility;
        obj["vac"] = info.Vac;
        
        if (info.Ext?.GameVersion != null)
            obj["version"] = info.Ext.GameVersion;

        if (info.Mod != null)
        {
            obj["mod"] = new JsonObject
            {
                ["url"] = info.Mod.Url,
                ["download_url"] = info.Mod.DwlUrl,
                ["version"] = info.Mod.Version,
                ["size"] = info.Mod.Size,
                ["type"] = info.Mod.Type,
                ["dll"] = info.Mod.Dll
            };
        }

        if (info.TheShip != null)
        {
            obj["the_ship"] = new JsonObject
            {
                ["mode"] = info.TheShip.Mode,
                ["witnesses"] = info.TheShip.Witnesses,
                ["duration"] = info.TheShip.Duration
            };
        }

        return obj;
    }
}
