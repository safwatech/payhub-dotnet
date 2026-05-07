// Async-only PayHub HTTP client. Async-only matches modern .NET expectations
// and removes the sync-over-async footgun.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Payhub;

/// <summary>
/// Synchronous PayHub client (async-only public surface).
/// Construct one instance per process and share — internally backed by a
/// long-lived <see cref="HttpClient"/>.
/// </summary>
public sealed class PayhubClient : IDisposable
{
    public const string Version = "1.0.0";
    public const string DefaultBaseUrl = "https://app.payhub.ly";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public const int DefaultMaxRetries = 2;

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly int _maxRetries;
    private readonly string _userAgent;
    private readonly Random _rand = new();

    public PaymentsResource Payments { get; }
    public HealthResource Health { get; }

    public PayhubClient(
        string apiKey,
        string? baseUrl = null,
        TimeSpan? timeout = null,
        int? maxRetries = null,
        HttpClient? httpClient = null,
        string? userAgentSuffix = null)
    {
        if (apiKey is null) throw new ArgumentNullException(nameof(apiKey));
        if (!apiKey.StartsWith("phk_", StringComparison.Ordinal))
            throw new ArgumentException("PayHub API key must start with 'phk_'", nameof(apiKey));

        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _maxRetries = maxRetries ?? DefaultMaxRetries;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = timeout ?? DefaultTimeout };
        if (httpClient is null && timeout is not null)
            _http.Timeout = timeout.Value;

        var arch = RuntimeInformation.OSDescription.Replace(' ', '_');
        var baseUa = $"payhub-dotnet/{Version} ({RuntimeInformation.FrameworkDescription.Trim()}; {arch})";
        _userAgent = userAgentSuffix is null ? baseUa : $"{baseUa} {userAgentSuffix}";

        Payments = new PaymentsResource(this);
        Health = new HealthResource(this);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal async Task<byte[]> RequestRawAsync(
        HttpMethod method,
        string path,
        object? body,
        string? idempotencyKey,
        bool retriable,
        CancellationToken ct)
    {
        byte[]? bodyBytes = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonOptions);
        int attempts = retriable ? Math.Max(1, _maxRetries + 1) : 1;
        Exception? lastErr = null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            using var req = new HttpRequestMessage(method, _baseUrl + path);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (idempotencyKey is not null)
                req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            if (bodyBytes is not null)
            {
                req.Content = new ByteArrayContent(bodyBytes);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            HttpResponseMessage? resp = null;
            try
            {
                resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                lastErr = new PayhubTimeoutException(e.Message, e);
            }
            catch (HttpRequestException e)
            {
                lastErr = new PayhubConnectionException(e.Message, e);
            }

            if (resp is null)
            {
                if (retriable && attempt + 1 < attempts)
                {
                    await Task.Delay(BackoffMs(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw lastErr!;
            }

            using (resp)
            {
                int status = (int)resp.StatusCode;
                byte[] respBody = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (status is >= 200 and < 300)
                    return respBody;

                int? retryAfter = null;
                if (resp.Headers.RetryAfter is { Delta: { } d }) retryAfter = (int)d.TotalSeconds;
                else if (resp.Headers.TryGetValues("Retry-After", out var raVals))
                {
                    foreach (var v in raVals)
                        if (int.TryParse(v, out int n)) { retryAfter = n; break; }
                }

                var apiErr = ErrorMapping.FromEnvelope(status, respBody, retryAfter);
                if (retriable && (status >= 500 || status == 429) && attempt + 1 < attempts)
                {
                    int waitMs = retryAfter is { } sec ? sec * 1000 : BackoffMs(attempt);
                    await Task.Delay(waitMs, ct).ConfigureAwait(false);
                    lastErr = apiErr;
                    continue;
                }
                throw apiErr;
            }
        }
        throw lastErr ?? new PayhubException("payhub: unreachable retry loop");
    }

    private int BackoffMs(int attempt)
    {
        long b = 500L * (1L << attempt);
        double jitter = 0.8 + _rand.NextDouble() * 0.4;
        return (int)(b * jitter);
    }

    internal static T DecodeJson<T>(byte[] body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                ?? throw new PayhubDecodeException("decoded null response");
        }
        catch (JsonException e)
        {
            throw new PayhubDecodeException(e.Message, e);
        }
    }

    /// <summary>Routes for <c>/v1/payments/*</c>.</summary>
    public sealed class PaymentsResource
    {
        private readonly PayhubClient _c;
        internal PaymentsResource(PayhubClient c) => _c = c;

        public async Task<Payment> CreateAsync(CreatePaymentRequest request, string? idempotencyKey = null, CancellationToken ct = default)
        {
            string key = idempotencyKey ?? Guid.NewGuid().ToString();
            var raw = await _c.RequestRawAsync(HttpMethod.Post, "/v1/payments", request, key, retriable: true, ct).ConfigureAwait(false);
            return DecodePayment(raw);
        }

        public async Task<Payment> ConfirmOtpAsync(string paymentId, string code, string? idempotencyKey = null, CancellationToken ct = default)
        {
            string key = idempotencyKey ?? Guid.NewGuid().ToString();
            var body = new Dictionary<string, string> { ["code"] = code };
            var raw = await _c.RequestRawAsync(HttpMethod.Post, $"/v1/payments/{paymentId}/otp", body, key, retriable: true, ct).ConfigureAwait(false);
            return DecodePayment(raw);
        }

        public async Task<Payment> RefundAsync(string paymentId, long? amountMinor = null, string? reason = null, string? idempotencyKey = null, CancellationToken ct = default)
        {
            string key = idempotencyKey ?? Guid.NewGuid().ToString();
            var body = new RefundRequest { AmountMinor = amountMinor, Reason = reason };
            var raw = await _c.RequestRawAsync(HttpMethod.Post, $"/v1/payments/{paymentId}/refund", body, key, retriable: true, ct).ConfigureAwait(false);
            return DecodePayment(raw);
        }

        public async Task<Payment> RetrieveAsync(string paymentId, CancellationToken ct = default)
        {
            var raw = await _c.RequestRawAsync(HttpMethod.Get, $"/v1/payments/{paymentId}", null, null, retriable: true, ct).ConfigureAwait(false);
            return DecodePayment(raw);
        }

        private static Payment DecodePayment(byte[] raw)
        {
            // JsonSerializer doesn't know how to decode our discriminated NextAction —
            // do it manually so the public surface is a sum type, not a flat class.
            var p = DecodeJson<Payment>(raw);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("next_action", out var na) && na.ValueKind != JsonValueKind.Null)
                p.NextAction = NextAction.FromJson(na);
            return p;
        }
    }

    /// <summary>Routes for <c>/v1/health</c>.</summary>
    public sealed class HealthResource
    {
        private readonly PayhubClient _c;
        internal HealthResource(PayhubClient c) => _c = c;

        public async Task<Health> CheckAsync(CancellationToken ct = default)
        {
            var raw = await _c.RequestRawAsync(HttpMethod.Get, "/v1/health", null, null, retriable: true, ct)
                .ConfigureAwait(false);
            return DecodeJson<Health>(raw);
        }
    }
}
