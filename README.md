# PayHub .NET SDK

Official PayHub SDK for .NET 8/9. Idiomatic async surface (`Task<T>` only),
auto-idempotency on mutating calls, typed exception hierarchy, and a webhook
verifier that throws on every failure mode so merchants can't forget the
unhappy path.

```
dotnet add package Payhub
```

## Quickstart — Sadad OTP

```csharp
using Payhub;

using var client = new PayhubClient(Environment.GetEnvironmentVariable("PAYHUB_API_KEY")!);

var payment = await client.Payments.CreateAsync(new CreatePaymentRequest
{
    Psp = Psp.Sadad,
    MerchantOrderRef = "ord-42",
    AmountMinor = 4500,
    Currency = "LYD",
    Customer = new() { ["msisdn"] = "218910000001", ["birth_year"] = 1990 },
});

if (payment.NextAction is NextAction.OtpRequired otp)
    Console.WriteLine($"Sadad sent OTP to {otp.MaskedDestination}");

var confirmed = await client.Payments.ConfirmOtpAsync(payment.Id, "111111");
Console.WriteLine(confirmed.Status); // "succeeded"
```

## Webhook verification (ASP.NET Core minimal API)

The single most important rule: **read the raw body bytes**, not a parsed
model. Re-serializing JSON before HMAC will corrupt the signature.

```csharp
app.MapPost("/webhooks/payhub", async (HttpContext ctx) =>
{
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    byte[] body = ms.ToArray();

    var ev = WebhookEvent.Verify(
        secret: Convert.FromHexString(Environment.GetEnvironmentVariable("PAYHUB_WEBHOOK_SECRET")!),
        body: body,
        header: ctx.Request.Headers["Hub-Signature"]!);

    // ev.Type ∈ "payment.succeeded" | "payment.failed" | "payment.expired" | "payment.refunded"
    return Results.Ok();
});
```

## Errors

| Exception | Fires on |
| --- | --- |
| `AuthenticationException` | 401 — bad API key |
| `PermissionException` | 403 |
| `NotFoundException` | 404 |
| `ValidationException` | 422 |
| `IdempotencyConflictException` | 409 — same key, different body |
| `RateLimitedException` | 429 — `RetryAfter` carried |
| `GatewayException` | 5xx + `gateway.<psp>.*` code |
| `ServerException` | other 5xx |
| `PayhubTimeoutException` | request timed out |
| `PayhubConnectionException` | TCP/TLS/DNS failure |
| `PayhubDecodeException` | server response not decodable |
| `MalformedHeaderException` | `Hub-Signature` missing `t=`/`v1=` |
| `TimestampOutOfToleranceException` | webhook clock skew > 300 s |
| `InvalidSignatureException` | webhook HMAC mismatch |

## Auto-idempotency

`CreateAsync`, `ConfirmOtpAsync`, and `RefundAsync` mint a UUIDv4
`Idempotency-Key` if the caller doesn't supply one. Caller-supplied
always wins; a 409 raises `IdempotencyConflictException`.

## Retry policy

Network + 5xx + 429 retried up to `MaxRetries` times (default 2 → up to 3
calls), honoring `Retry-After`. 4xx other than 429 is never retried.

## License

MIT — see `LICENSE`.
