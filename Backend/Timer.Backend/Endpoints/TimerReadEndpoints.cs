using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Backend.Contracts;
using Source2Surf.Timer.Shared.Models;
using Timer.Backend.Mapping;
using Timer.Backend.Configuration;
using Timer.Backend.Infrastructure;
using Timer.Backend.Storage;

namespace Timer.Backend.Endpoints;

internal static class TimerReadEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var runtime = endpoints.ServiceProvider.GetService<TimerBackendRuntimeOptions>() ?? new TimerBackendRuntimeOptions();

        var health = endpoints.MapGroup("/health")
                              .WithTags("Health");
        health.WithRequestTimeout("TimerHealth");

        health.MapGet("/live", () => TypedResults.Ok(new HealthStatusDto { Status = "live" }))
              .Produces<HealthStatusDto>(StatusCodes.Status200OK);

        // The probes that query the database share the read limit and, when configured, stay on internal ports.
        var probes = health.MapGroup(string.Empty)
                           .RequireRateLimiting(TimerBackendRuntimeRegistration.ReadDatabasePolicy);
        if (runtime.InternalHealthPorts.Count != 0)
        {
            var ports = runtime.InternalHealthPorts;
            probes.AddEndpointFilter((context, next) =>
                ports.Contains(context.HttpContext.Connection.LocalPort)
                    ? next(context)
                    : ValueTask.FromResult<object?>(Results.NotFound()));
        }

        probes.MapGet("/ready", GetReadinessAsync)
              .Produces<HealthStatusDto>(StatusCodes.Status200OK)
               .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);
        probes.MapGet("/worker", GetWorkerHealthAsync)
              .Produces<WorkerHealthStatusDto>(StatusCodes.Status200OK)
              .Produces<WorkerHealthStatusDto>(StatusCodes.Status503ServiceUnavailable);

        var api = endpoints.MapGroup($"/api/{ApiVersions.V1}")
                            .WithTags("Timer read API");
        api.WithRequestTimeout("TimerRead");
        // After the output cache in the pipeline, so only misses take a database slot.
        api.RequireRateLimiting(TimerBackendRuntimeRegistration.ReadDatabasePolicy);

        api.MapGet("/maps", GetMapCatalogAsync)
           .Produces<MapListResponse>(StatusCodes.Status200OK);
        api.MapGet("/maps/{mapName}", GetMapProfileAsync)
           .Produces<MapProfileDto>(StatusCodes.Status200OK)
           .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
           .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);
        api.MapGet("/maps/{mapName}/leaderboard", GetMainLeaderboardAsync)
           .CacheOutput(TimerBackendRuntimeRegistration.LeaderboardCachePolicy)
           .Produces<RecordListResponse>(StatusCodes.Status200OK)
           .Produces(StatusCodes.Status304NotModified)
           .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
           .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);
        api.MapGet("/maps/{mapName}/stage-leaderboard", GetStageLeaderboardAsync)
           .CacheOutput(TimerBackendRuntimeRegistration.LeaderboardCachePolicy)
           .Produces<RecordListResponse>(StatusCodes.Status200OK)
           .Produces(StatusCodes.Status304NotModified)
           .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
           .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);
        api.MapGet("/records/{runId}/checkpoints", GetRecordCheckpointsAsync)
           .Produces<RunCheckpointDto[]>(StatusCodes.Status200OK)
           .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        api.MapGet("/players/{steamId}/maps/{mapName}/records", GetPlayerMainRecordsAsync)
           .Produces<RecordListResponse>(StatusCodes.Status200OK)
           .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
           .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);
        api.MapGet("/players/{steamId}/maps/{mapName}/stage-records", GetPlayerStageRecordsAsync)
           .Produces<RecordListResponse>(StatusCodes.Status200OK)
           .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
           .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);
        api.MapGet("/players/{steamId}/points-rank", GetPointsRankAsync)
           .Produces<PointsRankDto>(StatusCodes.Status200OK)
           .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);
        api.MapGet("/players/{steamId}/maps/{mapName}/stats", GetPlayerMapStatsAsync)
           .Produces<PlayerMapStatsDto>(StatusCodes.Status200OK)
           .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
           .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetReadinessAsync(HttpContext                       context,
                                                          TimerBackendStorage               storage,
                                                          ILoggerFactory                    loggerFactory)
    {
        try
        {
            await storage.CheckReadyAsync(context.RequestAborted);
            return TypedResults.Ok(new HealthStatusDto { Status = "ready" });
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("Timer.Backend.Readiness")
                         .LogWarning(exception, "Timer backend readiness query failed.");
            return ApiErrors.Create(context,
                                    StatusCodes.Status503ServiceUnavailable,
                                    "database_unavailable",
                                    "The timer database is not ready.");
        }
    }

    private static async Task<IResult> GetWorkerHealthAsync(HttpContext context, TimerBackendStorage storage,
                                                          TimerBackendRuntimeOptions options, ILoggerFactory loggerFactory)
    {
        try
        {
            var snapshot = await storage.GetWorkerHealthAsync(context.RequestAborted);
            var response = WorkerHealthPolicy.Evaluate(snapshot, options.WorkerMaxLag, DateTime.UtcNow);
            return TypedResults.Json(response, statusCode: response.Status == "degraded"
                ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("Timer.Backend.WorkerHealth")
                .LogWarning(exception, "Score worker health query failed.");
            return ApiErrors.Create(context, StatusCodes.Status503ServiceUnavailable,
                "worker_unavailable", "The score worker is not ready.");
        }
    }

    private static async Task<IResult> GetMapCatalogAsync(TimerBackendStorage storage, CancellationToken cancellationToken)
    {
        var names = await storage.GetAllMapNamesAsync(cancellationToken);
        var mapNames = new string[names.Count];
        for (var index = 0; index < names.Count; index++)
        {
            mapNames[index] = names[index];
        }

        return TypedResults.Ok(new MapListResponse { MapNames = mapNames });
    }

    private static async Task<IResult> GetMapProfileAsync(string              mapName,
                                                           HttpContext         context,
                                                           TimerBackendStorage storage)
    {
        if (!ApiRouteValidation.TryParseRouteMapName(mapName, out var canonicalMapName, out var error))
        {
            return ApiErrors.BadRequest(context, "invalid_map_name", error);
        }

        var profile = await storage.GetExistingMapInfoAsync(canonicalMapName, context.RequestAborted);

        return profile is null
            ? ApiErrors.NotFound(context, "map_not_found", "The requested map does not exist.")
            : TypedResults.Ok(TimerDtoMapper.ToDto(profile));
    }

    private static Task<IResult> GetMainLeaderboardAsync(string              mapName,
                                                          string?             style,
                                                          string?             track,
                                                          string?             limit,
                                                          HttpContext         context,
                                                          TimerBackendStorage storage,
                                                          ILoggerFactory      loggerFactory)
        => GetLeaderboardAsync(mapName, style, track, stage: null, limit, stageRecords: false, context, storage, loggerFactory);

    private static Task<IResult> GetStageLeaderboardAsync(string              mapName,
                                                           string?             style,
                                                           string?             track,
                                                           string?             stage,
                                                           string?             limit,
                                                           HttpContext         context,
                                                           TimerBackendStorage storage,
                                                          ILoggerFactory      loggerFactory)
        => GetLeaderboardAsync(mapName, style, track, stage, limit, stageRecords: true, context, storage, loggerFactory);

    private static async Task<IResult> GetLeaderboardAsync(string              mapName,
                                                            string?             style,
                                                            string?             track,
                                                            string?             stage,
                                                            string?             limit,
                                                            bool                stageRecords,
                                                            HttpContext         context,
                                                            TimerBackendStorage storage,
                                                          ILoggerFactory      loggerFactory)
    {
        if (!ApiRouteValidation.TryParseRouteMapName(mapName, out var canonicalMapName, out var error))
        {
            return ApiErrors.BadRequest(context, "invalid_map_name", error);
        }

        if (!ApiRouteValidation.TryParseOptionalStyle(style, out var styleValue, out error)
            || !ApiRouteValidation.TryParseOptionalTrack(track, out var trackValue, out error)
            || !ApiRouteValidation.TryParseLimit(limit, out var limitValue, out error))
        {
            return ApiErrors.BadRequest(context, "invalid_query_parameter", error);
        }

        int? stageValue = null;
        if (stageRecords && !ApiRouteValidation.TryParseOptionalStage(stage, out stageValue, out error))
        {
            return ApiErrors.BadRequest(context, "invalid_query_parameter", error);
        }

        if (!await storage.MapExistsAsync(canonicalMapName, context.RequestAborted))
        {
            return ApiErrors.NotFound(context, "map_not_found", "The requested map does not exist.");
        }

        var records = await storage.GetMapRecordsForReadApiAsync(canonicalMapName,
                                                                  stageRecords,
                                                                  styleValue,
                                                                  trackValue,
                                                                  stageValue,
                                                                  limitValue, context.RequestAborted);
        var response = new RecordListResponse
        {
            MapName = canonicalMapName,
            Records = ToRecordDtos(records, loggerFactory),
        };
        var entityTag = EntityTags.For(response);

        context.Response.Headers["ETag"] = entityTag;
        context.Response.Headers["Cache-Control"] = "public, max-age=15";

        // When the output cache will store this response, answer 200 even to a matching If-None-Match: a 304 is
        // never cached, so after each expiry every polling client would otherwise reach the database until some
        // unconditional request refilled the entry. The cache then answers later conditional hits with 304 itself.
        var cacheWillStore = context.Features.Get<IOutputCacheFeature>()?.Context.AllowCacheStorage == true;
        if (!cacheWillStore && EntityTags.Matches(context.Request, entityTag))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return TypedResults.Ok(response);
    }

    private static async Task<IResult> GetRecordCheckpointsAsync(string              runId,
                                                                   HttpContext         context,
                                                                   TimerBackendStorage storage,
                                                          ILoggerFactory      loggerFactory)
    {
        if (!ApiRouteValidation.TryParseRunId(runId, out var parsedRunId, out var error))
        {
            return ApiErrors.BadRequest(context, "invalid_run_id", error);
        }

        var checkpoints = await storage.GetRecordCheckpointsAsync(unchecked((long)parsedRunId), context.RequestAborted);
        var response = new List<RunCheckpointDto>(checkpoints.Count);

        var skipped = 0;
        long firstSkippedId = 0;
        foreach (var checkpoint in checkpoints)
        {
            if (TimerDtoMapper.TryToDto(checkpoint, out var dto))
            {
                response.Add(dto);
            }
            else if (skipped++ == 0)
            {
                firstSkippedId = checkpoint.Id;
            }
        }

        if (skipped != 0)
        {
            // Once per request, not per row: a corrupt run must not turn each anonymous read into a log burst.
            loggerFactory.CreateLogger("Timer.Backend.ReadApi")
                         .LogWarning("Skipped {Count} checkpoints of run {RunId} with unrepresentable stored times, first {CheckpointId}.",
                                     skipped, parsedRunId, firstSkippedId);
        }

        return TypedResults.Ok(response.ToArray());
    }

    private static async Task<IResult> GetPlayerMainRecordsAsync(string              steamId,
                                                                   string              mapName,
                                                                   string?             limit,
                                                                   HttpContext         context,
                                                                   TimerBackendStorage storage,
                                                          ILoggerFactory      loggerFactory)
    {
        if (!TryGetPlayerAndMap(steamId, mapName, context, out var parsedSteamId, out var canonicalMapName, out var errorResult))
        {
            return errorResult!;
        }

        if (!ApiRouteValidation.TryParseLimit(limit, out var limitValue, out var error))
        {
            return ApiErrors.BadRequest(context, "invalid_query_parameter", error);
        }

        if (!await storage.MapExistsAsync(canonicalMapName, context.RequestAborted))
        {
            return ApiErrors.NotFound(context, "map_not_found", "The requested map does not exist.");
        }

        var records = await storage.GetPlayerRecordsForReadApiAsync(parsedSteamId,
                                                                     canonicalMapName,
                                                                     stageRecords: false,
                                                                     limitValue, context.RequestAborted);
        return TypedResults.Ok(new RecordListResponse
        {
            MapName = canonicalMapName,
            Records = ToRecordDtos(records, loggerFactory),
        });
    }

    private static async Task<IResult> GetPlayerStageRecordsAsync(string              steamId,
                                                                    string              mapName,
                                                                    string?             limit,
                                                                    HttpContext         context,
                                                                    TimerBackendStorage storage,
                                                          ILoggerFactory      loggerFactory)
    {
        if (!TryGetPlayerAndMap(steamId, mapName, context, out var parsedSteamId, out var canonicalMapName, out var errorResult))
        {
            return errorResult!;
        }

        if (!ApiRouteValidation.TryParseLimit(limit, out var limitValue, out var error))
        {
            return ApiErrors.BadRequest(context, "invalid_query_parameter", error);
        }

        if (!await storage.MapExistsAsync(canonicalMapName, context.RequestAborted))
        {
            return ApiErrors.NotFound(context, "map_not_found", "The requested map does not exist.");
        }

        var records = await storage.GetPlayerRecordsForReadApiAsync(parsedSteamId,
                                                                     canonicalMapName,
                                                                     stageRecords: true,
                                                                     limitValue, context.RequestAborted);
        return TypedResults.Ok(new RecordListResponse
        {
            MapName = canonicalMapName,
            Records = ToRecordDtos(records, loggerFactory),
        });
    }

    private static async Task<IResult> GetPointsRankAsync(string              steamId,
                                                           HttpContext         context,
                                                           TimerBackendStorage storage)
    {
        if (!ApiRouteValidation.TryParseSteamId(steamId, out var parsedSteamId, out var error))
        {
            return ApiErrors.BadRequest(context, "invalid_steam_id", error);
        }

        var (rank, total) = await storage.GetPlayerPointsRankAsync(parsedSteamId, context.RequestAborted);
        return TypedResults.Ok(new PointsRankDto
        {
            SteamId = TimerDtoMapper.ToUnsignedId(parsedSteamId),
            Rank    = rank,
            Total   = total,
        });
    }

    private static async Task<IResult> GetPlayerMapStatsAsync(string              steamId,
                                                               string              mapName,
                                                               HttpContext         context,
                                                               TimerBackendStorage storage)
    {
        if (!TryGetPlayerAndMap(steamId, mapName, context, out var parsedSteamId, out var canonicalMapName, out var errorResult))
        {
            return errorResult!;
        }

        if (!await storage.MapExistsAsync(canonicalMapName, context.RequestAborted))
        {
            return ApiErrors.NotFound(context, "map_not_found", "The requested map does not exist.");
        }

        var (playTime, playCount) = await storage.GetPlayerMapStatsAsync(parsedSteamId, canonicalMapName, context.RequestAborted);
        return TypedResults.Ok(new PlayerMapStatsDto
        {
            SteamId         = TimerDtoMapper.ToUnsignedId(parsedSteamId),
            MapName         = canonicalMapName,
            PlayTimeMicros  = TimerDtoMapper.ToMicrosecondsOrZero(playTime),
            PlayCount       = playCount,
        });
    }

    private static bool TryGetPlayerAndMap(string       steamId,
                                            string       mapName,
                                            HttpContext  context,
                                            out ulong    parsedSteamId,
                                            out string   canonicalMapName,
                                            out IResult? errorResult)
    {
        parsedSteamId    = 0;
        canonicalMapName = string.Empty;
        errorResult      = null;

        if (!ApiRouteValidation.TryParseSteamId(steamId, out parsedSteamId, out var error))
        {
            errorResult = ApiErrors.BadRequest(context, "invalid_steam_id", error);
            return false;
        }

        if (!ApiRouteValidation.TryParseRouteMapName(mapName, out canonicalMapName, out error))
        {
            errorResult = ApiErrors.BadRequest(context, "invalid_map_name", error);
            return false;
        }

        return true;
    }

    private static RunRecordDto[] ToRecordDtos(IReadOnlyList<RunRecord> records, ILoggerFactory loggerFactory)
    {
        var result = new List<RunRecordDto>(records.Count);
        var skipped = 0;
        RunRecord? firstSkipped = null;

        foreach (var record in records)
        {
            if (TimerDtoMapper.TryToDto(record, out var dto))
            {
                result.Add(dto);
            }
            else if (skipped++ == 0)
            {
                // One corrupt legacy row must not turn the whole list into a 500.
                firstSkipped = record;
            }
        }

        if (firstSkipped is not null)
        {
            // Once per request, not per row: up to 5,000 rows must not become 5,000 log lines per anonymous read.
            loggerFactory.CreateLogger("Timer.Backend.ReadApi")
                         .LogWarning("Skipped {Count} runs with unrepresentable stored times, first run {RunId} ({Time}).",
                                     skipped, firstSkipped.Id, firstSkipped.Time);
        }

        return result.ToArray();
    }
}

