// Typed exception hierarchy mirroring `app/core/errors.py`. Maps the
// server's `{error: {code, message, details, request_id}}` envelope plus
// HTTP status into a precise C# subclass; transport failures live under
// PayhubTransportException.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Payhub;

/// <summary>Base for every error this SDK raises.</summary>
public class PayhubException : Exception
{
    public PayhubException(string message) : base(message) { }
    public PayhubException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Server returned a non-2xx with the typed error envelope.</summary>
public class PayhubApiException : PayhubException
{
    public string Code { get; }
    public int HttpStatus { get; }
    public IReadOnlyDictionary<string, object?> Details { get; }
    public string? RequestId { get; }

    public PayhubApiException(string message, string code, int httpStatus,
                              IReadOnlyDictionary<string, object?>? details = null,
                              string? requestId = null)
        : base(BuildMessage(message, requestId))
    {
        Code = code;
        HttpStatus = httpStatus;
        Details = details ?? new Dictionary<string, object?>();
        RequestId = requestId;
    }

    private static string BuildMessage(string message, string? requestId)
        => requestId is null ? message : $"{message} [request_id={requestId}]";
}

public sealed class AuthenticationException : PayhubApiException
{
    public AuthenticationException(string m, string c, int s, IReadOnlyDictionary<string, object?>? d, string? r)
        : base(m, c, s, d, r) { }
}

public sealed class PermissionException : PayhubApiException
{
    public PermissionException(string m, string c, int s, IReadOnlyDictionary<string, object?>? d, string? r)
        : base(m, c, s, d, r) { }
}

public sealed class NotFoundException : PayhubApiException
{
    public NotFoundException(string m, string c, int s, IReadOnlyDictionary<string, object?>? d, string? r)
        : base(m, c, s, d, r) { }
}

public sealed class ValidationException : PayhubApiException
{
    public ValidationException(string m, string c, int s, IReadOnlyDictionary<string, object?>? d, string? r)
        : base(m, c, s, d, r) { }
}

public sealed class IdempotencyConflictException : PayhubApiException
{
    public IdempotencyConflictException(string m, string c, int s, IReadOnlyDictionary<string, object?>? d, string? r)
        : base(m, c, s, d, r) { }
}

public sealed class RateLimitedException : PayhubApiException
{
    public int? RetryAfter { get; }

    public RateLimitedException(string m, string c, int s, IReadOnlyDictionary<string, object?>? d, string? r, int? retryAfter)
        : base(m, c, s, d, r) { RetryAfter = retryAfter; }
}

public sealed class GatewayException : PayhubApiException
{
    public GatewayException(string m, string c, int s, IReadOnlyDictionary<string, object?>? d, string? r)
        : base(m, c, s, d, r) { }
}

public sealed class ServerException : PayhubApiException
{
    public ServerException(string m, string c, int s, IReadOnlyDictionary<string, object?>? d, string? r)
        : base(m, c, s, d, r) { }
}

/// <summary>Network / serialization problem — never reached the server cleanly.</summary>
public class PayhubTransportException : PayhubException
{
    public PayhubTransportException(string m) : base(m) { }
    public PayhubTransportException(string m, Exception inner) : base(m, inner) { }
}

public sealed class PayhubTimeoutException : PayhubTransportException
{
    public PayhubTimeoutException(string m, Exception? inner = null)
        : base("payhub: timeout: " + m, inner ?? new TimeoutException(m)) { }
}

public sealed class PayhubConnectionException : PayhubTransportException
{
    public PayhubConnectionException(string m, Exception? inner = null)
        : base("payhub: connection: " + m, inner ?? new System.IO.IOException(m)) { }
}

public sealed class PayhubDecodeException : PayhubTransportException
{
    public PayhubDecodeException(string m, Exception? inner = null)
        : base("payhub: decode: " + m, inner ?? new System.IO.IOException(m)) { }
}

internal static class ErrorMapping
{
    public static PayhubApiException FromEnvelope(int status, byte[] body, int? retryAfter)
    {
        string code = "hub.unknown";
        string message = $"HTTP {status}";
        Dictionary<string, object?> details = new();
        string? requestId = null;

        if (body is { Length: > 0 })
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                {
                    if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                        code = c.GetString() ?? code;
                    if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        message = m.GetString() ?? message;
                    if (err.TryGetProperty("request_id", out var r) && r.ValueKind == JsonValueKind.String)
                        requestId = r.GetString();
                    if (err.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in d.EnumerateObject())
                            details[prop.Name] = prop.Value.GetRawText();
                    }
                }
            }
            catch (JsonException) { /* fall through with defaults */ }
        }

        return status switch
        {
            401 => new AuthenticationException(message, code, status, details, requestId),
            403 => new PermissionException(message, code, status, details, requestId),
            404 => new NotFoundException(message, code, status, details, requestId),
            409 => new IdempotencyConflictException(message, code, status, details, requestId),
            422 => new ValidationException(message, code, status, details, requestId),
            429 => new RateLimitedException(message, code, status, details, requestId, retryAfter),
            _ => status >= 500 && status < 600
                ? code.StartsWith("gateway.", StringComparison.Ordinal)
                    ? new GatewayException(message, code, status, details, requestId)
                    : new ServerException(message, code, status, details, requestId)
                : new PayhubApiException(message, code, status, details, requestId),
        };
    }
}
