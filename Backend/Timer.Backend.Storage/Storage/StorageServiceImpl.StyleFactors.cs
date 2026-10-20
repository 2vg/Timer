using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    public async Task<IReadOnlyDictionary<int, double>> GetStyleFactorsAsync()
        => (await _db.Queryable<StyleFactorEntity>().ToListAsync(OperationCancellation)).ToDictionary(x => x.Style, x => x.Factor);

    // Upserts only: servers sharing the database may not all run every style. Without overwriteExisting a caller
    // can add styles nobody registered yet but cannot change a factor another server or an operator set.
    public async Task SaveStyleFactorsAsync(IReadOnlyDictionary<int, double> factors, bool overwriteExisting = true)
    {
        var now      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var existing = await GetStyleFactorsAsync();

        foreach (var (style, factor) in factors)
        {
            if (existing.TryGetValue(style, out var current))
            {
                if (overwriteExisting && current != factor)
                {
                    await _db.Updateable<StyleFactorEntity>()
                             .SetColumns(x => x.Factor == factor)
                             .SetColumns(x => x.UpdatedAtUnixMilliseconds == now)
                             .Where(x => x.Style == style)
                             .ExecuteCommandAsync(OperationCancellation);
                }

                continue;
            }

            try
            {
                await _db.Insertable(new StyleFactorEntity { Style = style, Factor = factor, UpdatedAtUnixMilliseconds = now })
                         .ExecuteCommandAsync(OperationCancellation);
            }
            catch (Exception ex) when (IsUniqueKeyViolation(ex))
            {
                if (!overwriteExisting)
                {
                    continue; // A concurrent registration won; keep its factor.
                }

                await _db.Updateable<StyleFactorEntity>()
                         .SetColumns(x => x.Factor == factor)
                         .SetColumns(x => x.UpdatedAtUnixMilliseconds == now)
                         .Where(x => x.Style == style)
                         .ExecuteCommandAsync(OperationCancellation);
            }
        }
    }
}