internal static class ApiErrors
{
    public static IResult BadRequest(HttpContext context, string code, string message)
        => Create(context, StatusCodes.Status400BadRequest, code, message);

    public static IResult NotFound(HttpContext context, string code, string message)
        => Create(context, StatusCodes.Status404NotFound, code, message);

    public static IResult Create(HttpContext context, int statusCode, string code, string message)
    {
        var response = new ApiErrorDto
        {
            Code      = code,
            Message   = message,
            RequestId = context.TraceIdentifier,
        };

        return TypedResults.Json(response,
                                 BackendJsonContext.Default.ApiErrorDto,
                                 statusCode: statusCode);
    }
}

internal static class EntityTags
{
    // A weak validator is deliberate: field-based hashing avoids allocating and serializing a
    // second 5,000-record JSON payload just to validate a response. Every contract field is
    // included in a stable order, so a material read-model change invalidates the cached entry.
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime       = 1099511628211UL;

    public static string For(RecordListResponse response)
    {
        var hash = FnvOffsetBasis;
        AddString(ref hash, response.ApiVersion);
        AddString(ref hash, response.MapName);
        AddInt32(ref hash, response.Records.Length);

        foreach (var record in response.Records)
        {
            AddRecord(ref hash, record);
        }

        return $"W/\"{hash:X16}\"";
    }

