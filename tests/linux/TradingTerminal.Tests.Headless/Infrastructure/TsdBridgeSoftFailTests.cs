using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Infrastructure.TsdBridge;
using Xunit;

namespace TradingTerminal.Tests.Headless.Infrastructure;

/// <summary>
/// E1 SoftFail: unreachable TSD must not throw — Model A keeps DaxAlgo OMS usable.
/// </summary>
public sealed class TsdBridgeSoftFailTests
{
    [Fact]
    public async Task SoftFail_unreachable_status_returns_false_without_throwing()
    {
        var client = MakeClient(
            softFail: true,
            handler: new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var reachable = await client.IsReachableAsync(CancellationToken.None);

        reachable.Should().BeFalse();
    }

    [Fact]
    public async Task SoftFail_unreachable_signals_return_empty_without_throwing()
    {
        var client = MakeClient(
            softFail: true,
            handler: new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var signals = await client.GetSignalsAsync(0, CancellationToken.None);
        var intents = await client.GetPendingIntentsAsync(CancellationToken.None);

        signals.Should().BeEmpty();
        intents.Should().BeEmpty();
    }

    [Fact]
    public async Task SoftFail_fill_mirror_failure_is_swallowed()
    {
        var client = MakeClient(
            softFail: true,
            handler: new StubHandler(_ => throw new HttpRequestException("connection refused")));

        var act = async () => await client.PostFillAsync(
            new BridgeFillDto
            {
                Symbol = "AAPL",
                Exchange = "ALPACA_PAPER",
                Side = "BUY",
                Quantity = 1,
                Price = 100,
            },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Reachable_when_bridge_status_returns_200()
    {
        var client = MakeClient(
            softFail: true,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"execution_mode":"signal","signal_mode":true}"""),
            }));

        (await client.IsReachableAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Live_shaped_signal_json_becomes_pending_confirm_in_store()
    {
        // Shape matches live POST /api/bridge/signals → GET /api/bridge/signals (snake_case).
        const string payload = """
            {
              "signals": [
                {
                  "signal_id": "sig_aa7e5f34aff4",
                  "kind": "long",
                  "strength": 0.8,
                  "symbol": "AAPL",
                  "exchange": "ALPACA_PAPER",
                  "quantity": 1.0,
                  "side": "BUY",
                  "note": "e1-live-check",
                  "created_at_ms": 1789471607926,
                  "mark_price": 190.5,
                  "source": "agent"
                }
              ],
              "signal_mode": true
            }
            """;

        var client = MakeClient(
            softFail: true,
            handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            }));

        var signals = await client.GetSignalsAsync(0, CancellationToken.None);
        signals.Should().ContainSingle();
        var s = signals[0];
        s.SignalId.Should().Be("sig_aa7e5f34aff4");
        s.Symbol.Should().Be("AAPL");
        s.Kind.Should().Be("long");
        s.MarkPrice.Should().Be(190.5);

        var store = new TsdPendingConfirmStore();
        store.Upsert(new TsdPendingConfirm(
            Id: s.SignalId,
            Kind: "signal",
            Symbol: s.Symbol,
            Exchange: s.Exchange,
            Side: s.Side,
            Quantity: s.Quantity,
            Strength: s.Strength,
            Note: s.Note,
            CreatedAtMs: s.CreatedAtMs,
            SignalKind: StrategySignalKind.Long,
            MarkPrice: s.MarkPrice));

        var snap = store.Snapshot();
        snap.Should().ContainSingle();
        snap[0].Id.Should().Be("sig_aa7e5f34aff4");
        snap[0].Symbol.Should().Be("AAPL");
        snap[0].Exchange.Should().Be("ALPACA_PAPER");
        snap[0].Quantity.Should().Be(1.0);
        snap[0].MarkPrice.Should().Be(190.5);
        snap[0].SignalKind.Should().Be(StrategySignalKind.Long);

        store.Acknowledge("sig_aa7e5f34aff4");
        store.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Live_dead_port_soft_fails_without_throwing()
    {
        // Real TCP refuse (not a stub) — proves SoftFail against an offline sidecar.
        using var http = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:18000/"),
            Timeout = TimeSpan.FromSeconds(2),
        };
        var options = StubOptions(enabled: true, softFail: true, baseUrl: "http://127.0.0.1:18000");
        var client = new TsdBridgeClient(http, options, NullLogger<TsdBridgeClient>.Instance);

        var reachable = await client.IsReachableAsync(CancellationToken.None);
        var signals = await client.GetSignalsAsync(0, CancellationToken.None);

        reachable.Should().BeFalse();
        signals.Should().BeEmpty();
    }

    private static TsdBridgeClient MakeClient(bool softFail, HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000/") };
        return new TsdBridgeClient(
            http,
            StubOptions(enabled: true, softFail: softFail, baseUrl: "http://127.0.0.1:8000"),
            NullLogger<TsdBridgeClient>.Instance);
    }

    private static IOptionsMonitor<TsdBridgeOptions> StubOptions(bool enabled, bool softFail, string baseUrl)
    {
        var opts = new TsdBridgeOptions
        {
            Enabled = enabled,
            SoftFail = softFail,
            BaseUrl = baseUrl,
            PollIntervalSeconds = 2,
        };
        var monitor = Substitute.For<IOptionsMonitor<TsdBridgeOptions>>();
        monitor.CurrentValue.Returns(opts);
        return monitor;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(factory(request));
    }
}
