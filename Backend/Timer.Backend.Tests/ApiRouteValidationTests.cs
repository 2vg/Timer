using Timer.Backend.Endpoints;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class ApiRouteValidationTests
{
    [Fact]
    public void MapNameIsTrimmedAndCanonicalized()
    {
        var valid = ApiRouteValidation.TryNormalizeMapName("  SURF_Utopia  ", out var mapName, out _);

        Assert.True(valid);
        Assert.Equal("surf_utopia", mapName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("surf/test")]
    [InlineData("surf\\test")]
    [InlineData("surf\ntest")]
    [InlineData("surf_édge")]
    [InlineData("surf edge")]
    public void InvalidMapNameIsRejected(string value)
        => Assert.False(ApiRouteValidation.TryNormalizeMapName(value, out _, out _));

    [Theory]
    [InlineData(null, 5000)]
    [InlineData("1", 1)]
    [InlineData("5000", 5000)]
    public void RecordLimitAcceptsDocumentedRange(string? raw, int expected)
    {
        Assert.True(ApiRouteValidation.TryParseLimit(raw, out var limit, out _));
        Assert.Equal(expected, limit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5001")]
    [InlineData("+1")]
    [InlineData("1.0")]
    public void RecordLimitRejectsValuesOutsideDocumentedRange(string raw)
        => Assert.False(ApiRouteValidation.TryParseLimit(raw, out _, out _));

    [Theory]
    [InlineData("0")]
    [InlineData("18446744073709551616")]
    [InlineData("-1")]
    [InlineData(" 1 ")]
    public void SteamIdRejectsNonCanonicalOrZeroValues(string raw)
        => Assert.False(ApiRouteValidation.TryParseSteamId(raw, out _, out _));

    [Fact]
    public void SteamIdAcceptsMaximumUnsignedValue()
    {
        Assert.True(ApiRouteValidation.TryParseSteamId("18446744073709551615", out var value, out _));
        Assert.Equal(ulong.MaxValue, value);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("63", true)]
    [InlineData("64", false)]
    [InlineData("-1", false)]
    public void StyleUsesTimerBounds(string raw, bool expected)
        => Assert.Equal(expected, ApiRouteValidation.TryParseOptionalStyle(raw, out _, out _));

    [Theory]
    [InlineData("1", true)]
    [InlineData("63", true)]
    [InlineData("0", false)]
    [InlineData("64", false)]
    public void StageExcludesZeroAndMaximum(string raw, bool expected)
        => Assert.Equal(expected, ApiRouteValidation.TryParseOptionalStage(raw, out _, out _));

    [Theory]
    [InlineData(" surf_utopia")]
    [InlineData("surf_utopia\t")]
    public void RouteMapNameRejectsSurroundingWhitespace(string raw)
        => Assert.False(ApiRouteValidation.TryParseRouteMapName(raw, out _, out _));

    [Fact]
    public void RouteMapNameStillAcceptsLetterCase()
    {
        // Output-cache paths are case-insensitive, so letter case cannot multiply entries.
        Assert.True(ApiRouteValidation.TryParseRouteMapName("SURF_Utopia", out var mapName, out _));
        Assert.Equal("surf_utopia", mapName);
    }

    [Fact]
    public void NumbersWithLeadingZerosAreRejected()
    {
        Assert.False(ApiRouteValidation.TryParseLimit("010", out _, out _));
        Assert.False(ApiRouteValidation.TryParseOptionalStyle("00", out _, out _));
        Assert.False(ApiRouteValidation.TryParseOptionalStage("01", out _, out _));
        Assert.False(ApiRouteValidation.TryParseSteamId("076561198000000001", out _, out _));
        Assert.True(ApiRouteValidation.TryParseOptionalStyle("0", out _, out _));
    }

    [Fact]
    public void RunIdIsLimitedToTheStoredSignedRange()
    {
        Assert.True(ApiRouteValidation.TryParseRunId("9223372036854775807", out _, out _));
        Assert.False(ApiRouteValidation.TryParseRunId("9223372036854775808", out _, out _));
    }
}
