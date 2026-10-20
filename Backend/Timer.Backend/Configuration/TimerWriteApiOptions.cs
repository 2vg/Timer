using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Timer.Backend.WriteApi;

namespace Timer.Backend.Configuration;

/// <summary>
/// Configuration for the optional MagicOnion write API.
/// </summary>
internal sealed class TimerWriteApiOptions
{
    public const string SectionName = "TimerBackend:WriteApi";

    public bool Enabled { get; }

    public int RulesetVersion { get; }

    /// <summary>
    /// The configured factors; without any, those the game servers registered for their styles, or until then the
    /// implicit style-0 default.
    /// </summary>
    public IReadOnlyDictionary<int, double> StyleFactors => Volatile.Read(ref _styleFactors);

    private IReadOnlyDictionary<int, double> _styleFactors;
    private volatile bool                    _hasRegisteredStyleFactors;

    /// <summary>
    /// False when <see cref="StyleFactors"/> is only the implicit style-0 default. Score
    /// administration must not treat that default as the serving instance's policy.
    /// </summary>
    public bool HasExplicitStyleFactors { get; }

    /// <summary>
    /// True once game servers' registered factors replaced the implicit style-0 default.
    /// </summary>
    public bool HasRegisteredStyleFactors => _hasRegisteredStyleFactors;

    /// <summary>
    /// Factors to put on every board a tier change or recalculation queues: configured or registered, with style 0.
    /// </summary>
    public bool HasScorePolicy => (HasExplicitStyleFactors || HasRegisteredStyleFactors) && StyleFactors.ContainsKey(0);

    /// <summary>
    /// Local listener ports that may serve write RPCs. Empty is only valid with <see cref="AllowAllListeners"/>.
    /// </summary>
    public IReadOnlySet<int> LocalPorts { get; }

    /// <summary>
    /// Explicit opt-in to serve write RPCs on every listener (e.g. a Unix socket behind a gRPC proxy, which has no
    /// local port). Without it an empty <see cref="LocalPorts"/> refuses to start.
    /// </summary>
    public bool AllowAllListeners { get; }

    public TimerWriteAuthenticationMode AuthenticationMode { get; }

    /// <summary>Configured API clients by key hash; empty unless <see cref="AuthenticationMode"/> is ApiKey.</summary>
    public FrozenDictionary<TimerWriteKeyHash, TimerWriteClient> Clients { get; }

    /// <summary>Peer networks allowed to call write RPCs, checked against the direct connection's address. Empty allows any.</summary>
    public IReadOnlyList<IPNetwork> AllowedNetworks { get; }

    /// <summary>
    /// Replay URL prefixes game servers may store, each ending in '/'. Empty allows any http/https URL.
    /// </summary>
    public IReadOnlyList<string> ReplayUrlPrefixes { get; }

    private TimerWriteApiOptions(bool enabled,
                                 int rulesetVersion,
                                 IReadOnlyDictionary<int, double> styleFactors,
                                 bool hasExplicitStyleFactors,
                                 IReadOnlySet<int> localPorts,
                                 bool allowAllListeners,
                                 TimerWriteAuthenticationMode authenticationMode,
                                 FrozenDictionary<TimerWriteKeyHash, TimerWriteClient> clients,
                                 IReadOnlyList<IPNetwork> allowedNetworks,
                                 IReadOnlyList<string> replayUrlPrefixes)
    {
        Enabled = enabled;
        RulesetVersion = rulesetVersion;
        _styleFactors = styleFactors;
        HasExplicitStyleFactors = hasExplicitStyleFactors;
        LocalPorts = localPorts;
        AllowAllListeners = allowAllListeners;
        AuthenticationMode = authenticationMode;
        Clients = clients;
        AllowedNetworks = allowedNetworks;
        ReplayUrlPrefixes = replayUrlPrefixes;
    }

    private static readonly FrozenSet<string> SupportedKeys = FrozenSet.ToFrozenSet(
    [
        "Enabled", "RulesetVersion", "StyleFactors", "LocalPorts", "AllowAllListeners", "Authentication",
        "AllowedNetworks", "ReplayUrlPrefixes",
    ], StringComparer.OrdinalIgnoreCase);

