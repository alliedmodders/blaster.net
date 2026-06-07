// SPDX-License-Identifier: GPL-3.0-or-later
// Blaster (C) Copyright 2014 AlliedModders LLC

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Blaster.Valve;
using Xunit;

namespace Blaster.Tests;

/// <summary>
/// Unit tests for the pure helpers backing the master-server fan-out: address/string decoding,
/// filter construction, the filter-safety guard, and the query retry policy.
/// </summary>
public class MasterServerParsingTests
{
    [Theory]
    [InlineData(0x7F000001u, "127.0.0.1")]
    [InlineData(0xC0A80101u, "192.168.1.1")]
    [InlineData(0x08080808u, "8.8.8.8")]
    public void IPv4FromHostUInt_ByteSwapsToDottedQuad(uint hostOrder, string expected)
    {
        Assert.Equal(expected, GmsParse.IPv4FromHostUInt(hostOrder).ToString());
    }

    [Fact]
    public void ResolveString_UsesInlineValue_WhenNoIndex()
    {
        var table = new List<string> { "de_dust2", "de_inferno" };
        Assert.Equal("cs_office", GmsParse.ResolveString("cs_office", 0, hasIndex: false, table));
    }

    [Fact]
    public void ResolveString_UsesTable_WhenIndexPresent()
    {
        var table = new List<string> { "de_dust2", "de_inferno" };
        Assert.Equal("de_inferno", GmsParse.ResolveString("", 1, hasIndex: true, table));
    }

    [Fact]
    public void ResolveString_FallsBackToInline_WhenIndexOutOfRange()
    {
        var table = new List<string> { "de_dust2" };
        Assert.Equal("fallback", GmsParse.ResolveString("fallback", 9, hasIndex: true, table));
    }

    [Fact]
    public void BuildFilter_AppIdOnly_WhenNoConditions()
    {
        Assert.Equal("\\appid\\10", MasterServerQuerier.BuildFilter(10, [], []));
    }

    [Fact]
    public void BuildFilter_FramesNorCountAndAppendsAndConditions()
    {
        var filter = MasterServerQuerier.BuildFilter(10, ["\\gametype\\valve", "\\empty\\1"], ["\\linux\\1"]);
        Assert.Equal("\\appid\\10\\nor\\2\\gametype\\valve\\empty\\1\\linux\\1", filter);
    }

    [Theory]
    [InlineData("ROMANIA.CS1.RO", true)]
    [InlineData("[EU] Rush B", true)]
    [InlineData("weird\\name", false)]
    public void IsFilterSafe_RejectsNamesWithBackslash(string name, bool expected)
    {
        Assert.Equal(expected, MasterServerQuerier.IsFilterSafe(name));
    }

    [Fact]
    public async Task Retry_ReturnsResult_OnFirstSuccess_WithoutRetrying()
    {
        var calls = 0;
        var result = await MasterQueryRetry.RunAsync(
            () => { calls++; return Task.FromResult<IReadOnlyList<MasterServerRecord>>([]); },
            maxAttempts: 3, baseDelayMs: 1, logger: null, filter: "x");

        Assert.Empty(result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retry_RetriesTransientFailures_ThenSucceeds()
    {
        var calls = 0;
        IReadOnlyList<MasterServerRecord> success = [new MasterServerRecord { EndPoint = new System.Net.IPEndPoint(1, 1) }];

        var result = await MasterQueryRetry.RunAsync(
            () =>
            {
                calls++;
                if (calls < 3)
                {
                    throw new TimeoutException("transient");
                }
                return Task.FromResult(success);
            },
            maxAttempts: 3, baseDelayMs: 1, logger: null, filter: "x");

        Assert.Single(result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Retry_GivesUpAfterMaxAttempts_ReturningEmpty()
    {
        var calls = 0;
        var result = await MasterQueryRetry.RunAsync(
            () => { calls++; throw new TimeoutException("always"); },
            maxAttempts: 3, baseDelayMs: 1, logger: null, filter: "x");

        Assert.Empty(result);
        Assert.Equal(3, calls);
    }
}
