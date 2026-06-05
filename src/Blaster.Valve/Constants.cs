// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

namespace Blaster.Valve;

public static class ValveConstants
{
    public const int MaxPacketSize = 1400;

    // OOB request packet types
    public const byte A2S_INFO = 0x54;
    public const byte A2S_RULES = 0x56;

    // Official versions of the A2S_INFO reply
    public const byte S2A_INFO_GOLDSRC = 0x6d;
    public const byte S2A_INFO_SOURCE = 0x49;

    // Other OOB response packet types
    public const byte S2C_CHALLENGE = 0x41;
    public const byte S2A_PLAYER = 0x44;
    public const byte S2A_RULES = 0x45;

    public const string MasterServer = "hl2master.steampowered.com:27011";
}
