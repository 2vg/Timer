using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Timer.Backend.Configuration;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class TimerWriteApiOptionsTests
{
    [Fact]
    public void MissingSectionDefaultsToEnabledWithStyleZero()
    {
        var options = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.True(options.Enabled);
        Assert.Equal(1, options.RulesetVersion);
        Assert.Equal(1d, options.StyleFactors[0]);
        Assert.False(options.HasScorePolicy);
    }

    [Fact]
    public void ReadOnlyReplicasTurnTheApiOff()
    {
        var options = TimerWriteApiOptions.FromConfiguration(Build(new()
        {
            ["TimerBackend:WriteApi:Enabled"] = "false",
        }));
        Assert.False(options.Enabled);
        Assert.Equal(0, options.RulesetVersion);
    }

    [Fact]
    public void RegisteredFactorsReplaceTheImplicitDefault()
    {
        var options = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.False(options.UseRegisteredStyleFactors(new Dictionary<int, double> { [1] = 0.5 }));
        Assert.True(options.UseRegisteredStyleFactors(new Dictionary<int, double> { [0] = 1, [1] = 0.5 }));

        Assert.True(options.HasRegisteredStyleFactors);
        Assert.True(options.HasScorePolicy);
        Assert.Equal(0.5, options.StyleFactors[1]);
    }

    [Fact]
    public void ConfiguredFactorsWinOverRegisteredOnes()
    {
        var options = TimerWriteApiOptions.FromConfiguration(Build(new()
        {
            ["TimerBackend:WriteApi:StyleFactors:0"] = "1",
            ["TimerBackend:WriteApi:StyleFactors:1"] = "2",
        }));

        Assert.False(options.UseRegisteredStyleFactors(new Dictionary<int, double> { [0] = 1, [1] = 0.5 }));
        Assert.Equal(2d, options.StyleFactors[1]);
    }

    [Fact]
    public void EnabledApiDefaultsToRulesetOneAndMainStyleFactorOne()
    {
        var options = TimerWriteApiOptions.FromConfiguration(Build(new()
        {
            ["TimerBackend:WriteApi:Enabled"] = "true",
        }));
        Assert.True(options.Enabled);
        Assert.Equal(1, options.RulesetVersion);
        Assert.Equal(1d, options.StyleFactors[0]);
    }

    [Fact]
    public void ValidConfigurationParsesStyleFactors()
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:StyleFactors:15"] = "100";
        var options = TimerWriteApiOptions.FromConfiguration(Build(values));
        Assert.True(options.Enabled);
        Assert.Equal(7, options.RulesetVersion);
        Assert.Equal(1d, options.StyleFactors[0]);
        Assert.Equal(100d, options.StyleFactors[15]);
    }

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("Servers:alpha:ApiKeySha256")]
    public void ObsoleteIdentityConfigurationIsRejected(string setting)
    {
        var values = EnabledValues();
        values[$"TimerBackend:WriteApi:{setting}"] = "obsolete";
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void UppercaseEnvironmentStyleSettingNamesRemainAccepted()
    {
        var options = TimerWriteApiOptions.FromConfiguration(Build(new()
        {
            ["TIMERBACKEND:WRITEAPI:ENABLED"] = "true",
            ["TIMERBACKEND:WRITEAPI:RULESETVERSION"] = "7",
            ["TIMERBACKEND:WRITEAPI:STYLEFACTORS:0"] = "1",
        }));
        Assert.True(options.Enabled);
        Assert.Equal(1d, options.StyleFactors[0]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("64")]
    public void StyleOutsideRangeFailsFast(string style)
    {
        var values = EnabledValues();
        values[$"TimerBackend:WriteApi:StyleFactors:{style}"] = "1";
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("100.1")]
    [InlineData("-0.1")]
    public void NonFiniteOrOutOfRangeStyleFactorFailsFast(string factor)
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:StyleFactors:0"] = factor;
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void ExplicitNonPositiveRulesetStillFailsWhenEnabled()
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:RulesetVersion"] = "0";
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void EnabledApiRequiresStyleZeroWhenCustomFactorsAreConfigured()
    {
        var values = EnabledValues();
        values.Remove("TimerBackend:WriteApi:StyleFactors:0");
        values["TimerBackend:WriteApi:StyleFactors:1"] = "1";
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void ImplicitStyleZeroDefaultIsNotReportedAsExplicit()
    {
        var implicitOptions = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var explicitOptions = TimerWriteApiOptions.FromConfiguration(Build(EnabledValues()));

        Assert.False(implicitOptions.HasExplicitStyleFactors);
        Assert.True(explicitOptions.HasExplicitStyleFactors);
    }

    [Fact]
    public void LocalPortsAcceptArraysAndScalars()
    {
        var arrayValues = EnabledValues();
        arrayValues["TimerBackend:WriteApi:LocalPorts:0"] = "5082";
        arrayValues["TimerBackend:WriteApi:LocalPorts:1"] = "5083";
        var scalarValues = EnabledValues();
        scalarValues["TimerBackend:WriteApi:LocalPorts"] = "5082";

        Assert.Equal(new[] { 5082, 5083 }, TimerWriteApiOptions.FromConfiguration(Build(arrayValues)).LocalPorts.Order());
        Assert.Equal(new[] { 5082 }, TimerWriteApiOptions.FromConfiguration(Build(scalarValues)).LocalPorts);
    }

    [Fact]
    public void LocalPortsSetAsBothScalarAndListFailsFast()
    {
        // An env scalar layered over an appsettings array would otherwise merge into both ports.
        var values = EnabledValues();
        values["TimerBackend:WriteApi:LocalPorts:0"] = "5082";
        values["TimerBackend:WriteApi:LocalPorts"] = "6000";

        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("grpc")]
    public void InvalidLocalPortFailsFast(string port)
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:LocalPorts:0"] = port;
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Theory]
    [InlineData("Mode", "Basic")]                                   // unknown mode
    [InlineData("Mode", "ApiKey")]                                  // ApiKey without clients
    [InlineData("Clients:0:Name", "eu1")]                           // clients while Mode is None
    public void InvalidAuthenticationFailsFast(string key, string value)
    {
        var values = EnabledValues();
        values[$"TimerBackend:WriteApi:Authentication:{key}"] = value;
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Theory]
    [InlineData("abc", "Game")]                                     // not a 64-hex SHA-256
    [InlineData(KeyHash, "Owner")]                                  // unknown scope
    [InlineData(KeyHash, null)]                                     // no scope
    public void InvalidApiKeyClientFailsFast(string hash, string? scope)
    {
        var values = ApiKeyValues(hash, scope);
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void DuplicateApiKeysFailFast()
    {
        var values = ApiKeyValues(KeyHash, "Game");
        values["TimerBackend:WriteApi:Authentication:Clients:1:Name"] = "eu2";
        values["TimerBackend:WriteApi:Authentication:Clients:1:KeySha256"] = KeyHash.ToLowerInvariant();
        values["TimerBackend:WriteApi:Authentication:Clients:1:Scopes:0"] = "Admin";
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void ApiKeyClientsResolveByTheKeysHash()
    {
        var options = TimerWriteApiOptions.FromConfiguration(Build(ApiKeyValues(KeyHash, "Game")));

        Assert.Equal(Timer.Backend.WriteApi.TimerWriteAuthenticationMode.ApiKey, options.AuthenticationMode);
        Assert.True(Timer.Backend.WriteApi.TimerWriteKeyHash.TryCompute(Key, out var hash));
        Assert.Equal(Timer.Backend.WriteApi.TimerWriteScopes.Game, options.Clients[hash].Scopes);
        Assert.True(Timer.Backend.WriteApi.TimerWriteKeyHash.TryCompute("other-key", out var other));
        Assert.False(options.Clients.ContainsKey(other));
    }

    [Theory]
    [InlineData("10.0.0.0/33")]
    [InlineData("not-an-ip")]
    public void InvalidAllowedNetworkFailsFast(string network)
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:AllowedNetworks:0"] = network;
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Theory]
    [InlineData("ftp://replays.example.com/")]
    [InlineData("https://user:pass@replays.example.com/")]
    [InlineData("/relative")]
    public void InvalidReplayUrlPrefixFailsFast(string prefix)
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:ReplayUrlPrefixes:0"] = prefix;
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void ReplayUrlPrefixesEndWithASlash()
    {
        // Without it "https://store/replays" would also admit "https://store/replays-evil/...".
        var values = EnabledValues();
        values["TimerBackend:WriteApi:ReplayUrlPrefixes:0"] = "https://replays.example.com/replays";
        Assert.Equal(["https://replays.example.com/replays/"],
                     TimerWriteApiOptions.FromConfiguration(Build(values)).ReplayUrlPrefixes);
    }

    internal const string Key = "test-key-3f9b6c";
    // SHA-256 of Key (printf %s test-key-3f9b6c | sha256sum), upper-case to prove the hex is case-insensitive.
    internal const string KeyHash = "2985812DBFFDFEA793A158FE4BB108262A357A743E559F8EA7A6F4EE9E3FC0F4";

    private static Dictionary<string, string?> ApiKeyValues(string hash, string? scope)
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:Authentication:Mode"] = "ApiKey";
        values["TimerBackend:WriteApi:Authentication:Clients:0:Name"] = "eu1";
        values["TimerBackend:WriteApi:Authentication:Clients:0:KeySha256"] = hash;
        if (scope is not null)
        {
            values["TimerBackend:WriteApi:Authentication:Clients:0:Scopes:0"] = scope;
        }

        return values;
    }

    private static Dictionary<string, string?> EnabledValues() => new()
    {
        ["TimerBackend:WriteApi:Enabled"] = "true",
        ["TimerBackend:WriteApi:RulesetVersion"] = "7",
        ["TimerBackend:WriteApi:StyleFactors:0"] = "1",
    };

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