    public static bool Matches(HttpRequest request, string entityTag)
    {
        var requestedTags = request.Headers["If-None-Match"];

        var normalizedEntityTag = RemoveWeakPrefix(entityTag);

        foreach (var headerValue in requestedTags)
        {
            if (headerValue is null)
            {
                continue;
            }

            foreach (var candidate in headerValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (candidate == "*"
                    || string.Equals(RemoveWeakPrefix(candidate), normalizedEntityTag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string RemoveWeakPrefix(string entityTag)
        => entityTag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? entityTag[2..] : entityTag;

    private static void AddRecord(ref ulong hash, RunRecordDto record)
    {
        AddString(ref hash, record.Id);
        AddInt64(ref hash, record.RunDate);
        AddString(ref hash, record.SteamId);
        AddString(ref hash, record.PlayerName);
        AddString(ref hash, record.MapId);
        AddInt32(ref hash, record.Style);
        AddInt32(ref hash, record.Track);
        AddInt32(ref hash, record.Stage);
        AddInt64(ref hash, record.TimeMicros);
        AddInt32(ref hash, record.Jumps);
        AddInt32(ref hash, record.Strafes);
        AddSingle(ref hash, record.Sync);
        AddSingle(ref hash, record.VelocityStartX);
        AddSingle(ref hash, record.VelocityStartY);
        AddSingle(ref hash, record.VelocityStartZ);
        AddSingle(ref hash, record.VelocityAvgX);
        AddSingle(ref hash, record.VelocityAvgY);
        AddSingle(ref hash, record.VelocityAvgZ);
        AddSingle(ref hash, record.VelocityEndX);
        AddSingle(ref hash, record.VelocityEndY);
        AddSingle(ref hash, record.VelocityEndZ);

        if (record.Checkpoints is null)
        {
            AddByte(ref hash, 0);
            return;
        }

        AddByte(ref hash, 1);
        AddInt32(ref hash, record.Checkpoints.Length);
        foreach (var checkpoint in record.Checkpoints)
        {
            AddCheckpoint(ref hash, checkpoint);
        }
    }

    private static void AddCheckpoint(ref ulong hash, RunCheckpointDto checkpoint)
    {
        AddString(ref hash, checkpoint.Id);
        AddString(ref hash, checkpoint.RecordId);
        AddUInt32(ref hash, checkpoint.CheckpointIndex);
        AddInt64(ref hash, checkpoint.TimeMicros);
        AddSingle(ref hash, checkpoint.Sync);
        AddSingle(ref hash, checkpoint.VelocityStartX);
        AddSingle(ref hash, checkpoint.VelocityStartY);
        AddSingle(ref hash, checkpoint.VelocityStartZ);
        AddSingle(ref hash, checkpoint.VelocityAvgX);
        AddSingle(ref hash, checkpoint.VelocityAvgY);
        AddSingle(ref hash, checkpoint.VelocityAvgZ);
        AddSingle(ref hash, checkpoint.VelocityMaxX);
        AddSingle(ref hash, checkpoint.VelocityMaxY);
        AddSingle(ref hash, checkpoint.VelocityMaxZ);
        AddSingle(ref hash, checkpoint.VelocityEndX);
        AddSingle(ref hash, checkpoint.VelocityEndY);
        AddSingle(ref hash, checkpoint.VelocityEndZ);
    }

    private static void AddString(ref ulong hash, string? value)
    {
        if (value is null)
        {
            AddByte(ref hash, 0);
            return;
        }

        AddByte(ref hash, 1);
        AddInt32(ref hash, value.Length);
        foreach (var character in value)
        {
            AddUInt16(ref hash, character);
        }
    }

    private static void AddSingle(ref ulong hash, float value)
        => AddUInt32(ref hash, BitConverter.SingleToUInt32Bits(value));

    private static void AddInt32(ref ulong hash, int value)
        => AddUInt32(ref hash, unchecked((uint)value));

    private static void AddInt64(ref ulong hash, long value)
        => AddUInt64(ref hash, unchecked((ulong)value));

    private static void AddUInt16(ref ulong hash, ushort value)
    {
        AddByte(ref hash, unchecked((byte)value));
        AddByte(ref hash, unchecked((byte)(value >> 8)));
    }

    private static void AddUInt32(ref ulong hash, uint value)
    {
        for (var shift = 0; shift < 32; shift += 8)
        {
            AddByte(ref hash, unchecked((byte)(value >> shift)));
        }
    }

    private static void AddUInt64(ref ulong hash, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            AddByte(ref hash, unchecked((byte)(value >> shift)));
        }
    }

    private static void AddByte(ref ulong hash, byte value)
        => hash = unchecked((hash ^ value) * FnvPrime);
}