    public static TimerWriteApiOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        foreach (var setting in section.GetChildren())
        {
            if (!SupportedKeys.Contains(setting.Key))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:{setting.Key} is not supported.");
            }
        }

        // A backend serves its game servers unless told otherwise (a read-only replica).
        var enabled = string.IsNullOrWhiteSpace(section["Enabled"]) || ParseBoolean(section["Enabled"], $"{SectionName}:Enabled");
        var rulesetVersion = string.IsNullOrWhiteSpace(section["RulesetVersion"])
                                 ? (enabled ? 1 : 0)
                                 : ParseInteger(section["RulesetVersion"],
                                                $"{SectionName}:RulesetVersion",
                                                minimum: 0);
        var styleFactors = ParseStyleFactors(section.GetSection("StyleFactors"));
        var hasExplicitStyleFactors = styleFactors.Count != 0;
        var localPorts = ParseLocalPorts(section.GetSection("LocalPorts"));
        var allowAllListeners = ParseBoolean(section["AllowAllListeners"], $"{SectionName}:AllowAllListeners");
        var (authenticationMode, clients) = ParseAuthentication(section.GetSection("Authentication"));
        var allowedNetworks = ParseNetworks(section.GetSection("AllowedNetworks"));
        var replayUrlPrefixes = ParseReplayUrlPrefixes(section.GetSection("ReplayUrlPrefixes"));

        // A fresh backend accepts the main style at factor 1 without additional setup.
        // Once custom factors are configured, style 0 must remain explicit.
        if (!hasExplicitStyleFactors)
        {
            styleFactors.Add(0, 1d);
        }

        if (enabled)
        {
            if (rulesetVersion <= 0)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:RulesetVersion must be greater than zero when the write API is enabled.");
            }

            if (!styleFactors.ContainsKey(0))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:StyleFactors must contain style 0 when the write API is enabled.");
            }
        }

        return new TimerWriteApiOptions(
            enabled,
            rulesetVersion,
            new ReadOnlyDictionary<int, double>(styleFactors),
            hasExplicitStyleFactors,
            localPorts,
            allowAllListeners,
            authenticationMode,
            clients,
            allowedNetworks,
            replayUrlPrefixes);
    }

    /// <summary>
    /// Takes the game servers' registered factors in place of the implicit default. Configured factors always win, and
    /// a set without style 0 is refused.
    /// </summary>
    public bool UseRegisteredStyleFactors(IReadOnlyDictionary<int, double> factors)
    {
        ArgumentNullException.ThrowIfNull(factors);

        if (HasExplicitStyleFactors || !factors.ContainsKey(0))
        {
            return false;
        }

        Volatile.Write(ref _styleFactors, new ReadOnlyDictionary<int, double>(new Dictionary<int, double>(factors)));
        _hasRegisteredStyleFactors = true;

        return true;
    }

    private static HashSet<int> ParseLocalPorts(IConfigurationSection section)
    {
        var localPorts = new HashSet<int>();

        foreach (var (path, value) in ReadList(section))
        {
            if (!int.TryParse(value,
                              NumberStyles.None,
                              CultureInfo.InvariantCulture,
                              out var port)
                || port is < 1 or > 65535)
            {
                throw new InvalidOperationException($"{path} must be a port number from 1 through 65535.");
            }

            localPorts.Add(port);
        }

        return localPorts;
    }

    // Accept a single scalar (environment variable) or a JSON array, but not both: configuration
    // layers merge rather than replace, so an env scalar on top of an appsettings array would
    // silently keep the old entries alongside the new one.
    private static List<(string Path, string? Value)> ReadList(IConfigurationSection section)
    {
        var values = new List<(string Path, string? Value)>();
        var children = section.GetChildren().ToList();
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            if (children.Count != 0)
            {
                throw new InvalidOperationException(
                    $"{section.Path} is set both as a single value and as a list (e.g. an environment variable over an appsettings array). Use one form; for a list in environment variables use {section.Path.Replace(":", "__")}__0, __1, ...");
            }

            values.Add((section.Path, section.Value));
        }

        foreach (var child in children)
        {
            values.Add((child.Path, child.Value));
        }

        return values;
    }

    private static (TimerWriteAuthenticationMode Mode, FrozenDictionary<TimerWriteKeyHash, TimerWriteClient> Clients)
        ParseAuthentication(IConfigurationSection section)
    {
        foreach (var setting in section.GetChildren())
        {
            if (!setting.Key.Equals("Mode", StringComparison.OrdinalIgnoreCase)
                && !setting.Key.Equals("Clients", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{setting.Path} is not supported.");
            }
        }

        var rawMode = section["Mode"];
        TimerWriteAuthenticationMode mode;
        if (string.IsNullOrWhiteSpace(rawMode) || rawMode.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            mode = TimerWriteAuthenticationMode.None;
        }
        else if (rawMode.Equals("ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            mode = TimerWriteAuthenticationMode.ApiKey;
        }
        else
        {
            throw new InvalidOperationException($"{section.Path}:Mode must be None or ApiKey.");
        }

        var clientSections = section.GetSection("Clients").GetChildren().ToList();
        if (mode == TimerWriteAuthenticationMode.None)
        {
            if (clientSections.Count != 0)
            {
                throw new InvalidOperationException(
                    $"{section.Path}:Clients is set but {section.Path}:Mode is not ApiKey; keys would be ignored.");
            }

            return (mode, FrozenDictionary<TimerWriteKeyHash, TimerWriteClient>.Empty);
        }

        if (clientSections.Count == 0)
        {
            throw new InvalidOperationException($"{section.Path}:Mode ApiKey needs at least one entry in {section.Path}:Clients.");
        }

        var clients = new Dictionary<TimerWriteKeyHash, TimerWriteClient>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in clientSections)
        {
            foreach (var setting in client.GetChildren())
            {
                if (!setting.Key.Equals("Name", StringComparison.OrdinalIgnoreCase)
                    && !setting.Key.Equals("KeySha256", StringComparison.OrdinalIgnoreCase)
                    && !setting.Key.Equals("Scopes", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{setting.Path} is not supported.");
                }
            }

            var name = client["Name"]?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 64 || !names.Add(name))
            {
                throw new InvalidOperationException($"{client.Path}:Name must be a unique non-empty name of at most 64 characters.");
            }

            var rawHash = client["KeySha256"]?.Trim();
            var hash = new byte[32];
            if (rawHash is not { Length: 64 }
                || Convert.FromHexString(rawHash, hash, out _, out var written) != System.Buffers.OperationStatus.Done
                || written != 32)
            {
                throw new InvalidOperationException($"{client.Path}:KeySha256 must be the 64-character hex SHA-256 of the key.");
            }

            var scopes = TimerWriteScopes.None;
            foreach (var (path, value) in ReadList(client.GetSection("Scopes")))
            {
                scopes |= value?.Trim() switch
                {
                    { } s when s.Equals("Game", StringComparison.OrdinalIgnoreCase)  => TimerWriteScopes.Game,
                    { } s when s.Equals("Admin", StringComparison.OrdinalIgnoreCase) => TimerWriteScopes.Admin,
                    _ => throw new InvalidOperationException($"{path} must be Game or Admin."),
                };
            }

            if (scopes == TimerWriteScopes.None)
            {
                throw new InvalidOperationException($"{client.Path}:Scopes must list Game, Admin or both.");
            }

            if (!clients.TryAdd(TimerWriteKeyHash.FromHash(hash), new TimerWriteClient(name, scopes)))
            {
                throw new InvalidOperationException($"{client.Path}:KeySha256 duplicates another client's key.");
            }
        }

        return (mode, clients.ToFrozenDictionary());
    }

    private static IReadOnlyList<IPNetwork> ParseNetworks(IConfigurationSection section)
    {
        var networks = new List<IPNetwork>();
        foreach (var (path, value) in ReadList(section))
        {
            var raw = value?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                throw new InvalidOperationException($"{path} must be an IP address or CIDR network.");
            }

            if (IPNetwork.TryParse(raw, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(raw, out var address))
            {
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                throw new InvalidOperationException($"{path} must be an IP address or CIDR network such as 10.0.0.0/8.");
            }
        }

        return networks.AsReadOnly();
    }

    private static IReadOnlyList<string> ParseReplayUrlPrefixes(IConfigurationSection section)
    {
        var prefixes = new List<string>();
        foreach (var (path, value) in ReadList(section))
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException($"{path} must be an absolute http or https URL without query, fragment or credentials.");
            }

            var prefix = uri.AbsoluteUri;
            prefixes.Add(prefix.EndsWith('/') ? prefix : prefix + "/");
        }

        return prefixes.AsReadOnly();
    }

    private static Dictionary<int, double> ParseStyleFactors(IConfigurationSection section)
    {
        var styleFactors = new Dictionary<int, double>();

        foreach (var child in section.GetChildren())
        {
            var styleKey = child.Key.Trim();
            if (!int.TryParse(styleKey,
                              NumberStyles.Integer,
                              CultureInfo.InvariantCulture,
                              out var style)
                || style is < 0 or > 15)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:StyleFactors:{child.Key} must be an integer from 0 through 15.");
            }

            if (!double.TryParse(child.Value,
                                 NumberStyles.Float,
                                 CultureInfo.InvariantCulture,
                                 out var factor)
                || !double.IsFinite(factor)
                || factor is < 0 or > 100)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:StyleFactors:{child.Key} must be a finite number from 0 through 100.");
            }

            if (!styleFactors.TryAdd(style, factor))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:StyleFactors contains duplicate style '{style}'.");
            }
        }

        return styleFactors;
    }

    private static bool ParseBoolean(string? raw, string path)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!bool.TryParse(raw, out var value))
        {
            throw new InvalidOperationException($"{path} must be true or false.");
        }

        return value;
    }

    private static int ParseInteger(string? raw, string path, int minimum)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        if (!int.TryParse(raw,
                         NumberStyles.Integer,
                         CultureInfo.InvariantCulture,
                         out var value)
            || value < minimum)
        {
            throw new InvalidOperationException($"{path} must be an integer greater than or equal to {minimum}.");
        }

        return value;
    }

}
