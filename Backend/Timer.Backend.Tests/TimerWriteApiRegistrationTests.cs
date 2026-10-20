using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Timer.Backend.Configuration;
using Timer.Backend.WriteApi;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class TimerWriteApiRegistrationTests
{
    [Fact]
    public void DisabledWriteApiDoesNotRegisterWriteFacadeOrMagicOnionServices()
    {
        var services = new ServiceCollection();
        var options = DisabledOptions();

        TimerWriteApiRegistration.Add(services, options);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(TimerBackendWriteStorage));
    }

    [Fact]
    public void EnabledWriteApiRegistersIndependentWriteFacade()
    {
        var services = new ServiceCollection();
        var options = EnabledOptions();

        TimerWriteApiRegistration.Add(services, options);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(TimerBackendWriteStorage));
    }

    [Fact]
    public async Task ServiceEndpointsAreMappedOnlyWhenTheWriteApiIsEnabled()
    {
        var disabled = await BuildAndMapAsync(DisabledOptions());
        try
        {
            Assert.Empty(((IEndpointRouteBuilder)disabled).DataSources.SelectMany(source => source.Endpoints));
        }
        finally
        {
            await disabled.DisposeAsync();
        }

        var enabled = await BuildAndMapAsync(EnabledOptions());
        try
        {
            var endpoints = ((IEndpointRouteBuilder)enabled).DataSources.SelectMany(source => source.Endpoints).ToArray();
            Assert.NotEmpty(endpoints);
            Assert.Contains(endpoints, endpoint => endpoint.DisplayName?.Contains(nameof(TimerWriteServiceV1), StringComparison.Ordinal) == true);
        }
        finally
        {
            await enabled.DisposeAsync();
        }
    }

    [Fact]
    public void EnabledWriteApiWithoutAPortGateRefusesToRegister()
    {
        // Without LocalPorts every HTTP/2 listener (e.g. a public TLS read port) would serve unauthenticated writes.
        var values = new Dictionary<string, string?>
        {
            ["TimerBackend:WriteApi:Enabled"] = "true",
            ["TimerBackend:WriteApi:StyleFactors:0"] = "1",
        };
        var gateless = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiRegistration.Add(new ServiceCollection(), gateless));

        values["TimerBackend:WriteApi:AllowAllListeners"] = "true";
        var optedIn = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        TimerWriteApiRegistration.Add(new ServiceCollection(), optedIn);
    }

    private static Task<WebApplication> BuildAndMapAsync(TimerWriteApiOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(options);
        TimerWriteApiRegistration.Add(builder.Services, options);
        var application = builder.Build();
        TimerWriteApiRegistration.Map(application, options);
        return Task.FromResult(application);
    }

    private static TimerWriteApiOptions EnabledOptions()
    {
        var values = new Dictionary<string, string?>
        {
            ["TimerBackend:WriteApi:Enabled"] = "true",
            ["TimerBackend:WriteApi:RulesetVersion"] = "7",
            ["TimerBackend:WriteApi:StyleFactors:0"] = "1",
            ["TimerBackend:WriteApi:LocalPorts:0"] = "5082",
        };
        return TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private static TimerWriteApiOptions DisabledOptions()
        => TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder()
                                                  .AddInMemoryCollection(new Dictionary<string, string?> { ["TimerBackend:WriteApi:Enabled"] = "false" })
                                                  .Build());
}
