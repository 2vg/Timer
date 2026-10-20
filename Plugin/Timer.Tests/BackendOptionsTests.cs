using Microsoft.Extensions.Configuration;
using Source2Surf.Timer.Configuration;
using Xunit;

namespace Timer.Tests;

public sealed class BackendOptionsTests
{
    [Fact]
    public void MissingEndpointMeansTheLocalBackend()
        => Assert.Equal(new Uri(BackendOptions.DefaultEndpoint),
                        BackendOptions.FromConfiguration(new ConfigurationBuilder().Build()).Endpoint);

    [Fact]
    public void LeftoverScoreWriteSectionStillWorks()
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["score_write:mode"]     = "remote-write",
                                ["score_write:endpoint"] = "http://timer.example:5082",
                            })
                            .Build();

        var options = BackendOptions.FromConfiguration(configuration);

        Assert.Equal(new Uri("http://timer.example:5082"), options.Endpoint);
        Assert.True(options.FromScoreWriteSection);
        Assert.False(BackendOptions.FromConfiguration(new ConfigurationBuilder().Build()).FromScoreWriteSection);
    }

    [Fact]
    public void EndpointAloneIsEnoughWithoutHttps()
    {
        var options = BackendOptions.FromConfiguration(Build(("endpoint", "http://timer.example:5082")));

        Assert.Equal(new Uri("http://timer.example:5082/"), options.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RpcDeadline);
    }

    [Fact]
    public void HttpsEndpointIsAcceptedWithoutCredentials()
    {
        var options = BackendOptions.FromConfiguration(Build(("endpoint", "https://timer.example")));

        Assert.Equal(Uri.UriSchemeHttps, options.Endpoint.Scheme);
    }

    [Theory]
    [InlineData("server_id")]
    [InlineData("mode")]
    [InlineData("enabled")]
    public void UnsupportedSettingsFailFast(string setting)
    {
        Assert.Throws<InvalidOperationException>(() =>
            BackendOptions.FromConfiguration(Build(("endpoint", "http://timer.example:5082"),
                                                   (setting, "obsolete"))));
    }

    [Fact]
    public void ApiKeyIsOptionalAndTrimmed()
    {
        Assert.Null(BackendOptions.FromConfiguration(Build(("endpoint", "https://timer.example"))).ApiKey);
        Assert.Equal("k3y-value", BackendOptions.FromConfiguration(Build(("api_key", "  k3y-value "))).ApiKey);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("non-ascii-é")]
    public void ApiKeyTheBackendWouldRefuseFailsFast(string key)
        => Assert.Throws<InvalidOperationException>(() => BackendOptions.FromConfiguration(Build(("api_key", key))));

    private static IConfiguration Build(params (string Name, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (name, value) in settings)
        {
            values[$"{BackendOptions.SectionName}:{name}"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
