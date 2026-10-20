using System;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Security.Cryptography;
using Source2Surf.Timer.Backend.Rpc.Contracts;

namespace Timer.Backend.WriteApi;

internal enum TimerWriteAuthenticationMode
{
    /// <summary>No credentials; the operator isolates the write listener (firewall, AllowedNetworks).</summary>
    None,

    /// <summary>Every call carries <c>authorization: Bearer &lt;key&gt;</c> matching a configured client.</summary>
    ApiKey,
}

[Flags]
internal enum TimerWriteScopes
{
    None  = 0,

    /// <summary>Routine game-server calls: submissions, profiles, stats, settings, reads, replay links.</summary>
    Game  = 1,

    /// <summary>Destructive or global operations: deletes, wipes, tiers, ranking, recalculation, zones.</summary>
    Admin = 2,
}

internal sealed record TimerWriteClient(string Name, TimerWriteScopes Scopes);

/// <summary>
/// SHA-256 of an API key as four words, so a lookup neither allocates nor compares secrets: the key itself is never
/// stored, and matching hashes by equality leaks nothing useful about the preimage through timing.
/// </summary>
internal readonly record struct TimerWriteKeyHash(ulong W0, ulong W1, ulong W2, ulong W3)
{
    public const int MaxKeyLength = 256;

    public static TimerWriteKeyHash FromHash(ReadOnlySpan<byte> hash)
        => new (BinaryPrimitives.ReadUInt64LittleEndian(hash),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]));

    /// <summary>Hashes a presented key; false when it is empty, too long or not printable ASCII.</summary>
    public static bool TryCompute(ReadOnlySpan<char> key, out TimerWriteKeyHash hash)
    {
        hash = default;
        if (key.IsEmpty || key.Length > MaxKeyLength)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[MaxKeyLength];
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c is < '!' or > '~')
            {
                return false;
            }

            bytes[i] = (byte)c;
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes[..key.Length], digest);
        hash = FromHash(digest);
        return true;
    }
}

/// <summary>
/// The scope each write RPC needs, keyed by its gRPC method path (<c>/IService/Method</c>). A method missing here
/// (a contract added without a decision) needs <see cref="TimerWriteScopes.Admin"/>.
/// </summary>
internal static class TimerWriteMethodScopes
{
    private static readonly FrozenSet<string> AdminMethods = FrozenSet.ToFrozenSet(
    [
        nameof(ITimerStorageServiceV1.SetMapTierAsync),
        nameof(ITimerStorageServiceV1.SetMapRankedAsync),
        nameof(ITimerStorageServiceV1.RecalculateScoresAsync),
        nameof(ITimerStorageServiceV1.RemoveMapRecordsAsync),
        nameof(ITimerStorageServiceV1.DeleteRunAsync),
        nameof(ITimerStorageServiceV1.WipePlayerRunsAsync),
        nameof(ITimerStorageServiceV1.SaveZonesAsync),
    ], StringComparer.Ordinal);

    public static readonly FrozenDictionary<string, TimerWriteScopes> ByMethodPath = Build();

    public static TimerWriteScopes Required(string methodPath)
        => ByMethodPath.GetValueOrDefault(methodPath, TimerWriteScopes.Admin);

    private static FrozenDictionary<string, TimerWriteScopes> Build()
    {
        var scopes = new Dictionary<string, TimerWriteScopes>(StringComparer.Ordinal);
        foreach (var service in (ReadOnlySpan<Type>)[typeof(ITimerWriteServiceV1), typeof(ITimerStorageServiceV1)])
        {
            foreach (var method in service.GetMethods())
            {
                scopes[$"/{service.Name}/{method.Name}"] = AdminMethods.Contains(method.Name)
                                                               ? TimerWriteScopes.Admin
                                                               : TimerWriteScopes.Game;
            }
        }

        return scopes.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
