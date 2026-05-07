using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Payhub.Tests;

public sealed class ClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; init; } = _ => new(HttpStatusCode.OK);
        public int Calls;
        public HttpRequestMessage? LastRequest;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastRequest = request;
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(int status, string body)
    {
        var resp = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };
        resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return resp;
    }

    [Fact]
    public void RejectsBadApiKey()
    {
        Assert.Throws<ArgumentException>(() => new PayhubClient("bad"));
    }

    [Fact]
    public async Task CreateDecodesPaymentAndSetsHeaders()
    {
        var stub = new StubHandler
        {
            Respond = _ => Json(201,
                """{"id":"pay_1","status":"requires_action","psp":"sadad","psp_ref":"TXN_1","next_action":{"type":"otp_required","psp_ref":"TXN_1","masked_destination":"2189...12"},"amount_minor":4500,"currency":"LYD","merchant_order_ref":"ord-1"}"""),
        };
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://stub") };
        using var c = new PayhubClient("phk_a.b", baseUrl: "https://stub", httpClient: http, maxRetries: 0);
        var p = await c.Payments.CreateAsync(new CreatePaymentRequest
        {
            Psp = "sadad",
            MerchantOrderRef = "ord-1",
            AmountMinor = 4500,
        });
        Assert.Equal("requires_action", p.Status);
        Assert.IsType<NextAction.OtpRequired>(p.NextAction);
        Assert.NotNull(stub.LastRequest);
        Assert.Equal("Bearer phk_a.b", stub.LastRequest!.Headers.Authorization?.ToString());
        Assert.True(stub.LastRequest.Headers.Contains("Idempotency-Key"));
    }

    [Fact]
    public async Task Maps401ToAuthentication()
    {
        var stub = new StubHandler
        {
            Respond = _ => Json(401, """{"error":{"code":"hub.unauthenticated","message":"no"}}"""),
        };
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://stub") };
        using var c = new PayhubClient("phk_a.b", baseUrl: "https://stub", httpClient: http, maxRetries: 0);
        await Assert.ThrowsAsync<AuthenticationException>(() => c.Health.CheckAsync());
    }

    [Fact]
    public async Task RetriesOn503ThenSucceeds()
    {
        int n = 0;
        var stub = new StubHandler
        {
            Respond = _ => ++n == 1
                ? Json(503, """{"error":{"code":"hub.unavailable","message":"x"}}""")
                : Json(200, """{"status":"ok","psps":["sadad"]}"""),
        };
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://stub") };
        using var c = new PayhubClient("phk_a.b", baseUrl: "https://stub", httpClient: http, maxRetries: 2);
        var h = await c.Health.CheckAsync();
        Assert.Equal("ok", h.Status);
        Assert.Equal(2, stub.Calls);
    }
}
