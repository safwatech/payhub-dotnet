// Discriminated NextAction sum type. The server emits an object whose
// `type` field selects one of four variants; pattern-matching is the
// canonical merchant idiom.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Payhub;

/// <summary>
/// Discriminated next_action variants. Match exhaustively:
/// <code>
/// switch (payment.NextAction)
/// {
///     case OtpRequired o: ...; break;
///     case Redirect r:    ...; break;
///     case QR q:          ...; break;
///     case Lightbox l:    ...; break;
///     case null:          ...; break;
/// }
/// </code>
/// </summary>
public abstract record NextAction
{
    /// <summary>Sadad-style flow: PSP sent an OTP to the customer's MSISDN.</summary>
    public sealed record OtpRequired(string PspRef, string MaskedDestination, string? ExpiresAt) : NextAction;

    /// <summary>HTTP redirect (T-Lync, hosted checkout). POST variants carry a form payload.</summary>
    public sealed record Redirect(string Url, string Method, IReadOnlyDictionary<string, string> Fields, string? ExpiresAt) : NextAction;

    /// <summary>Mobicash QR — display the payload as QR; reference is what the merchant polls.</summary>
    public sealed record QR(string Reference, string QrPayload, string? ExpiresAt) : NextAction;

    /// <summary>Moamalat lightbox — script_url + opaque params for the lightbox.</summary>
    public sealed record Lightbox(IReadOnlyDictionary<string, string> Params, string? ScriptUrl) : NextAction;

    internal static NextAction? FromJson(JsonElement? el)
    {
        if (el is null || el.Value.ValueKind == JsonValueKind.Null || el.Value.ValueKind == JsonValueKind.Undefined)
            return null;
        var node = el.Value;
        if (node.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("next_action must be an object or null");

        string type = node.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        return type switch
        {
            "otp_required" => new OtpRequired(
                Str(node, "psp_ref"),
                Str(node, "masked_destination"),
                NullableStr(node, "expires_at")),
            "redirect" => new Redirect(
                Str(node, "url"),
                Str(node, "method", "GET").ToUpperInvariant(),
                StrMap(node, "fields"),
                NullableStr(node, "expires_at")),
            "qr" => new QR(
                Str(node, "reference"),
                Str(node, "qr_payload"),
                NullableStr(node, "expires_at")),
            "lightbox" => BuildLightbox(node),
            _ => throw new InvalidOperationException($"unknown next_action.type: {type}"),
        };
    }

    private static Lightbox BuildLightbox(JsonElement node)
    {
        var paramMap = StrMap(node, "params");
        paramMap.TryGetValue("lightbox_js_url", out var script);
        return new Lightbox(paramMap, script);
    }

    private static string Str(JsonElement n, string key, string fallback = "")
        => n.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? fallback) : fallback;

    private static string? NullableStr(JsonElement n, string key)
        => n.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Dictionary<string, string> StrMap(JsonElement n, string key)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!n.TryGetProperty(key, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return dict;
        foreach (var prop in obj.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.Number => prop.Value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "",
                _ => prop.Value.GetRawText(),
            };
        }
        return dict;
    }
}
