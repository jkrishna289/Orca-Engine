using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wholphin.Engine.Http;

/// <summary>
/// The one outbound-JSON shape the metadata providers share.
/// </summary>
/// <remarks>
/// Providers do NOT catch here: <see cref="IProviderGate"/> is the single place failures are
/// classified and counted, so anything that goes wrong must reach it. The one thing translated is
/// 429 — the gate cannot see a status code, and being throttled must not count toward the breaker.
/// </remarks>
internal static class ProviderHttp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Gets and deserializes JSON, translating 429 into <see cref="ProviderRateLimitedException"/>.</summary>
    /// <typeparam name="T">The response shape.</typeparam>
    /// <param name="client">The metrics-instrumented HTTP client.</param>
    /// <param name="url">The absolute request URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="bearer">Optional bearer token.</param>
    /// <returns>The parsed body, or null when the provider answered a non-success status.</returns>
    public static async Task<T?> GetJsonAsync<T>(HttpClient client, string url, CancellationToken cancellationToken, string? bearer = null)
        where T : class
    {
        var (body, status) = await GetJsonWithStatusAsync<T>(client, url, cancellationToken, bearer).ConfigureAwait(false);

        // 401/403 is a bad key or an exhausted quota — OMDb answers a spent daily allowance with 401
        // and a "Request limit reached" body. Either way every subsequent call will fail too, so this
        // must reach the gate as a FAILURE and open the breaker. Treating it as "no data for this
        // title" would quietly mark thousands of rows as enriched with nothing.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new HttpRequestException($"Provider rejected the request ({(int)status}).", null, status);
        }

        return body;
    }

    /// <summary>
    /// As <see cref="GetJsonAsync{T}"/>, but also reports the status — for providers whose auth can
    /// expire mid-flight and which must tell 401 apart from "no such title".
    /// </summary>
    /// <typeparam name="T">The response shape.</typeparam>
    /// <param name="client">The metrics-instrumented HTTP client.</param>
    /// <param name="url">The absolute request URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="bearer">Optional bearer token.</param>
    /// <returns>The parsed body (null on non-success) and the status code.</returns>
    public static async Task<(T? Body, HttpStatusCode Status)> GetJsonWithStatusAsync<T>(
        HttpClient client,
        string url,
        CancellationToken cancellationToken,
        string? bearer = null)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ProviderRateLimitedException(RateLimitRetry.DelayFor(response));
        }

        // 404 is the common "we don't have this title" answer and is not a fault; neither is any
        // other non-success here, because the gate would otherwise trip a breaker on obscure titles.
        var body = response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
            : null;

        return (body, response.StatusCode);
    }

    /// <summary>Reads an integer that a provider may have encoded as a JSON string ("7" or 7).</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The parsed integer, or 0.</returns>
    public static int ToInt(string? value)
        => int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
}
