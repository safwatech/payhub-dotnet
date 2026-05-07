// Cross-language signing vector test. Loads the canonical JSON file shared
// across every PayHub SDK and asserts the .NET implementation classifies
// each case the same way as the Python server reference.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Payhub.Tests;

public sealed class WebhookSigningVectorsTests
{
    public static IEnumerable<object[]> Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "webhook-signing.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            yield return new object[] { c.GetProperty("name").GetString()!, c.GetRawText() };
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Vector(string name, string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var c = doc.RootElement;
        byte[] secret = Convert.FromHexString(c.GetProperty("secret_hex").GetString()!);
        byte[] body = Convert.FromBase64String(c.GetProperty("body_b64").GetString()!);
        string header = c.GetProperty("header").GetString()!;
        int tolerance = c.GetProperty("tolerance_seconds").GetInt32();
        long now = c.GetProperty("now").GetInt64();
        string expect = c.GetProperty("expect").GetString()!;

        switch (expect)
        {
            case "ok":
                var ev = WebhookEvent.Verify(secret, body, header, tolerance, now);
                Assert.NotNull(ev);
                break;
            case "TimestampOutOfTolerance":
                Assert.Throws<TimestampOutOfToleranceException>(
                    () => WebhookEvent.Verify(secret, body, header, tolerance, now));
                break;
            case "InvalidSignature":
                Assert.Throws<InvalidSignatureException>(
                    () => WebhookEvent.Verify(secret, body, header, tolerance, now));
                break;
            case "MalformedHeader":
                Assert.Throws<MalformedHeaderException>(
                    () => WebhookEvent.Verify(secret, body, header, tolerance, now));
                break;
            default:
                Assert.Fail($"unknown expect: {expect} ({name})");
                break;
        }
    }

    [Fact]
    public void ValidV1ReturnsTypedPayload()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "webhook-signing.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (c.GetProperty("name").GetString() != "valid_v1") continue;
            byte[] secret = Convert.FromHexString(c.GetProperty("secret_hex").GetString()!);
            byte[] body = Convert.FromBase64String(c.GetProperty("body_b64").GetString()!);
            var ev = WebhookEvent.Verify(secret, body, c.GetProperty("header").GetString()!,
                c.GetProperty("tolerance_seconds").GetInt32(), c.GetProperty("now").GetInt64());
            Assert.Equal("evt_1", ev.Id);
            Assert.Equal("payment.succeeded", ev.Type);
            Assert.Equal("pay_1", ev.PaymentId);
            return;
        }
        Assert.Fail("valid_v1 case missing");
    }
}
