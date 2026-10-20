using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;

namespace Timer.Backend.Infrastructure;

/// <summary>
/// Replaces the framework's default output-cache policy for the public leaderboards. The default skips the cache for
/// any request carrying an Authorization header, so a client could send <c>Authorization: x</c> to force a 5,000-row
/// query per request; these endpoints never read credentials, so the header is irrelevant to the response. Like the
/// default it caches only GET/HEAD and only 200 responses without cookies.
/// </summary>
internal sealed class TimerLeaderboardCachePolicy : IOutputCachePolicy
{
    ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var method = context.HttpContext.Request.Method;
        var cacheable = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

        context.EnableOutputCaching = true;
        context.AllowCacheLookup    = cacheable;
        context.AllowCacheStorage   = cacheable;
        context.AllowLocking        = true;

        // Responses are compressed inside the cache, so each entry holds one encoding. Key on the encoding the
        // compression middleware will pick, never on the raw header, which a client could vary without limit.
        context.CacheVaryByRules.VaryByValues["encoding"] = SelectEncoding(context.HttpContext.Request.Headers.AcceptEncoding);
        return ValueTask.CompletedTask;
    }

    ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        if (!StringValues.IsNullOrEmpty(response.Headers.SetCookie) || response.StatusCode != StatusCodes.Status200OK)
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The encoding ResponseCompression would choose with its default providers (Brotli, then Gzip): the highest
    /// quality of br, gzip or *, Brotli on ties, none when neither is acceptable.
    /// </summary>
    internal static string SelectEncoding(StringValues acceptEncoding)
    {
        double brotli = -1, gzip = -1, wildcard = -1;

        foreach (var header in acceptEncoding)
        {
            if (header is null)
            {
                continue;
            }

            foreach (var range in header.AsSpan().Split(','))
            {
                var token = header.AsSpan()[range];
                var separator = token.IndexOf(';');
                var name = (separator < 0 ? token : token[..separator]).Trim();
                var quality = separator < 0 ? 1d : ParseQuality(token[(separator + 1)..]);

                if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
                {
                    brotli = Math.Max(brotli, quality);
                }
                else if (name.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    gzip = Math.Max(gzip, quality);
                }
                else if (name.SequenceEqual("*"))
                {
                    wildcard = Math.Max(wildcard, quality);
                }
            }
        }

        if (brotli < 0) brotli = wildcard;
        if (gzip < 0) gzip = wildcard;

        if (brotli > 0 && brotli >= gzip) return "br";
        if (gzip > 0) return "gzip";
        return "identity";
    }

    private static double ParseQuality(ReadOnlySpan<char> parameters)
    {
        parameters = parameters.Trim();
        if (parameters.Length < 2 || (parameters[0] is not ('q' or 'Q')) || parameters[1] != '=')
        {
            return 1d;
        }

        return double.TryParse(parameters[2..].Trim(), System.Globalization.NumberStyles.AllowDecimalPoint,
                               System.Globalization.CultureInfo.InvariantCulture, out var quality)
                   ? Math.Clamp(quality, 0d, 1d)
                   : 0d;
    }
}
