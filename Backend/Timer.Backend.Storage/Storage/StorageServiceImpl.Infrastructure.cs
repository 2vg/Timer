using System;
using System.Collections.Generic;
using Sharp.Shared.Units;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    private static long ToDbSteamId(SteamID steamId) => unchecked((long)steamId.AsPrimitive());

    private static bool IsUniqueKeyViolation(Exception exception)
        => exception is MySqlConnector.MySqlException { Number: 1062 }
            or Npgsql.PostgresException { SqlState: "23505" }
           || (exception.InnerException is { } inner && IsUniqueKeyViolation(inner));

    /// <summary>
    /// Binds a map to its Steam Workshop item for the calling async flow only, so each game server's
    /// request applies its own binding. Call it at the start of the operation that names the map.
    /// </summary>
    internal void SetMapWorkshopId(string mapName, ulong workshopId)
        => _workshopMap.Value = workshopId == 0 ? null : new (ToMapKey(mapName), workshopId);

    private ulong GetWorkshopId(string mapKey)
        => _workshopMap.Value is { } workshopMap && workshopMap.MapKey == mapKey ? workshopMap.WorkshopId : 0;

    // A workshop update may have moved a cached name to another row, so a binding resolves through
    // the item once before the name cache is trusted for it.
    private bool TryGetCachedMapId(string mapKey, out ulong mapId)
    {
        mapId = 0;

        return _mapIdCache.TryGetValue(mapKey, out mapId)
               && (GetWorkshopId(mapKey) is var workshopId && (workshopId == 0 || _resolvedWorkshopMaps.ContainsKey((mapKey, workshopId))));
    }

    private void CacheMapId(string mapKey, MapEntity map)
    {
        // A read may resolve a renamed workshop file to the item's row before the map load renames it; that row
        // is not this name's yet, so caching it would let a caller-chosen item id redirect the name for everyone.
        if (!string.Equals(map.File, mapKey, StringComparison.Ordinal))
        {
            return;
        }

        _mapIdCache[mapKey] = map.MapId;

        // Only a binding the row actually carries is trusted for the name cache. A caller's unrelated item id
        // must neither skip resolution later nor add an entry per id it invents.
        if (GetWorkshopId(mapKey) is var workshopId and not 0 && map.WorkshopId == workshopId)
        {
            _resolvedWorkshopMaps[(mapKey, workshopId)] = 0;
        }
    }

    // Only loading or creating a map (mayRebind) may carry a workshop item over to a renamed file or claim a row for
    // an item; reads resolve without changing which row a name or item refers to.
    private Task<MapEntity?> FindMapAsync(string mapKey, bool mayRebind)
        => GetWorkshopId(mapKey) is var workshopId and not 0
               ? FindWorkshopMapAsync(mapKey, workshopId, mayRebind)
               : FindMapByNameAsync(mapKey);

    // A workshop map is identified by its item, not its file name: carry the item's row over to a
    // renamed file, or claim the row stored under this name.
    private async Task<MapEntity?> FindWorkshopMapAsync(string mapKey, ulong workshopId, bool mayRebind)
    {
        var itemMaps = await _db.Queryable<MapEntity>()
                                .Where(x => x.WorkshopId == workshopId)
                                .OrderBy(x => x.MapId)
                                .ToListAsync(OperationCancellation);

        if (itemMaps.Find(x => x.File == mapKey) is { } current)
        {
            return current;
        }

        var named = await FindMapByNameAsync(mapKey);

        if (!mayRebind)
        {
            // Read-only: a renamed file still finds its item's row (the plugin's first reads can race its map
            // load), but neither the name nor the item binding is written.
            return named ?? (itemMaps.Count > 0 ? itemMaps[0] : null);
        }

        if (named is null && itemMaps.Count > 0)
        {
            var renamed = itemMaps[0];

            try
            {
                await _db.Updateable<MapEntity>()
                         .SetColumns(x => x.File == mapKey)
                         .Where(x => x.MapId == renamed.MapId)
                         .ExecuteCommandAsync(OperationCancellation);

                _logger.LogInformation("Workshop item {workshopId} renamed map {old} to {map}.", workshopId, renamed.File, mapKey);
                _mapIdCache.TryRemove(renamed.File, out _);
                renamed.File = mapKey;

                return renamed;
            }
            catch (Exception ex)
            {
                // Another server created the new name meanwhile.
                named = await FindMapByNameAsync(mapKey);

                if (named is null)
                {
                    throw;
                }

                _logger.LogDebug(ex, "Rename to map {map} raced with its creation.", mapKey);
            }
        }

        if (named is null)
        {
            return null;
        }

        if (itemMaps.Count > 0)
        {
            _logger.LogWarning("Workshop item {workshopId} is stored as map {old}, but {map} has its own row; using that row.",
                               workshopId, itemMaps[0].File, mapKey);

            return named;
        }

        if (named.WorkshopId != 0)
        {
            _logger.LogInformation("Map {map} moved from workshop item {old} to {workshopId}.", mapKey, named.WorkshopId, workshopId);
        }

        await _db.Updateable<MapEntity>()
                 .SetColumns(x => x.WorkshopId == workshopId)
                 .Where(x => x.MapId == named.MapId)
                 .ExecuteCommandAsync(OperationCancellation);
        named.WorkshopId = workshopId;

        return named;
    }

    private async Task<MapEntity?> FindMapByNameAsync(string mapName)
    {
        var map = await _db.Queryable<MapEntity>()
                           .Where(x => x.File == mapName)
                           .FirstAsync(OperationCancellation);

        // MySQL's default utf8mb4_0900_ai_ci collation compares case- and accent-insensitively, so
        // the query can return a different map (surf_edge -> surf_édge). Map names are exact keys.
        return map is not null && string.Equals(map.File, mapName, StringComparison.Ordinal) ? map : null;
    }

    internal async Task<ulong?> ResolveMapIdByNameAsync(string mapName)
    {
        var mapKey = ToMapKey(mapName);

        if (TryGetCachedMapId(mapKey, out var cachedMapId))
        {
            return cachedMapId;
        }

        var mapEntity = await FindMapAsync(mapKey, mayRebind: false);

        if (mapEntity is null)
        {
            return null;
        }

        CacheMapId(mapKey, mapEntity);

        return mapEntity.MapId;
    }

    internal async Task<ulong> EnsureMapIdByNameAsync(string mapName)
    {
        var mapKey = ToMapKey(mapName);

        if (TryGetCachedMapId(mapKey, out var cachedMapId))
        {
            return cachedMapId;
        }

        // Map-creating writes may rebind like a map load: creating a second row for a renamed workshop file
        // would split the map's identity.
        return (await EnsureMapEntityByKeyAsync(mapKey, mapName, mayRebind: true)).MapId;
    }

    private async Task<MapEntity> EnsureMapEntityByKeyAsync(string mapKey, string mapName, bool mayRebind)
    {
        var mapEntity = await FindMapAsync(mapKey, mayRebind);

        if (mapEntity is null)
        {
            mapEntity = new ()
            {
                File                    = mapKey,
                Tier                    = 1,
                Stages                  = 0,
                WorkshopId              = GetWorkshopId(mapKey),
                AddedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            try
            {
                var newId = await _db.Insertable(mapEntity).ExecuteReturnBigIdentityAsync(OperationCancellation);
                mapEntity.MapId = unchecked((ulong) newId);
            }
            catch (Exception ex)
            {
                // Usually a duplicate-key race with another server inserting the same map —
                // the re-read below picks up the winning row. If the re-read finds nothing,
                // the insert genuinely failed: surface it instead of returning (and caching)
                // an unsaved entity with MapId 0.
                if (await FindMapByNameAsync(mapKey) is not { } raced)
                {
                    var collidingName = await _db.Queryable<MapEntity>()
                                                 .Where(x => x.File == mapKey)
                                                 .Select(x => x.File)
                                                 .FirstAsync(OperationCancellation);
                    if (collidingName is not null)
                    {
                        throw new InvalidOperationException(
                            $"Map '{mapKey}' cannot be created: the database collation treats it as equal to existing map '{collidingName}' (MySQL compares names case- and accent-insensitively). Rename one of them.",
                            ex);
                    }

                    _logger.LogError(ex, "Failed to insert map row for {map}", mapName);

                    throw;
                }

                _logger.LogDebug(ex, "Map insert raced for map {map}; using the winning row.", mapName);
                mapEntity = raced;
            }
        }

        CacheMapId(mapKey, mapEntity);

        return mapEntity;
    }

    private async Task SyncMapTrackTiersAsync(ulong mapId, byte[]? tiers)
    {
        await _db.Deleteable<MapTrackEntity>()
                 .Where(x => x.MapId == mapId)
                 .ExecuteCommandAsync(OperationCancellation);

        if (tiers is null || tiers.Length <= 1)
        {
            return;
        }

        var entities = new List<MapTrackEntity>(tiers.Length - 1);

        for (var index = 1; index < Math.Min(tiers.Length, MapProfile.DefaultTrackCount); index++)
        {
            var tier = tiers[index];

            if (tier == 0)
            {
                continue;
            }

            entities.Add(new ()
            {
                MapId = mapId,
                Track = (ushort) index,
                Tier  = tier,
            });
        }

        if (entities.Count > 0)
        {
            await _db.Insertable(entities).ExecuteCommandAsync(OperationCancellation);
        }
    }

    private SqlSugarScope CreateClient(DbType dbType, string connectionString) =>
        new (new ConnectionConfig
        {
            DbType                = dbType,
            ConnectionString      = connectionString,
            IsAutoCloseConnection = true,
            InitKeyType           = InitKeyType.Attribute,
            // SqlSugar's own messages are bilingual by default. This setting is process-wide.
            LanguageType          = LanguageType.English,
            // A mixed-version or incomplete entity must never delete a production
            // column merely because CodeFirst does not know about it.
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (_, entity) => entity.IsDisabledDelete = true,
            },
        }, ConfigureSqlLogging);

    private static byte GetTier(byte[]? tiers, int track)
    {
        if (tiers is null || track < 0 || track >= tiers.Length || tiers[track] == 0)
        {
            return 1;
        }

        return tiers[track];
    }

    private static ushort ToUInt16(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value >= ushort.MaxValue)
        {
            return ushort.MaxValue;
        }

        return (ushort) value;
    }

    private static uint ToUInt32(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value == int.MaxValue)
        {
            return int.MaxValue;
        }

        return (uint) value;
    }

    private static int ToInt32(uint value)
    {
        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int) value;
    }

    private static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            return 1;
        }

        return limit >= IRequestManager.DefaultRecordLimit ? IRequestManager.DefaultRecordLimit : limit;
    }

    private static string ToMapKey(string mapName)
        => mapName.ToLowerInvariant();

    public async Task<IReadOnlyList<string>> GetAllMapNamesAsync()
        => await _db.Queryable<MapEntity>()
                    .OrderBy(x => x.File)
                    .Select(x => x.File)
                    .ToListAsync(OperationCancellation);

    private sealed record WorkshopMap(string MapKey, ulong WorkshopId);

    private sealed class PlayerBestCheckRow
    {
        public ulong Id            { get; set; }
        public int   AtLeastAsFast { get; set; }
    }

}
