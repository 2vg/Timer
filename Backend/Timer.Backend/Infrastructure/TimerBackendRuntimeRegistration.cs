using System;
using System.Net;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Source2Surf.Timer.Backend.Contracts;
using Timer.Backend.Configuration;

namespace Timer.Backend.Infrastructure;

internal static class TimerBackendRuntimeRegistration
{
    private static readonly TimeSpan ShutdownMargin = TimeSpan.FromSeconds(5);

    internal static void Add(IServiceCollection services, TimerBackendRuntimeOptions options,
                              TimeSpan? leaderboardCacheDuration = null)
    {
        services.AddSingleton(options);

        // The host stops Kestrel (draining requests, each bounded by RequestTimeout) and then the
        // storage worker within ONE HostOptions.ShutdownTimeout, 30 s by default. Without this a
        // WorkerShutdownTimeout above ~30 s had no effect. Only ever raise the host budget.
        services.Configure<HostOptions>(host =>
        {
            var needed = options.RequestTimeout + options.WorkerShutdownTimeout + ShutdownMargin;
            if (host.ShutdownTimeout < needed)
            {
                host.ShutdownTimeout = needed;
            }
        });

        services.AddRequestTimeouts(timeouts =>
        {
            timeouts.AddPolicy("TimerRead", new RequestTimeoutPolicy
            {
                Timeout = options.RequestTimeout,
                WriteTimeoutResponse = context => context.Response.WriteAsJsonAsync(new ApiErrorDto
                {
                    Code = "request_timeout",
                    Message = "The request deadline elapsed.",
                    RequestId = context.TraceIdentifier,
                }, BackendJsonContext.Default.ApiErrorDto),
            });
            timeouts.AddPolicy("TimerHealth", TimeSpan.FromSeconds(5));
        });
        services.AddOutputCache(outputCache =>
        {
            outputCache.SizeLimit = 100 * 1024 * 1024;
            outputCache.MaximumBodySize = 64 * 1024 * 1024;
            outputCache.AddPolicy(LeaderboardCachePolicy, policy =>
                policy.AddPolicy<TimerLeaderboardCachePolicy>()
                      .Expire(leaderboardCacheDuration ?? TimeSpan.FromSeconds(15))
                      // Request locking would hand the first request's exception to every coalesced
                      // waiter, so one client disconnect or timeout would fail them all with 500.
                      .SetLocking(false)
                      // A single site: per-Host entries would let any Host header force a miss.
                      .SetVaryByHost(false)
                      // Key only on parameters the handlers read. The default varies on every
                      // query parameter, so ?x=1, ?x=2, ... each missed and ran a 5,000-row query.
                      .SetVaryByQuery("style", "track", "stage", "limit"),
                excludeDefaultPolicy: true);
        });

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = static (context, cancellationToken) =>
                new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(new ApiErrorDto
                {
                    Code = "too_many_requests",
                    Message = "Too many requests; retry later.",
                    RequestId = context.HttpContext.TraceIdentifier,
                }, BackendJsonContext.Default.ApiErrorDto, cancellationToken: cancellationToken));

            // One limiter for every database-backed read, so anonymous traffic can never hold more than this many
            // of the pool's connections that game-server writes also need. Output-cache hits never reach it.
            limiter.AddConcurrencyLimiter(ReadDatabasePolicy, concurrency =>
            {
                concurrency.PermitLimit          = options.ReadConcurrencyLimit;
                concurrency.QueueLimit           = options.ReadQueueLimit;
                concurrency.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });

            if (options.ClientRequestsPerSecond > 0)
            {
                var perSecond = options.ClientRequestsPerSecond;
                var burst = Math.Max(options.ClientRequestBurst, perSecond);
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, IPAddress>(context =>
                    context.Request.Path.StartsWithSegments(ApiPathPrefix)
                        ? RateLimitPartition.GetTokenBucketLimiter(
                            context.Connection.RemoteIpAddress ?? IPAddress.None,
                            _ => new TokenBucketRateLimiterOptions
                            {
                                TokenLimit           = burst,
                                TokensPerPeriod      = perSecond,
                                ReplenishmentPeriod  = TimeSpan.FromSeconds(1),
                                QueueLimit           = 0,
                                AutoReplenishment    = true,
                            })
                        : RateLimitPartition.GetNoLimiter(IPAddress.None));
            }
        });

        if (options.TrustsForwardedHeaders)
        {
            services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                foreach (var proxy in options.KnownProxies)
                {
                    forwarded.KnownProxies.Add(proxy);
                }

                foreach (var network in options.KnownProxyNetworks)
                {
                    forwarded.KnownIPNetworks.Add(network);
                }
            });
        }
    }

    internal const string LeaderboardCachePolicy = "TimerLeaderboard";
    internal const string ReadDatabasePolicy = "TimerReadDatabase";
    private static readonly PathString ApiPathPrefix = new ("/api");

    /// <summary>
    /// Before routing: trusted proxies' client address, so per-IP limits apply to clients rather than the proxy.
    /// Registered only when proxies are configured; otherwise every forwarded header is ignored.
    /// </summary>
    internal static void UseForwardedHeadersIfConfigured(IApplicationBuilder app, TimerBackendRuntimeOptions options)
    {
        if (options.TrustsForwardedHeaders)
        {
            app.UseForwardedHeaders();
        }
    }
}
