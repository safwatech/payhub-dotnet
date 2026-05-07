// Webhook signature verification.
//
// Algorithmic reference: app/core/signing.py. Header is
// "Hub-Signature: t=<unix>,v1=<hmac_sha256_hex>"; signed bytes are
// f"{t}.".encode() + body. Default tolerance is ±300 s.
//
// Every PayHub SDK ports the same algorithm; the canonical vectors at
// sdks/shared/test-vectors/webhook-signing.json are the spec.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Payhub;

/// <summary>Decoded webhook event body.</summary>
public sealed class WebhookEventPayload
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string PaymentId { get; init; } = "";
    public string? PrevStatus { get; init; }
    public string NewStatus { get; init; } = "";
    public string Source { get; init; } = "";
    public IReadOnlyDictionary<string, object?> Payload { get; init; } = new Dictionary<string, object?>();
    public string CreatedAt { get; init; } = "";
}

public abstract class WebhookSignatureException : PayhubException
{
    protected WebhookSignatureException(string m) : base(m) { }
}

public sealed class MalformedHeaderException : WebhookSignatureException
{
    public MalformedHeaderException(string m) : base(m) { }
}

public sealed class TimestampOutOfToleranceException : WebhookSignatureException
{
    public int SkewSeconds { get; }
    public TimestampOutOfToleranceException(int skew)
        : base($"webhook timestamp out of tolerance: {skew}s skew")
    {
        SkewSeconds = skew;
    }
}

public sealed class InvalidSignatureException : WebhookSignatureException
{
    public InvalidSignatureException(string m) : base(m) { }
}

/// <summary>Merchant-facing entry point for verifying a PayHub webhook.</summary>
public static class WebhookEvent
{
    public const int DefaultToleranceSeconds = 300;

    /// <summary>Verify a webhook delivery and return the decoded event.</summary>
    /// <exception cref="MalformedHeaderException">Header missing t= or v1=.</exception>
    /// <exception cref="TimestampOutOfToleranceException">|now - t| exceeds the tolerance.</exception>
    /// <exception cref="InvalidSignatureException">HMAC mismatch or body isn't a JSON object.</exception>
    public static WebhookEventPayload Verify(
        byte[] secret,
        byte[] body,
        string header,
        int toleranceSeconds = DefaultToleranceSeconds,
        long? now = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(header);

        var (t, v1) = ParseHeader(header);
        long wallNow = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long skew = Math.Abs(wallNow - t);
        if (skew > toleranceSeconds)
            throw new TimestampOutOfToleranceException((int)skew);

        byte[] prefix = Encoding.UTF8.GetBytes(t.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        byte[] signed = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
        if (body.Length > 0)
            Buffer.BlockCopy(body, 0, signed, prefix.Length, body.Length);
        byte[] mac = HMACSHA256.HashData(secret, signed);
        string expected = Convert.ToHexString(mac).ToLowerInvariant();

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(v1)))
            throw new InvalidSignatureException("Hub-Signature v1 does not match");

        return DecodePayload(body);
    }

    /// <summary>Convenience overload: secret/body as UTF-8 strings.</summary>
    public static WebhookEventPayload Verify(string secret, string body, string header,
                                             int toleranceSeconds = DefaultToleranceSeconds,
                                             long? now = null)
        => Verify(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body), header, toleranceSeconds, now);

    private static (long t, string v1) ParseHeader(string header)
    {
        string? t = null, v1 = null;
        foreach (var seg in header.Split(','))
        {
            int eq = seg.IndexOf('=');
            if (eq <= 0) continue;
            string key = seg[..eq].Trim();
            string val = seg[(eq + 1)..].Trim();
            if (key == "t") t = val;
            else if (key == "v1") v1 = val;
        }
        if (t is null || v1 is null)
            throw new MalformedHeaderException($"Hub-Signature missing t or v1: '{header}'");
        if (!long.TryParse(t, out long ts))
            throw new MalformedHeaderException($"Hub-Signature t is not an integer: '{t}'");
        return (ts, v1);
    }

    private static WebhookEventPayload DecodePayload(byte[] body)
    {
        if (body.Length == 0)
            return new WebhookEventPayload();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException e)
        {
            throw new InvalidSignatureException($"webhook body is not JSON: {e.Message}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidSignatureException("webhook body is not a JSON object");

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (root.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in pl.EnumerateObject())
                    payload[prop.Name] = prop.Value.GetRawText();
            }

            return new WebhookEventPayload
            {
                Id = Str(root, "id"),
                Type = Str(root, "type"),
                PaymentId = Str(root, "payment_id"),
                PrevStatus = NullableStr(root, "prev_status"),
                NewStatus = Str(root, "new_status"),
                Source = Str(root, "source"),
                Payload = payload,
                CreatedAt = Str(root, "created_at"),
            };
        }
    }

    private static string Str(JsonElement n, string key)
        => n.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static string? NullableStr(JsonElement n, string key)
        => n.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
