using System;
using System.Globalization;
using Source2Surf.Timer.Shared;

namespace Timer.Backend.Endpoints;

internal static class ApiRouteValidation
{
    public const int MaximumRecordLimit = 5000;

    public static bool TryNormalizeMapName(string? raw, out string mapName, out string error)
    {
        mapName = string.Empty;
        error   = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "mapName is required.";
            return false;
        }

        var normalized = raw.Trim();

        if (normalized.Length > 192)
        {
            error = "mapName must contain at most 192 characters.";
            return false;
        }

        var lowered = normalized.ToLowerInvariant();

        // Same character set the write path accepts. Anything else cannot name a stored map, and
        // on MySQL an accented name would otherwise match a different map through the collation.
        foreach (var character in lowered)
        {
            if (!(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
            {
                error = "mapName contains an unsupported character.";
                return false;
            }
        }

        mapName = lowered;
        return true;
    }

    /// <summary>
    /// A map name from a REST route. Surrounding whitespace is refused rather than trimmed: each spelling would be
    /// its own output-cache entry (paths are cached case-insensitively, so letter case is still accepted).
    /// </summary>
    public static bool TryParseRouteMapName(string? raw, out string mapName, out string error)
    {
        if (raw is { Length: > 0 } && (char.IsWhiteSpace(raw[0]) || char.IsWhiteSpace(raw[^1])))
        {
            mapName = string.Empty;
            error   = "mapName must not contain surrounding whitespace.";
            return false;
        }

        return TryNormalizeMapName(raw, out mapName, out error);
    }

    public static bool TryParseSteamId(string? raw, out ulong steamId, out string error)
        => TryParseUnsignedId(raw, "steamId", out steamId, out error);

    // Run ids are stored as signed 64-bit values.
    public static bool TryParseRunId(string? raw, out ulong runId, out string error)
    {
        if (!TryParseUnsignedId(raw, "runId", out runId, out error))
        {
            return false;
        }

        if (runId > long.MaxValue)
        {
            runId = 0;
            error = $"runId must be at most {long.MaxValue}.";
            return false;
        }

        return true;
    }

    public static bool TryParseLimit(string? raw, out int limit, out string error)
    {
        limit = MaximumRecordLimit;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!IsCanonicalNumber(raw)
            || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            || limit is < 1 or > MaximumRecordLimit)
        {
            error = $"limit must be an integer between 1 and {MaximumRecordLimit}.";
            return false;
        }

        return true;
    }

    public static bool TryParseOptionalStyle(string? raw, out int? style, out string error)
        => TryParseOptionalIndex(raw, "style", TimerConstants.MAX_STYLE, 0, out style, out error);

    public static bool TryParseOptionalTrack(string? raw, out int? track, out string error)
        => TryParseOptionalIndex(raw, "track", TimerConstants.MAX_TRACK, 0, out track, out error);

    public static bool TryParseOptionalStage(string? raw, out int? stage, out string error)
        => TryParseOptionalIndex(raw, "stage", TimerConstants.MAX_STAGE, 1, out stage, out error);

    private static bool TryParseUnsignedId(string? raw, string parameterName, out ulong value, out string error)
    {
        value = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw)
            || !IsCanonicalNumber(raw)
            || !ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            || value == 0)
        {
            error = $"{parameterName} must be a non-zero unsigned decimal string.";
            return false;
        }

        return true;
    }

    private static bool TryParseOptionalIndex(string? raw,
                                              string  parameterName,
                                              int     exclusiveMaximum,
                                              int     inclusiveMinimum,
                                              out int? value,
                                              out string error)
    {
        value = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!IsCanonicalNumber(raw)
            || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < inclusiveMinimum
            || parsed >= exclusiveMaximum)
        {
            error = $"{parameterName} must be an integer in [{inclusiveMinimum}, {exclusiveMaximum}).";
            return false;
        }

        value = parsed;
        return true;
    }

    // One spelling per value ("7", never "07"), so query values cannot multiply output-cache keys.
    private static bool IsCanonicalNumber(string raw)
        => raw.Length == 1 || raw[0] != '0';
}
