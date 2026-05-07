// PayHub SDK — public DTOs.
//
// Field names map snake_case JSON via JsonPropertyName so the public C#
// surface is idiomatic PascalCase while wire format matches the server.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Payhub;

/// <summary>Identifies a Payment Service Provider supported by PayHub.</summary>
public static class Psp
{
    public const string Sadad = "sadad";
    public const string Moamalat = "moamalat";
    public const string Mobicash = "mobicash";
    public const string Tlync = "tlync";
    public const string Adfali = "adfali";
}

/// <summary>POST /v1/payments body.</summary>
public sealed class CreatePaymentRequest
{
    [JsonPropertyName("psp")]
    public string Psp { get; set; } = "";

    [JsonPropertyName("merchant_order_ref")]
    public string MerchantOrderRef { get; set; } = "";

    [JsonPropertyName("amount_minor")]
    public long AmountMinor { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("customer")]
    public Dictionary<string, object>? Customer { get; set; }

    [JsonPropertyName("return_urls")]
    public Dictionary<string, string>? ReturnUrls { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    [JsonPropertyName("hosted_checkout")]
    public bool? HostedCheckout { get; set; }
}

/// <summary>POST /v1/payments/{id}/refund body.</summary>
public sealed class RefundRequest
{
    [JsonPropertyName("amount_minor")]
    public long? AmountMinor { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>Payment row as returned by the PayHub v1 API.</summary>
public sealed class Payment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("psp")]
    public string PspName { get; set; } = "";

    [JsonPropertyName("psp_ref")]
    public string? PspRef { get; set; }

    [JsonPropertyName("amount_minor")]
    public long AmountMinor { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("merchant_order_ref")]
    public string MerchantOrderRef { get; set; } = "";

    [JsonPropertyName("hosted_checkout_url")]
    public string? HostedCheckoutUrl { get; set; }

    /// <summary>Server-emitted next-action discriminated payload, decoded into a typed sum type.</summary>
    public NextAction? NextAction { get; set; }
}

/// <summary>GET /v1/health response.</summary>
public sealed class Health
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("psps")]
    public List<string> Psps { get; set; } = new();
}
