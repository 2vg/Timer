using System;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Timer.Backend.Administration;
using Timer.Backend.Configuration;
using Timer.Backend.Endpoints;
using Timer.Backend.Infrastructure;
using Timer.Backend.WriteApi;
using Timer.Backend.Storage;

var administrativeInvocation = BackendAdministrativeCli.Parse(args);
var builder = WebApplication.CreateBuilder(administrativeInvocation.ConfigurationArguments);
TimerBackendDefaults.Apply(builder.Configuration);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();

var backendOptions = TimerBackendOptions.FromConfiguration(builder.Configuration);

if (administrativeInvocation.Operation == BackendAdministrativeOperation.Migrate)
{
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
    using var storage = TimerBackendStorageFactory.Create(
        backendOptions.DatabaseType, backendOptions.ConnectionString, loggerFactory,
        enableOutboxWorker: false);
    storage.MigrateMasterDatabase();
    return;
}

if (administrativeInvocation.Operation == BackendAdministrativeOperation.ConvertRunDates)
{
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
    using var storage = TimerBackendStorageFactory.Create(
        backendOptions.DatabaseType, backendOptions.ConnectionString, loggerFactory,
        enableOutboxWorker: false);
    storage.ConvertMasterRunDates(administrativeInvocation.BackupConfirmed);
    return;
}

var writeApiOptions = TimerWriteApiOptions.FromConfiguration(builder.Configuration);
var runtimeOptions = TimerBackendRuntimeOptions.FromConfiguration(builder.Configuration);
if (administrativeInvocation.Operation is BackendAdministrativeOperation.SetTier
    or BackendAdministrativeOperation.RecalculateScores)
{
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
    using var storage = TimerBackendStorageFactory.Create(
        backendOptions.DatabaseType, backendOptions.ConnectionString, loggerFactory,
        enableOutboxWorker: false);
    storage.Start(initializeSchema: false, allowReadRepair: false, requireWriteSchema: true);

    // These commands write the factors onto every board: configured, or the ones game servers registered.
    if (!writeApiOptions.HasExplicitStyleFactors)
    {
        writeApiOptions.UseRegisteredStyleFactors(
            new TimerBackendGameStorage(storage).GetStyleFactorsAsync().GetAwaiter().GetResult());
    }
    var result = BackendAdministrativeCli.ExecuteAsync(administrativeInvocation, storage, writeApiOptions)
        .GetAwaiter().GetResult();
    if (!result.MapFound)
        throw new InvalidOperationException($"Map '{administrativeInvocation.MapName}' was not found.");
    Console.WriteLine(BackendAdministrativeCli.FormatCompletion(administrativeInvocation, result));
    if (result.FailedMaps.Count > 0)
    {
        // Other maps were requeued, but make the partial failure visible to scripts.
        Environment.ExitCode = 1;
    }

    return;
}

builder.Services.AddSingleton(backendOptions);
builder.Services.AddSingleton(writeApiOptions);
TimerBackendRuntimeRegistration.Add(builder.Services, runtimeOptions);
builder.Services.AddSingleton<TimerBackendStorage>(services =>
    TimerBackendStorageFactory.Create(
        backendOptions.DatabaseType,
        backendOptions.ConnectionString,
        services.GetRequiredService<ILoggerFactory>(),
        backendOptions.ShouldRunOutboxWorker(writeApiOptions.Enabled)));
builder.Services.AddHostedService<TimerBackendStorageHostedService>();
builder.Services.AddResponseCompression();
builder.Services.ConfigureHttpJsonOptions(BackendJsonOptions.Configure);
TimerWriteApiRegistration.Add(builder.Services, writeApiOptions);

// Slow readers may not hold a multi-MiB leaderboard (and its buffers) open for minutes: 4 KiB/s after a 5 s grace.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MinResponseDataRate = new MinDataRate(bytesPerSecond: 4096, gracePeriod: TimeSpan.FromSeconds(5)));

var app = builder.Build();
app.Logger.LogInformation("Timer.Backend {Version}",
                          typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
TimerWriteApiRegistration.LogExposureWarnings(app.Logger, app.Configuration, writeApiOptions);
DatabaseTransportCheck.LogIfUnverified(app.Logger, backendOptions.ConnectionString);

TimerBackendRuntimeRegistration.UseForwardedHeadersIfConfigured(app, runtimeOptions);
app.UseExceptionHandler(exceptionApplication => exceptionApplication.Run(BackendExceptionHandling.WriteErrorAsync));
app.UseRouting();
app.UseRequestTimeouts();
app.UseOutputCache();
// Inside the output cache: entries hold the compressed body (keyed by encoding), so a hit is never recompressed.
app.UseResponseCompression();
// After the cache, so hits never take a permit; only reads that reach the database are limited.
app.UseRateLimiter();
TimerReadEndpoints.Map(app);
TimerWriteApiRegistration.Map(app, writeApiOptions);
app.Run();

public partial class Program;
