using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using MagicOnion.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Timer.Backend.Configuration;
using Timer.Backend.WriteApi;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class TimerWriteLoopbackTests
{
    [WindowsTlsFact]
    public async Task DirectKestrelTlsHttp2LoopbackReachesServiceValidationWithoutCredentials()
    {
        using var certificate = CreateSelfSignedServerCertificate();
        await using var server = await LoopbackServer.StartAsync(certificate: certificate);
        using var channel = CreateTrustedTlsChannel(server.Address, certificate);
        var client = MagicOnionClient.Create<ITimerWriteServiceV1>(channel);

        var validation = await Assert.ThrowsAsync<RpcException>(() => CallStatusAsync(client));
        Assert.Equal(StatusCode.InvalidArgument, validation.StatusCode);

        Assert.Equal(Uri.UriSchemeHttps, server.Address.Scheme);
        Assert.Equal("https", await server.RequestScheme.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("HTTP/2", await server.RequestProtocol.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [DisposableTlsDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task DirectKestrelTlsHttp2PostgreSqlWriteRoundTripSurvivesDiscardedReplyRetry()
        => DirectKestrelTlsWriteRoundTripAsync(
            "postgresql",
            Environment.GetEnvironmentVariable("TIMER_TEST_POSTGRES")!);

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task KestrelHttp2PostgreSqlWriteRoundTripSurvivesDiscardedReplyRetry()
        => KestrelHttp2WriteRoundTripAsync(
            "postgresql",
            Environment.GetEnvironmentVariable("TIMER_TEST_POSTGRES")!,
            useTls: false);

    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task KestrelHttp2MySqlWriteRoundTripSurvivesDiscardedReplyRetry()
        => KestrelHttp2WriteRoundTripAsync(
            "mysql",
            Environment.GetEnvironmentVariable("TIMER_TEST_MYSQL")!,
            useTls: false);
    [DisposableTlsDatabaseFact("TIMER_TEST_MYSQL")]
    public Task DirectKestrelTlsHttp2MySqlWriteRoundTripSurvivesDiscardedReplyRetry()
        => DirectKestrelTlsWriteRoundTripAsync(
            "mysql",
            Environment.GetEnvironmentVariable("TIMER_TEST_MYSQL")!);

    [Fact]
    public async Task MagicOnionLoopbackUsesHttp2AndValidatesRequestsWithoutCredentials()
    {
        await using var server = await LoopbackServer.StartAsync();
        using var channel = GrpcChannel.ForAddress(server.Address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
            },
        });

        var client = MagicOnionClient.Create<ITimerWriteServiceV1>(channel);
        var statusError = await Assert.ThrowsAsync<RpcException>(() => CallStatusAsync(client));
        Assert.Equal(StatusCode.InvalidArgument, statusError.StatusCode);
        var profileError = await Assert.ThrowsAsync<RpcException>(() => CallEnsurePlayerProfileAsync(client));
        Assert.Equal(StatusCode.InvalidArgument, profileError.StatusCode);
        Assert.Equal("HTTP/2", await server.RequestProtocol.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(new byte[] { 0xC1, 0xFF, 0x00, 0x13 })]   // reserved MessagePack code
    [InlineData(new byte[] { 0x05 })]                     // fixint where an array is expected
    [InlineData(new byte[] { 0xDD, 0x80, 0x00, 0x00, 0x00 })] // array header claiming 2^31 elements
    public async Task MalformedMessagePackIsReportedAsInvalidArgument(byte[] payload)
    {
        await using var server = await LoopbackServer.StartAsync();
        using var channel = GrpcChannel.ForAddress(server.Address);
        var raw = new Marshaller<byte[]>(static bytes => bytes, static bytes => bytes);
        var method = new Method<byte[], byte[]>(MethodType.Unary, nameof(ITimerWriteServiceV1), "SubmitRunAsync", raw, raw);

        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await channel.CreateCallInvoker().AsyncUnaryCall(method, null, new CallOptions(), payload));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public async Task WriteApiIsServedOnlyOnConfiguredLocalPorts()
    {
        var writePort = GetFreeLoopbackPort();
        var readPort = GetFreeLoopbackPort();
        var options = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["TimerBackend:WriteApi:Enabled"] = "true",
                ["TimerBackend:WriteApi:StyleFactors:0"] = "1",
                ["TimerBackend:WriteApi:LocalPorts:0"] = writePort.ToString(CultureInfo.InvariantCulture),
            }).Build());
        using var loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.None));
        using var storage = TimerBackendStorageFactory.Create(
            "postgresql",
            "Host=127.0.0.1;Port=1;Database=timer;Username=timer;Password=timer",
            loggerFactory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TimerWriteServiceV1).Assembly.GetName().Name,
            EnvironmentName = "Testing",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            // Both listeners speak HTTP/2, as a TLS read port with default protocols would.
            serverOptions.Listen(IPAddress.Loopback, writePort, listen => listen.Protocols = HttpProtocols.Http2);
            serverOptions.Listen(IPAddress.Loopback, readPort, listen => listen.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(storage);
        TimerWriteApiRegistration.Add(builder.Services, options);
        await using var application = builder.Build();
        TimerWriteApiRegistration.Map(application, options);
        await application.StartAsync();

        try
        {
            using var writeChannel = GrpcChannel.ForAddress($"http://127.0.0.1:{writePort}");
            var allowed = await Assert.ThrowsAsync<RpcException>(() =>
                CallStatusAsync(MagicOnionClient.Create<ITimerWriteServiceV1>(writeChannel)));
            Assert.Equal(StatusCode.InvalidArgument, allowed.StatusCode);

            using var readChannel = GrpcChannel.ForAddress($"http://127.0.0.1:{readPort}");
            var rejected = await Assert.ThrowsAsync<RpcException>(() =>
                CallStatusAsync(MagicOnionClient.Create<ITimerWriteServiceV1>(readChannel)));
            Assert.Equal(StatusCode.Unimplemented, rejected.StatusCode);
        }
        finally
        {
            await application.StopAsync();
        }
    }

    [Fact]
    public async Task MagicOnionLoopbackRejectsPayloadAboveConfiguredReceiveLimit()
    {
        await using var server = await LoopbackServer.StartAsync();
        using var channel = GrpcChannel.ForAddress(server.Address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler(),
        });
        var client = MagicOnionClient.Create<ITimerWriteServiceV1>(channel);

        var request = new SubmitRunRequest
        {
            MapName = new string('m', TimerWriteApiRegistration.MaxMessageSizeBytes + 1024),
        };

        var exception = await Assert.ThrowsAsync<RpcException>(() =>
            CallSubmitAsync(client, request));

        Assert.Equal(StatusCode.ResourceExhausted, exception.StatusCode);
    }

    [Fact]
    public async Task ApiKeyModeAuthenticatesEveryCallAndEnforcesScopes()
    {
        const string gameKey = "game-key-for-loopback";
        const string adminKey = "admin-key-for-loopback";
        await using var server = await LoopbackServer.StartAsync(settings: new()
        {
            ["TimerBackend:WriteApi:Authentication:Mode"] = "ApiKey",
            ["TimerBackend:WriteApi:Authentication:Clients:0:Name"] = "game",
            ["TimerBackend:WriteApi:Authentication:Clients:0:KeySha256"] = Sha256Hex(gameKey),
            ["TimerBackend:WriteApi:Authentication:Clients:0:Scopes:0"] = "Game",
            ["TimerBackend:WriteApi:Authentication:Clients:1:Name"] = "admin",
            ["TimerBackend:WriteApi:Authentication:Clients:1:KeySha256"] = Sha256Hex(adminKey),
            ["TimerBackend:WriteApi:Authentication:Clients:1:Scopes:0"] = "Game",
            ["TimerBackend:WriteApi:Authentication:Clients:1:Scopes:1"] = "Admin",
        });
        using var channel = GrpcChannel.ForAddress(server.Address);
        var write = MagicOnionClient.Create<ITimerWriteServiceV1>(channel);
        var storage = MagicOnionClient.Create<ITimerStorageServiceV1>(channel);

        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(() => CallStatusAsync(write))).StatusCode);
        Assert.Equal(StatusCode.Unauthenticated,
                     (await Assert.ThrowsAsync<RpcException>(() => CallStatusAsync(write.WithHeaders(Bearer("wrong-key"))))).StatusCode);
        Assert.Equal(StatusCode.Unauthenticated,
                     (await Assert.ThrowsAsync<RpcException>(() => CallStatusAsync(write.WithHeaders(new Metadata { { "authorization", gameKey } })))).StatusCode);

        // A valid Game key reaches request validation; an Admin-only method stays closed to it.
        Assert.Equal(StatusCode.InvalidArgument,
                     (await Assert.ThrowsAsync<RpcException>(() => CallStatusAsync(write.WithHeaders(Bearer(gameKey))))).StatusCode);
        Assert.Equal(StatusCode.PermissionDenied,
                     (await Assert.ThrowsAsync<RpcException>(async () =>
                         await storage.WithHeaders(Bearer(gameKey)).WipePlayerRunsAsync(76561198000000001, dryRun: true))).StatusCode);

        // The Admin key passes the gate; the unreachable test database then reports Unavailable.
        var admin = await Assert.ThrowsAsync<RpcException>(async () =>
            await storage.WithHeaders(Bearer(adminKey)).WipePlayerRunsAsync(76561198000000001, dryRun: true));
        Assert.NotEqual(StatusCode.PermissionDenied, admin.StatusCode);
        Assert.NotEqual(StatusCode.Unauthenticated, admin.StatusCode);
    }

    [Theory]
    [InlineData("10.0.0.0/8", StatusCode.PermissionDenied)]
    [InlineData("127.0.0.1", StatusCode.InvalidArgument)]
    public async Task AllowedNetworksGateThePeerAddress(string network, StatusCode expected)
    {
        await using var server = await LoopbackServer.StartAsync(settings: new()
        {
            ["TimerBackend:WriteApi:AllowedNetworks:0"] = network,
        });
        using var channel = GrpcChannel.ForAddress(server.Address);

        var error = await Assert.ThrowsAsync<RpcException>(() =>
            CallStatusAsync(MagicOnionClient.Create<ITimerWriteServiceV1>(channel)));
        Assert.Equal(expected, error.StatusCode);
    }

    [Fact]
    public void EveryWriteRpcHasAnExplicitScope()
    {
        // A method missing from the map falls back to Admin; this pins that every contract method was decided.
        string[] admin =
        [
            "SetMapTierAsync", "SetMapRankedAsync", "RecalculateScoresAsync", "RemoveMapRecordsAsync",
            "DeleteRunAsync", "WipePlayerRunsAsync", "SaveZonesAsync",
        ];
        foreach (var service in new[] { typeof(ITimerWriteServiceV1), typeof(ITimerStorageServiceV1) })
        {
            foreach (var method in service.GetMethods())
            {
                Assert.True(TimerWriteMethodScopes.ByMethodPath.TryGetValue($"/{service.Name}/{method.Name}", out var scope),
                            method.Name);
                Assert.Equal(admin.Contains(method.Name) ? TimerWriteScopes.Admin : TimerWriteScopes.Game, scope);
            }
        }

        Assert.Equal(TimerWriteScopes.Admin, TimerWriteMethodScopes.Required("/ITimerStorageServiceV1/NotYetDecidedAsync"));
    }

    private static Metadata Bearer(string key) => new () { { "authorization", $"Bearer {key}" } };

    private static string Sha256Hex(string key) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(key)));

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task CallStatusAsync(ITimerWriteServiceV1 client)
    {
        await client.GetSubmissionStatusAsync(new GetSubmissionStatusRequest
        {
            SubmissionId = Guid.Empty,
        });
    }

    private static async Task CallSubmitAsync(ITimerWriteServiceV1 client, SubmitRunRequest request)
    {
        await client.SubmitRunAsync(request);
    }

    private static async Task CallEnsurePlayerProfileAsync(ITimerWriteServiceV1 client)
    {
        await client.EnsurePlayerProfileAsync(new EnsurePlayerProfileRequest
        {
            SteamId = 0,
            Name = string.Empty,
        });
    }

    private static async Task DirectKestrelTlsWriteRoundTripAsync(string databaseType,
                                                                    string connectionString)
        => await KestrelHttp2WriteRoundTripAsync(databaseType, connectionString, useTls: true);

    private static async Task KestrelHttp2WriteRoundTripAsync(string databaseType,
                                                               string connectionString,
                                                               bool useTls)
    {
        // These tests are opt-in because Start(initializeSchema: true) intentionally verifies
        // the production schema migration path and creates isolated test data.
        using var loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.None));
        using var storage = TimerBackendStorageFactory.Create(databaseType, connectionString, loggerFactory);
        storage.Start(initializeSchema: true, allowReadRepair: false);

        var mapName = $"surf_tls_{Guid.NewGuid():N}";
        await ProvisionMapAsync(databaseType, connectionString, mapName);

        using var certificate = useTls ? CreateSelfSignedServerCertificate() : null;
        await using var server = await LoopbackServer.StartAsync(certificate: certificate,
                                                                 storage: storage);
        var submissionId = Guid.NewGuid();
        var steamId = CreateUniqueSteamId();
        var request = CreateSubmissionRequest(mapName, steamId, submissionId);

        using (var firstChannel = CreateLoopbackChannel(server.Address, certificate))
        {
            var client = MagicOnionClient.Create<ITimerWriteServiceV1>(firstChannel);

            var profile = await client.EnsurePlayerProfileAsync(new EnsurePlayerProfileRequest
            {
                SteamId = steamId,
                Name = "TLS gRPC integration player",
            });
            Assert.True(profile.PlayerId > 0);
            Assert.Equal(steamId, profile.SteamId);
            Assert.Equal("TLS gRPC integration player", profile.Name);

            foreach (var finishedAt in new[]
                {
                    new DateTime(999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc),
                    new DateTime(9999, 12, 31, 23, 59, 59, 500, DateTimeKind.Utc),
                    new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc),
                })
            {
                var invalid = CreateSubmissionRequest(mapName, steamId, Guid.NewGuid());
                invalid.FinishedAtUnixTimeMilliseconds = new DateTimeOffset(finishedAt).ToUnixTimeMilliseconds();
                var error = await Assert.ThrowsAsync<RpcException>(() => CallSubmitAsync(client, invalid));
                Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
                Assert.False((await client.GetSubmissionStatusAsync(new GetSubmissionStatusRequest
                {
                    SubmissionId = invalid.SubmissionId,
                })).Found);
            }

            // Start the first write but intentionally never observe its reply. A separate client
            // confirms that the server committed before we close the submitting channel; retry
            // must then recover using only the stable SubmissionId.
            var discardedReply = client.SubmitRunAsync(request);
            using (var observerChannel = CreateLoopbackChannel(server.Address, certificate))
            {
                var observer = MagicOnionClient.Create<ITimerWriteServiceV1>(observerChannel);
                var committed = await WaitForCommittedSubmissionAsync(observer, submissionId);
                Assert.True(committed.Found);
                Assert.NotNull(committed.Submission);
            }

            GC.KeepAlive(discardedReply);
        }

        using var retryChannel = CreateLoopbackChannel(server.Address, certificate);
        var retriedClient = MagicOnionClient.Create<ITimerWriteServiceV1>(retryChannel);
        var retried = await retriedClient.SubmitRunAsync(request);

        Assert.Equal(SubmissionDisposition.AlreadyApplied, retried.Disposition);
        Assert.Equal(submissionId, retried.SubmissionId);
        Assert.True(retried.RunId > 0);

        var status = await retriedClient.GetSubmissionStatusAsync(new GetSubmissionStatusRequest
        {
            SubmissionId = submissionId,
        });
        Assert.True(status.Found);
        Assert.NotNull(status.Submission);
        Assert.Equal(SubmissionDisposition.AlreadyApplied, status.Submission!.Disposition);
        Assert.Equal(retried.RunId, status.Submission.RunId);
        var expectedScheme = useTls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        Assert.Equal(expectedScheme, server.Address.Scheme);
        Assert.Equal(expectedScheme, await server.RequestScheme.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("HTTP/2", await server.RequestProtocol.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<GetSubmissionStatusResponse> WaitForCommittedSubmissionAsync(
        ITimerWriteServiceV1 client,
        Guid submissionId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true)
        {
            var status = await client.GetSubmissionStatusAsync(new GetSubmissionStatusRequest
            {
                SubmissionId = submissionId,
            });
            if (status.Found)
            {
                return status;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Submission {submissionId} was not committed before the deadline.");
            }

            await Task.Delay(20);
        }
    }

    private static GrpcChannel CreateLoopbackChannel(Uri address, X509Certificate2? certificate)
        => certificate is null
            ? GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                },
            })
            : CreateTrustedTlsChannel(address, certificate);

    private static GrpcChannel CreateTrustedTlsChannel(Uri address, X509Certificate2 certificate)
    {
        var expectedThumbprint = certificate.Thumbprint;
        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new HttpClientHandler
            {
                UseProxy = false,
                // Test-only trust: accept exactly the ephemeral certificate created by this
                // fixture, rather than globally disabling certificate validation.
                ServerCertificateCustomValidationCallback = (_, remoteCertificate, _, _) =>
                    remoteCertificate is not null
                    && string.Equals(remoteCertificate.GetCertHashString(),
                                     expectedThumbprint,
                                     StringComparison.OrdinalIgnoreCase),
            },
        });
    }

    private static X509Certificate2 CreateSelfSignedServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost",
                                             key,
                                             HashAlgorithmName.SHA256,
                                             RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
            critical: false));
        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName("localhost");
        subjectAlternativeName.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeName.Build());

        using var generatedCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
                                                                   DateTimeOffset.UtcNow.AddMinutes(10));
        return X509CertificateLoader.LoadPkcs12(generatedCertificate.Export(X509ContentType.Pkcs12),
                                                 string.Empty,
                                                 X509KeyStorageFlags.DefaultKeySet);
    }

    private static async Task ProvisionMapAsync(string databaseType,
                                                string connectionString,
                                                string mapName)
    {
        await using DbConnection connection = databaseType switch
        {
            "postgresql" => new NpgsqlConnection(connectionString),
            "mysql" => new MySqlConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(databaseType)),
        };
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = databaseType switch
        {
            "postgresql" =>
                "INSERT INTO surf_maps (file, tier, stages, basepot, bonuses, playcount, totalplaytime) "
                + "VALUES (@mapName, 1, 0, 0, 0, 0, 0)",
            "mysql" =>
                "INSERT INTO surf_maps (`file`, `tier`, `stages`, `basepot`, `bonuses`, `playcount`, `totalplaytime`) "
                + "VALUES (@mapName, 1, 0, 0, 0, 0, 0)",
            _ => throw new ArgumentOutOfRangeException(nameof(databaseType)),
        };
        var mapParameter = command.CreateParameter();
        mapParameter.ParameterName = "@mapName";
        mapParameter.Value = mapName;
        command.Parameters.Add(mapParameter);

        // The RPC deliberately never provisions maps. This isolated row is created only after
        // TimerBackendStorage.Start(initializeSchema: true) has verified the migration path.
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static long CreateUniqueSteamId()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return 7_656_119_800_000_000L + BitConverter.ToUInt32(bytes, 0);
    }

    private static SubmitRunRequest CreateSubmissionRequest(string mapName, long steamId, Guid submissionId)
        => new ()
        {
            SubmissionId = submissionId,
            SteamId = steamId,
            MapName = mapName,
            RunKind = RunKind.Main,
            Style = 0,
            Track = 0,
            Stage = 0,
            TimeMicros = 80_000_000,
            Jumps = 7,
            Strafes = 13,
            Sync = 98.5f,
            Motion = new MotionDto { StartX = 1, AverageY = 2, EndZ = 3 },
            Checkpoints =
            [
                new CheckpointDto
                {
                    Index = 1,
                    TimeMicros = 40_000_000,
                    Sync = 97,
                    Motion = new MotionDto { MaxX = 3 },
                },
            ],
            FinishedAtUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ContractVersion = TimerBackendRunSubmissionCommand.CurrentContractVersion,
            RulesetVersion = 7,
        };

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly ILoggerFactory _loggerFactory;
        private readonly TimerBackendStorage? _ownedStorage;

        private LoopbackServer(WebApplication application,
                               ILoggerFactory loggerFactory,
                               TimerBackendStorage? ownedStorage,
                               Uri address,
                               Task<string> requestProtocol,
                               Task<string> requestScheme)
        {
            _application = application;
            _loggerFactory = loggerFactory;
            _ownedStorage = ownedStorage;
            Address = address;
            RequestProtocol = requestProtocol;
            RequestScheme = requestScheme;
        }

        public Uri Address { get; }

        public Task<string> RequestProtocol { get; }

        public Task<string> RequestScheme { get; }

        public static async Task<LoopbackServer> StartAsync(X509Certificate2? certificate = null,
                                                             TimerBackendStorage? storage = null,
                                                             Dictionary<string, string?>? settings = null)
        {
            var options = CreateOptions(settings);
            var loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.None));
            var ownsStorage = storage is null;
            storage ??= TimerBackendStorageFactory.Create(
                "postgresql",
                "Host=127.0.0.1;Port=1;Database=timer;Username=timer;Password=timer",
                loggerFactory);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(TimerWriteServiceV1).Assembly.GetName().Name,
                EnvironmentName = "Testing",
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.WebHost.ConfigureKestrel(serverOptions =>
                serverOptions.Listen(IPAddress.Loopback,
                                     0,
                                     listenOptions =>
                                     {
                                         listenOptions.Protocols = HttpProtocols.Http2;
                                         if (certificate is not null)
                                         {
                                             listenOptions.UseHttps(certificate);
                                         }
                                     }));
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(storage);
            TimerWriteApiRegistration.Add(builder.Services, options);

            var application = builder.Build();
            var requestProtocol = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var requestScheme = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            application.Use(async (context, next) =>
            {
                requestProtocol.TrySetResult(context.Request.Protocol);
                requestScheme.TrySetResult(context.Request.Scheme);
                await next(context);
            });
            TimerWriteApiRegistration.Map(application, options);

            try
            {
                await application.StartAsync();

                var addresses = application.Services.GetRequiredService<IServer>()
                                           .Features.Get<IServerAddressesFeature>()?.Addresses;
                var boundAddress = addresses?.Single()
                    ?? throw new InvalidOperationException("Loopback HTTP/2 endpoint has no bound address.");

                return new LoopbackServer(application,
                                          loggerFactory,
                                          ownsStorage ? storage : null,
                                          new Uri(boundAddress),
                                          requestProtocol.Task,
                                          requestScheme.Task);
            }
            catch
            {
                await application.DisposeAsync();
                if (ownsStorage)
                {
                    storage.Dispose();
                }
                loggerFactory.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
            _ownedStorage?.Dispose();
            _loggerFactory.Dispose();
        }

        private static TimerWriteApiOptions CreateOptions(Dictionary<string, string?>? settings)
        {
            var values = new Dictionary<string, string?>
            {
                ["TimerBackend:WriteApi:Enabled"] = "true",
                ["TimerBackend:WriteApi:RulesetVersion"] = "7",
                ["TimerBackend:WriteApi:StyleFactors:0"] = "1",
                // The listener binds port 0, so no port can be configured ahead.
                ["TimerBackend:WriteApi:AllowAllListeners"] = "true",
            };
            foreach (var (key, value) in settings ?? [])
            {
                values[key] = value;
            }

            return TimerWriteApiOptions.FromConfiguration(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        }
    }

    private sealed class DisposableDatabaseFactAttribute : FactAttribute
    {
        public DisposableDatabaseFactAttribute(string environment)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environment)))
            {
                Skip = $"Set {environment} to a disposable test database.";
            }
        }
    }

    private sealed class WindowsTlsFactAttribute : FactAttribute
    {
        public WindowsTlsFactAttribute()
        {
            if (OperatingSystem.IsWindows()
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIMER_TEST_TLS")))
            {
                Skip = "Set TIMER_TEST_TLS in a full Windows user profile to run the Kestrel TLS test; Schannel cannot use the self-signed test key in restricted profiles.";
            }
        }
    }

    private sealed class DisposableTlsDatabaseFactAttribute : FactAttribute
    {
        public DisposableTlsDatabaseFactAttribute(string environment)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environment)))
            {
                Skip = $"Set {environment} to a disposable test database.";
            }
            else if (OperatingSystem.IsWindows()
                     && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TIMER_TEST_TLS")))
            {
                Skip = "Set TIMER_TEST_TLS in a full Windows user profile to run Kestrel TLS; Schannel cannot use the self-signed test key in restricted profiles.";
            }
        }
    }
}
