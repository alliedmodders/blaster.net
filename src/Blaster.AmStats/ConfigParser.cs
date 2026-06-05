// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

namespace Blaster.AmStats;

/// <summary>
/// Simple YAML configuration parser for database connection settings.
/// Parses basic YAML format used by AmStats.
/// </summary>
public static class ConfigParser
{
    public static Dictionary<string, string> Parse(string filePath)
    {
        var config = new Dictionary<string, string>();
        
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Config file not found: {filePath}");

        var lines = File.ReadAllLines(filePath);
        string? currentSection = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            // Check for section headers (e.g., "database:")
            if (trimmed.EndsWith(":") && !trimmed.Contains(' '))
            {
                currentSection = trimmed.TrimEnd(':');
                continue;
            }

            // Parse key-value pairs (e.g., "  host: localhost")
            if (trimmed.Contains(':'))
            {
                var parts = trimmed.Split(':', 2);
                if (parts.Length == 2)
                {
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    // Handle quoted values
                    if (value.StartsWith('"') && value.EndsWith('"'))
                        value = value.Substring(1, value.Length - 2);

                    var fullKey = currentSection != null ? $"{currentSection}.{key}" : key;
                    config[fullKey] = value;
                }
            }
        }

        return config;
    }
}
