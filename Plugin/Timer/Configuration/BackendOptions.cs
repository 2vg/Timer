/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Frozen;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Source2Surf.Timer.Configuration;

/// <summary>
/// timer.jsonc's backend section: where Timer.Backend's gRPC services are and how the plugin calls them. The
/// backend owns the database; every record, zone and map read or write goes through it.
/// </summary>
internal sealed class BackendOptions
{
    internal const string SectionName = "backend";

    internal static readonly FrozenSet<string> Keys = FrozenSet.ToFrozenSet(
    [
        "endpoint", "ruleset_version", "rpc_deadline_milliseconds", "poll_interval_milliseconds",
        "shutdown_drain_timeout_milliseconds", "batch_size", "api_key",
    ], StringComparer.OrdinalIgnoreCase);

    private const int DefaultBatchSize = 16;

    internal const string DefaultEndpoint = "http://127.0.0.1:5082";

    public Uri Endpoint { get; }

    /// <summary>
    /// Read from an old score_write section, which still works but should be renamed.
    /// </summary>
    public bool FromScoreWriteSection { get; private init; }

    public TimeSpan RpcDeadline { get; }

    public TimeSpan PollInterval { get; }

    public TimeSpan ShutdownDrainTimeout { get; }

    public int BatchSize { get; }

    /// <summary>
    /// Sent as <c>authorization: Bearer</c> when the backend's write API uses ApiKey authentication; null sends none.
    /// </summary>
    public string? ApiKey { get; private init; }

    private BackendOptions(Uri      endpoint,
                           TimeSpan rpcDeadline,
                           TimeSpan pollInterval,
                           TimeSpan shutdownDrainTimeout,
                           int      batchSize)
    {
        Endpoint             = endpoint;
        RpcDeadline          = rpcDeadline;
        PollInterval         = pollInterval;
        ShutdownDrainTimeout = shutdownDrainTimeout;
        BatchSize            = batchSize;
    }

    public static BackendOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var legacy  = !section.Exists() && configuration.GetSection("score_write").Exists();

        if (legacy)
        {
            section = configuration.GetSection("score_write");
        }

        ValidateKnownSettings(section, legacy);

        var rpcDeadline          = ParseMilliseconds(section["rpc_deadline_milliseconds"],
                                                     "rpc_deadline_milliseconds",
                                                     TimeSpan.FromSeconds(10),
                                                     minimum: 100,
                                                     maximum: 300_000);
        var pollInterval         = ParseMilliseconds(section["poll_interval_milliseconds"],
                                                     "poll_interval_milliseconds",
                                                     TimeSpan.FromMilliseconds(250),
                                                     minimum: 10,
                                                     maximum: 60_000);
        var shutdownDrainTimeout = ParseMilliseconds(section["shutdown_drain_timeout_milliseconds"],
                                                     "shutdown_drain_timeout_milliseconds",
                                                     TimeSpan.FromSeconds(15),
                                                     minimum: 100,
                                                     maximum: 300_000);
        var batchSize            = ParseInteger(section["batch_size"],
                                                "batch_size",
                                                DefaultBatchSize,
                                                minimum: 1,
                                                maximum: 128);
        var endpoint             = ParseEndpoint(section["endpoint"]);

        return new BackendOptions(endpoint, rpcDeadline, pollInterval, shutdownDrainTimeout, batchSize)
        {
            FromScoreWriteSection = legacy,
            ApiKey                = ParseApiKey(section["api_key"]),
        };
    }

    internal static BackendOptions CreateForTests(Uri       endpoint,
                                                  TimeSpan? rpcDeadline = null,
                                                  TimeSpan? pollInterval = null,
                                                  TimeSpan? shutdownDrainTimeout = null,
                                                  int       batchSize = DefaultBatchSize)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return Create(endpoint,
                      rpcDeadline ?? TimeSpan.FromSeconds(1),
                      pollInterval ?? TimeSpan.FromMilliseconds(10),
                      shutdownDrainTimeout ?? TimeSpan.FromSeconds(2),
                      batchSize);
    }

    internal static BackendOptions Create(Uri      endpoint,
                                          TimeSpan rpcDeadline,
                                          TimeSpan pollInterval,
                                          TimeSpan shutdownDrainTimeout,
                                          int      batchSize)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateEndpoint(endpoint);
        ValidateDuration(rpcDeadline, nameof(rpcDeadline), minimumMilliseconds: 100, maximumMilliseconds: 300_000);
        ValidateDuration(pollInterval, nameof(pollInterval), minimumMilliseconds: 10, maximumMilliseconds: 60_000);
        ValidateDuration(shutdownDrainTimeout,
                         nameof(shutdownDrainTimeout),
                         minimumMilliseconds: 100,
                         maximumMilliseconds: 300_000);

        if (batchSize is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        return new BackendOptions(endpoint, rpcDeadline, pollInterval, shutdownDrainTimeout, batchSize);
    }

    // score_write's mode setting is gone: the backend is the only storage.
    private static void ValidateKnownSettings(IConfigurationSection section, bool legacy)
    {
        foreach (var setting in section.GetChildren())
        {
            if (!Keys.Contains(setting.Key) && !(legacy && setting.Key.Equals("mode", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"{SectionName}:{setting.Key} is not supported.");
            }
        }
    }

    private static int ParseInteger(string? raw, string name, int defaultValue, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{name} must be an integer from {minimum} through {maximum}.");
        }

        return value;
    }

    private static TimeSpan ParseMilliseconds(string? raw,
                                              string  name,
                                              TimeSpan defaultValue,
                                              int     minimum,
                                              int     maximum)
    {
        var milliseconds = ParseInteger(raw,
                                        name,
                                        checked((int)defaultValue.TotalMilliseconds),
                                        minimum,
                                        maximum);

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static Uri ParseEndpoint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = DefaultEndpoint;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"{SectionName}:endpoint must be an absolute URI.");
        }

        ValidateEndpoint(endpoint);
        return endpoint;
    }

    // Printable ASCII without spaces, as the backend accepts; at most its 256-character limit.
    private static string? ParseApiKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var key = raw.Trim();
        if (key.Length > 256 || key.AsSpan().ContainsAnyExceptInRange('!', '~'))
        {
            throw new InvalidOperationException($"{SectionName}:api_key must be at most 256 printable ASCII characters without spaces.");
        }

        return key;
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri
            || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0)
        {
            throw new InvalidOperationException($"{SectionName}:endpoint must be a plain absolute service URI.");
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{SectionName}:endpoint must use HTTP or HTTPS.");
    }

    private static void ValidateDuration(TimeSpan value,
                                         string   name,
                                         int      minimumMilliseconds,
                                         int      maximumMilliseconds)
    {
        var milliseconds = value.TotalMilliseconds;
        if (!double.IsFinite(milliseconds)
            || milliseconds < minimumMilliseconds
            || milliseconds > maximumMilliseconds)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
