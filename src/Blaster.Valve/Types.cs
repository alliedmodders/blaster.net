// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

namespace Blaster.Valve;

public enum GameEngine
{
    Goldsrc = 1,
    Source = 2
}

public enum ServerType
{
    Unknown = 0,
    Dedicated = 1,
    Listen = 2,
    HLTV = 3
}

public enum ServerOS
{
    Unknown = 0,
    Windows = 1,
    Linux = 2,
    Mac = 3
}

public static class ServerTypeExtensions
{
    public static string ToDisplayString(this ServerType type) => type switch
    {
        ServerType.Dedicated => "dedicated",
        ServerType.Listen => "listen",
        ServerType.HLTV => "hltv",
        _ => "unknown"
    };
}

public static class ServerOSExtensions
{
    public static string ToDisplayString(this ServerOS os) => os switch
    {
        ServerOS.Windows => "windows",
        ServerOS.Linux => "linux",
        ServerOS.Mac => "mac",
        _ => "unknown"
    };
}

/// <summary>
/// Optional mod information returned by S2A_INFO_GOLDSRC.
/// </summary>
public class ModInfo
{
    public string Url { get; set; } = "";
    public string DwlUrl { get; set; } = "";
    public uint Version { get; set; }
    public uint Size { get; set; }
    public byte Type { get; set; }
    public byte Dll { get; set; }
}

/// <summary>
/// Optional information returned by App_TheShip.
/// </summary>
public class TheShipInfo
{
    public byte Mode { get; set; }
    public byte Witnesses { get; set; }
    public byte Duration { get; set; }
}

/// <summary>
/// Optional information available with S2A_INFO_SOURCE.
/// </summary>
public class SpecTvInfo
{
    public ushort Port { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Optional information available with S2A_INFO_SOURCE. This is a grab-bag
/// of various optional bits. If some are not present they are left as 0/empty.
/// </summary>
public class ExtendedInfo
{
    public AppId AppId { get; set; }
    public string GameVersion { get; set; } = "";
    public ushort Port { get; set; } // 0 if not present
    public ulong SteamId { get; set; } // 0 if not present
    public string GameModeDescription { get; set; } = ""; // "" if not present
    public ulong GameId { get; set; } // 0 if not present
}

/// <summary>
/// Information returned by an A2S_INFO query. Most of this is returned as-is
/// from the wire, except where otherwise noted.
/// </summary>
public class ServerInfo
{
    /// <summary>
    /// The address can be arbitrary in older replies; for Source servers, it is
    /// computed as the address and port used to connect. It should not be relied
    /// on for reconnecting to the server.
    /// </summary>
    public string Address { get; set; } = "";

    /// <summary>
    /// One of the A2S_INFO constants (S2A_INFO_GOLDSRC or S2A_INFO_SOURCE).
    /// </summary>
    public byte InfoVersion { get; set; }

    public byte Protocol { get; set; }
    public string Name { get; set; } = "";
    public string MapName { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Game { get; set; } = "";
    public byte Players { get; set; }
    public byte MaxPlayers { get; set; }
    public byte Bots { get; set; }
    public ServerType Type { get; set; }
    public ServerOS OS { get; set; }
    public byte Visibility { get; set; }
    public byte Vac { get; set; }
    public ModInfo? Mod { get; set; }
    public TheShipInfo? TheShip { get; set; }
    public SpecTvInfo? SpecTv { get; set; }
    public ExtendedInfo? Ext { get; set; }

    /// <summary>
    /// Attempt to guess the game engine version.
    /// </summary>
    public GameEngine DetermineGameEngine()
    {
        if (InfoVersion == ValveConstants.S2A_INFO_GOLDSRC || Ext == null)
        {
            return GameEngine.Goldsrc;
        }

        if ((uint)Ext.AppId < 80)
        {
            return GameEngine.Goldsrc;
        }

        return GameEngine.Source;
    }

    /// <summary>
    /// Determines whether or not a Source server is pre-orangebox. This should
    /// not be called on non-Source servers.
    /// </summary>
    public bool IsPreOrangeBox()
    {
        return Ext == null || Protocol <= 7;
    }
}
