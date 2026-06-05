// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

namespace Blaster.AmStats;

/// <summary>
/// Database models for AmStats statistics collection.
/// </summary>

public class Game
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public class GameMod
{
    public long Id { get; set; }
    public long GameId { get; set; }
    public ulong ValveGameId { get; set; }
    public long LastSeen { get; set; }
    public string ModString { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public int IsVerified { get; set; }
}

public class GameAddonVar
{
    public long VariableId { get; set; }
    public long AddonId { get; set; }
    public string Name { get; set; } = "";
}

public class GameVarValue
{
    public long Id { get; set; }
    public long VariableId { get; set; }
    public string Value { get; set; } = "";
    public long FirstKnown { get; set; }
}

public class BaseStat
{
    public long MaxPlayers { get; set; }
    public long TotalPlayers { get; set; }
    public long TotalBots { get; set; }
}

public class GameStat : BaseStat
{
    public long Id { get; set; }
    public long Stamp { get; set; }
    public long GameId { get; set; }
    public long AliveCount { get; set; }
    public long DeadCount { get; set; }
    public long LinuxServers { get; set; }
    public long WindowsServers { get; set; }
    public long ListenServers { get; set; }
}

public class GameObjectStat : BaseStat
{
    public long StatsId { get; set; }
    public long ObjectId { get; set; }
    public long ServerCount { get; set; }
}

public class GameAddonStat : GameObjectStat { }
public class GameValueStat : GameObjectStat { }

public class GameModStat : BaseStat
{
    public long StatsId { get; set; }
    public long ModId { get; set; }
    public long ServerType { get; set; }
    public long ServerCount { get; set; }
}

public class GameModObjectStat : GameModStat
{
    public long ObjectId { get; set; }
}

public class GameModAddonStat : GameModObjectStat { }
public class GameModValueStat : GameModObjectStat { }

public class Server
{
    public Blaster.Valve.ServerInfo? Info { get; set; }
    public Dictionary<string, string> Rules { get; set; } = new();

    public long DbType()
    {
        if (Info == null)
            return 0;

        return Info.Type switch
        {
            Blaster.Valve.ServerType.Listen => 3,
            Blaster.Valve.ServerType.Dedicated => Info.OS switch
            {
                Blaster.Valve.ServerOS.Windows => 2,
                Blaster.Valve.ServerOS.Linux => 1,
                Blaster.Valve.ServerOS.Mac => 4,
                _ => 0
            },
            _ => 0
        };
    }
}
