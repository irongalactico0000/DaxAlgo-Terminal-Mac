using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using TradingTerminal.Backtest.Engine.Accounting;
using TradingTerminal.Backtest.Engine.Cost;
using TradingTerminal.Backtest.Engine.Execution;
using TradingTerminal.Backtest.Engine.Stats;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Definition;
using TradingTerminal.Core.Trading;
using TradingTerminal.TradeIr.Runtime;

namespace TradingTerminal.Backtest.Engine.TradeIr;

/// <summary>
/// Minimum honest product runner for a package-valid typed graph. It materializes one deterministic
/// synthetic QuoteL1 tape, performs the closed target and exact data-admission gates, then drives the
/// real evaluator, risk gateway, simulated book, portfolio, and report builder in-process.
/// </summary>
public sealed class TradeIrSimulatedBacktestRunnerV1 : ITradeIrSimulatedBacktestRunnerV1
{
    private const double StartingCash = 100_000d;
    private const double StartPrice = 100d;
    private const double Spread = 0.02d;
    private const double TickSize = 0.01d;
    private const double ContractMultiplier = 1d;
    private const string CapabilityId = "daxalgo.synthetic.quote-l1.smoke";
    private const string AdapterId = "daxalgo.synthetic.quote-l1.smoke-adapter";
    private const int AdapterVersion = 1;
    private static readonly DateTime StartUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<TradeIrSimulatedBacktestResultV1> RunAsync(
        TradeIrSimulatedBacktestRequestV1 request,
        CancellationToken ct = default)
    {
        if (request is null)
            return Rejected(TradeIrSimulatedBacktestIssueCodesV1.RequestRequired, "$", "A smoke-run request is required.");
        if (!IsSha256(request.SourceCandidateHashSha256))
        {
            return Rejected(
                TradeIrSimulatedBacktestIssueCodesV1.SourceCandidateHashInvalid,
                "sourceCandidateHashSha256",
                "Source candidate identity must be a lowercase SHA-256 digest.");
        }
        if (!IsSha256(request.ExpectedModuleHashSha256))
        {
            return Rejected(
                TradeIrSimulatedBacktestIssueCodesV1.ModuleHashInvalid,
                "expectedModuleHashSha256",
                "Expected module identity must be a lowercase SHA-256 digest.");
        }
        if (request.EventCount is <= 0 or > TradeIrSimulatedBacktestContractV1.MaximumEventCount)
        {
            return Rejected(
                TradeIrSimulatedBacktestIssueCodesV1.EventCountInvalid,
                "eventCount",
                $"Synthetic smoke runs require 1..{TradeIrSimulatedBacktestContractV1.MaximumEventCount} events.");
        }
        if (request.Module is null)
        {
            return Rejected(
                TradeIrSimulatedBacktestIssueCodesV1.ModuleInvalid,
                "module",
                "An OperatorGraphModuleV1 is required.");
        }

        OperatorGraphModuleV1 module;
        string moduleHash;
        try
        {
            // Freeze caller-owned lists and dictionaries before the first await. The same exact
            // canonical document supplies the module identity checked below.
            var canonicalModule = OperatorGraphModuleCanonicalJsonV1.Serialize(request.Module);
            moduleHash = OperatorGraphModuleCanonicalJsonV1.Hash(request.Module);
            module = OperatorGraphModuleCanonicalJsonV1.Deserialize(canonicalModule);
        }
        catch (Exception exception) when (IsDeterministicInputException(exception))
        {
            return Rejected(
                TradeIrSimulatedBacktestIssueCodesV1.ModuleInvalid,
                "module",
                exception.Message);
        }

        if (!StringComparer.Ordinal.Equals(moduleHash, request.ExpectedModuleHashSha256))
        {
            return Rejected(
                TradeIrSimulatedBacktestIssueCodesV1.ModuleHashMismatch,
                "expectedModuleHashSha256",
                $"Expected module hash '{request.ExpectedModuleHashSha256}' does not identify the supplied canonical module '{moduleHash}'.");
        }

        var registry = StrategyOperatorRegistryV1.CreateDefault();
        var moduleValidation = TradeIrModuleValidatorV1.Validate(module, registry);
        if (!moduleValidation.IsValid)
        {
            return Rejected(moduleValidation.Issues.Select(issue => new TradeIrSimulatedBacktestIssueV1(
                TradeIrSimulatedBacktestIssueCodesV1.ModuleInvalid,
                issue.Path,
                $"{issue.Code}: {issue.Message}")));
        }

        var definition = module.Definition;
        var requirements = definition.DataRequirements.ToArray();
        if (requirements.Length != 1)
        {
            return Rejected(
                TradeIrExecutionPlanIssueCodesV1.DataRequirementCount,
                "definition.dataRequirements",
                $"The synthetic QuoteL1 smoke target requires exactly one data requirement; found {requirements.Length}.");
        }

        var requirement = requirements[0];
        if (requirement.DataKind != TradeIrDataKindV1.QuoteL1)
        {
            return Rejected(
                TradeIrExecutionPlanIssueCodesV1.DataRequirementKind,
                "definition.dataRequirements[0].dataKind",
                $"The synthetic smoke target requires QuoteL1, not '{requirement.DataKind}'.");
        }

        var instruments = requirement.InstrumentSelector.References.ToArray();
        if (instruments.Length != 1)
        {
            return Rejected(
                TradeIrExecutionPlanIssueCodesV1.PortableInstrumentCount,
                "definition.dataRequirements[0].instrumentSelector.references",
                $"The synthetic smoke target requires exactly one portable instrument; found {instruments.Length}.");
        }

        var portableInstrument = instruments[0];
        BacktestTradeIrArtifactSetV1 artifacts;
        try
        {
            var compilerHash = HashAssembly(typeof(TradeIrExecutionPlanCompilerV1).Assembly);
            var runtimeHash = HashAssembly(typeof(TradeIrEvaluatorV1).Assembly);
            var executionHostHash = HashAssembly(typeof(TradeIrSimulatedBacktestRunnerV1).Assembly);
            artifacts = new BacktestTradeIrArtifactSetV1(
                new BacktestTradeIrArtifactIdentityV1(
                    BacktestTradeIrTargetV1.CompilerArtifactId,
                    BacktestTradeIrTargetV1.ArtifactVersion,
                    compilerHash),
                new BacktestTradeIrArtifactIdentityV1(
                    BacktestTradeIrTargetV1.RuntimeArtifactId,
                    BacktestTradeIrTargetV1.ArtifactVersion,
                    runtimeHash),
                new BacktestTradeIrArtifactIdentityV1(
                    BacktestTradeIrTargetV1.ExecutionHostArtifactId,
                    BacktestTradeIrTargetV1.ArtifactVersion,
                    executionHostHash));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           NotSupportedException or ArgumentException)
        {
            return Rejected(
                TradeIrSimulatedBacktestIssueCodesV1.ArtifactIdentityUnavailable,
                "targetArtifacts",
                exception.Message);
        }

        IReadOnlyList<SyntheticQuoteV1> quotes;
        string syntheticInputHash;
        try
        {
            quotes = MaterializeQuotes(portableInstrument, request.EventCount, request.Seed);
            syntheticInputHash = HashSyntheticInput(portableInstrument, request.Seed, quotes);
        }
        catch (Exception exception) when (IsDeterministicInputException(exception))
        {
            return Rejected(
                TradeIrSimulatedBacktestIssueCodesV1.DataRequirementInvalid,
                "syntheticInput",
                exception.Message);
        }

        var schema = TradeIrSimulatedBacktestContractV1.CreateEventSchema();
        var temporal = new DataTemporalSemanticsV1(
            TradeIrEventTimeBasisV1.OccurredAtUtc,
            TradeIrTimestampPrecisionV1.Microseconds,
            TradeIrEventOrderingV1.EventTimeThenSourceSequence,
            Interval: null,
            RequireAuthoritativeEventTime: true,
            RequirePointInTimeAvailability: true);
        var capturedAt = new DateTimeOffset(StartUtc);
        var capability = new DataSourceCapabilityV1(
            CapabilityId,
            Revision: 1,
            capturedAt,
            TradeIrDataKindV1.QuoteL1,
            [portableInstrument],
            schema,
            temporal,
            TradeIrNormalizationPolicyV1.RawUnadjusted,
            TradeIrMissingDataPolicyV1.Reject,
            TradeIrRevisionPolicyV1.LatestAvailableAtDecisionTime,
            AdapterId,
            AdapterVersion,
            artifacts.ExecutionHost.ArtifactHashSha256);
        var binding = new DataBindingManifestV1(
            $"synthetic-smoke.{requirement.RequirementId}",
            requirement.RequirementId,
            capability.CapabilityId,
            capability.Revision,
            capability.CapturedAtUtc,
            capability.DataKind,
            [portableInstrument],
            schema,
            temporal,
            capability.NormalizationPolicy,
            capability.MissingDataPolicy,
            capability.RevisionPolicy,
            syntheticInputHash,
            capability.AdapterId,
            capability.AdapterVersion,
            capability.AdapterHashSha256,
            schema.SchemaHashSha256);

        TradeIrExecutionPlanCompilationResultV1 compilation;
        try
        {
            var target = BacktestTradeIrTargetV1.Create(artifacts);
            compilation = TradeIrExecutionPlanCompilerV1.Compile(
                definition,
                target,
                artifacts,
                [capability],
                [binding]);
        }
        catch (Exception exception) when (IsDeterministicInputException(exception))
        {
            return Rejected(
                TradeIrSimulatedBacktestIssueCodesV1.ModuleInvalid,
                "module.definition",
                exception.Message);
        }

        if (!compilation.Succeeded)
        {
            return Rejected(compilation.Issues.Select(issue => new TradeIrSimulatedBacktestIssueV1(
                issue.Code,
                issue.Path,
                issue.Message)));
        }

        try
        {
            return await RunAdmittedAsync(
                    request,
                    moduleHash,
                    portableInstrument,
                    quotes,
                    syntheticInputHash,
                    artifacts,
                    compilation,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new TradeIrSimulatedBacktestResultV1(
                TradeIrSimulatedBacktestStatusV1.Cancelled,
                Report: null,
                Evidence: null,
                [new TradeIrSimulatedBacktestIssueV1(
                    TradeIrSimulatedBacktestIssueCodesV1.Cancelled,
                    "$",
                    "The in-process synthetic smoke run was cancelled.")]);
        }
        catch (Exception exception)
        {
            return new TradeIrSimulatedBacktestResultV1(
                TradeIrSimulatedBacktestStatusV1.Failed,
                Report: null,
                Evidence: null,
                [new TradeIrSimulatedBacktestIssueV1(
                    TradeIrSimulatedBacktestIssueCodesV1.RuntimeFailed,
                    "runtime",
                    exception.Message)]);
        }
    }

    private static async Task<TradeIrSimulatedBacktestResultV1> RunAdmittedAsync(
        TradeIrSimulatedBacktestRequestV1 request,
        string moduleHash,
        SourceIndependentInstrumentRef portableInstrument,
        IReadOnlyList<SyntheticQuoteV1> quotes,
        string syntheticInputHash,
        BacktestTradeIrArtifactSetV1 artifacts,
        TradeIrExecutionPlanCompilationResultV1 compilation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var plan = compilation.Plan!;
        var admissionManifest = compilation.AdmissionManifest!;
        var evaluator = new TradeIrEvaluatorV1(plan);
        var instrument = new InstrumentSpec(
            new InstrumentId(1),
            ToContract(portableInstrument),
            TickSize,
            ContractMultiplier);
        var clock = new SimClock();
        var book = new SimulatedOrderBook(
            clock,
            new L1TouchFillModel(slippageTicks: 0),
            _ => TickSize);
        var portfolio = new Portfolio(
            StartingCash,
            new Dictionary<InstrumentId, double> { [instrument.Id] = ContractMultiplier },
            FeeModels.From(new CostSpec()));
        var policy = new BacktestTradeIrHostPolicyV1(
            "risk/tradeir-synthetic-smoke-v1",
            new TradingAccountId("account/tradeir-synthetic-smoke"),
            new VenueId("venue/simulated"),
            new RiskLimits(
                maximumOrderQuantity: ScaledQuantity.FromWhole(1_000_000),
                maximumAbsolutePosition: ScaledQuantity.FromWhole(1_000_000),
                maximumGrossNotional: new ScaledMoney(1_000_000_000, 0),
                minimumBuyingPower: ScaledMoney.Zero,
                maximumDailyLoss: new ScaledMoney(100_000, 0),
                maximumDrawdown: new ScaledMoney(100_000, 0),
                maximumExposureCommandsPerWindow: 100_000,
                rateLimitWindow: TimeSpan.FromMinutes(1)),
            RiskControlMode.Active,
            KillSwitchActive: false);
        using var gateway = new TradeIrRiskGatewayV1(
            admissionManifest,
            plan,
            instrument,
            policy,
            book,
            portfolio,
            clock);

        var equity = new List<EquitySample>();
        DateTime? lastSample = null;
        var peak = StartingCash;
        foreach (var quote in quotes)
        {
            ct.ThrowIfCancellationRequested();
            if ((quote.SourceSequence & 255) == 0)
                await Task.Yield();

            var tick = new Tick(
                quote.TimestampUtc,
                quote.Bid,
                quote.Ask,
                quote.BidSize,
                quote.AskSize);
            gateway.ObserveQuote(tick, quote.SourceSequence);
            ApplyFeedback(gateway, evaluator);

            var position = portfolio.SnapshotOf(instrument.Id).Quantity;
            var intent = evaluator.EvaluateQuote(new TradeIrQuoteFrameV1(
                plan.InstrumentKey,
                plan.AdmissionManifestSha256,
                quote.SourceSequence,
                quote.EventTimeUnixMicroseconds,
                quote.Bid,
                quote.Ask,
                position));
            if (intent is not null) gateway.Admit(intent);

            var accountEquity = portfolio.Equity();
            peak = Math.Max(peak, accountEquity);
            if (lastSample is null || (quote.TimestampUtc - lastSample.Value).TotalSeconds >= 60)
            {
                equity.Add(new EquitySample(
                    quote.TimestampUtc,
                    accountEquity,
                    portfolio.Cash,
                    peak > 0 ? (peak - accountEquity) / peak : 0));
                lastSample = quote.TimestampUtc;
            }
        }

        var lastQuote = quotes[^1];
        var flatten = evaluator.End(new TradeIrPortfolioFrameV1(
            plan.InstrumentKey,
            checked(lastQuote.SourceSequence + 1),
            lastQuote.EventTimeUnixMicroseconds,
            portfolio.SnapshotOf(instrument.Id).Quantity));
        if (flatten is not null) gateway.Admit(flatten);

        // Match the managed engine's end-of-run market-order flush without introducing another
        // authored data event. Equal event time is legal; source sequence remains strictly increasing.
        gateway.ObserveQuote(
            new Tick(
                lastQuote.TimestampUtc,
                lastQuote.Bid,
                lastQuote.Ask,
                lastQuote.BidSize,
                lastQuote.AskSize),
            checked(lastQuote.SourceSequence + 1));
        ApplyFeedback(gateway, evaluator);

        var finalEquity = portfolio.Equity();
        peak = Math.Max(peak, finalEquity);
        equity.Add(new EquitySample(
            lastQuote.TimestampUtc,
            finalEquity,
            portfolio.Cash,
            peak > 0 ? (peak - finalEquity) / peak : 0));
        stopwatch.Stop();

        var summary = new RunSummary(
            quotes[0].TimestampUtc,
            lastQuote.TimestampUtc,
            StartingCash,
            finalEquity,
            quotes.Count,
            stopwatch.Elapsed.TotalMilliseconds);
        var universe = Universe.Single(instrument);
        var report = ReportBuilder.Build(summary, equity, portfolio.Trades, universe);
        var runtimeReceiptHash = HashRuntimeReceipt(
            request.SourceCandidateHashSha256,
            moduleHash,
            plan,
            syntheticInputHash,
            artifacts,
            report,
            gateway);
        var evidence = new TradeIrSimulatedBacktestEvidenceV1(
            TradeIrSimulatedBacktestContractV1.ExecutionMode,
            IsWorkerIsolated: false,
            IsHistoricalData: false,
            request.SourceCandidateHashSha256,
            moduleHash,
            plan.DefinitionSha256,
            plan.AdmissionManifestSha256,
            syntheticInputHash,
            artifacts.Compiler.ArtifactHashSha256,
            artifacts.Runtime.ArtifactHashSha256,
            artifacts.ExecutionHost.ArtifactHashSha256,
            runtimeReceiptHash,
            report.Summary.EventsProcessed,
            gateway.SubmittedOrderCount);
        return new TradeIrSimulatedBacktestResultV1(
            TradeIrSimulatedBacktestStatusV1.Succeeded,
            report,
            evidence,
            []);
    }

    private static void ApplyFeedback(TradeIrRiskGatewayV1 gateway, TradeIrEvaluatorV1 evaluator)
    {
        foreach (var feedback in gateway.DrainFeedback()) evaluator.ApplyOrderFeedback(feedback);
    }

    private static IReadOnlyList<SyntheticQuoteV1> MaterializeQuotes(
        SourceIndependentInstrumentRef instrument,
        int eventCount,
        int seed)
    {
        var quotes = new SyntheticQuoteV1[eventCount];
        var random = new StableRandomV1(seed);
        var mid = StartPrice;
        const double theta = 0.01d;
        const double sigma = StartPrice * 0.001d;
        var halfSpread = Spread * 0.5d;
        for (var index = 0; index < eventCount; index++)
        {
            mid += theta * (StartPrice - mid) + sigma * ((random.NextUnit() * 2d) - 1d);
            var bid = mid - halfSpread;
            var ask = mid + halfSpread;
            if (!double.IsFinite(bid) || !double.IsFinite(ask) || bid <= 0d || ask < bid)
                throw new InvalidOperationException("The deterministic synthetic generator produced an invalid quote.");
            var timestamp = StartUtc.AddSeconds(index);
            quotes[index] = new SyntheticQuoteV1(
                instrument.InstrumentKey,
                SourceSequence: index + 1L,
                TimestampUtc: timestamp,
                EventTimeUnixMicroseconds: checked((timestamp.Ticks - DateTime.UnixEpoch.Ticks) / 10),
                bid,
                ask,
                BidSize: 100,
                AskSize: 100);
        }
        return Array.AsReadOnly(quotes);
    }

    private static string HashSyntheticInput(
        SourceIndependentInstrumentRef instrument,
        int seed,
        IReadOnlyList<SyntheticQuoteV1> quotes) =>
        ExecutableStrategyDefinitionCanonicalJson.Hash(new SyntheticInputDocumentV1(
            "tradeir/synthetic-quote-l1-smoke/v1",
            "splitmix64-v1",
            seed,
            instrument,
            quotes));

    private static string HashRuntimeReceipt(
        string sourceCandidateHash,
        string moduleHash,
        CompiledTradeIrPlanV1 plan,
        string syntheticInputHash,
        BacktestTradeIrArtifactSetV1 artifacts,
        BacktestReport report,
        TradeIrRiskGatewayV1 gateway)
    {
        var stableReport = new StableReportV1(
            report.Summary.StartUtc,
            report.Summary.EndUtc,
            Number(report.Summary.StartingCash),
            Number(report.Summary.EndingEquity),
            report.Summary.EventsProcessed,
            report.Metrics.All.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new StableMetricV1(pair.Key, Number(pair.Value)))
                .ToArray(),
            report.Trades.Select(static trade => new StableTradeV1(
                trade.Instrument.Value,
                trade.EntryUtc,
                trade.ExitUtc,
                trade.Side.ToString(),
                trade.Quantity,
                Number(trade.EntryPrice),
                Number(trade.ExitPrice),
                Number(trade.GrossPnl),
                Number(trade.Fees),
                Number(trade.MaxFavorableExcursion),
                Number(trade.MaxAdverseExcursion))).ToArray(),
            report.Equity.Select(static sample => new StableEquityV1(
                sample.TimestampUtc,
                Number(sample.Equity),
                Number(sample.Balance),
                Number(sample.Drawdown))).ToArray(),
            report.PerInstrument.Select(static item => new StableInstrumentV1(
                item.Instrument.Value,
                Number(item.NetPnl),
                item.TradeCount,
                Number(item.WinRate))).ToArray());
        var decisionHashes = gateway.Decisions.Select(ExecutionCanonicalJson.Hash).ToArray();
        return ExecutableStrategyDefinitionCanonicalJson.Hash(new RuntimeReceiptDocumentV1(
            "tradeir/synthetic-smoke-receipt/v1",
            TradeIrSimulatedBacktestContractV1.ExecutionMode,
            IsWorkerIsolated: false,
            IsHistoricalData: false,
            sourceCandidateHash,
            moduleHash,
            plan.DefinitionSha256,
            plan.AdmissionManifestSha256,
            syntheticInputHash,
            artifacts.Compiler.ArtifactHashSha256,
            artifacts.Runtime.ArtifactHashSha256,
            artifacts.ExecutionHost.ArtifactHashSha256,
            stableReport,
            decisionHashes,
            gateway.SubmittedOrderCount));
    }

    private static Contract ToContract(SourceIndependentInstrumentRef instrument)
    {
        var securityType = instrument.AssetClass switch
        {
            AssetClass.Equity => "STK",
            AssetClass.Future => "FUT",
            AssetClass.Forex => "CASH",
            AssetClass.Crypto => "CRYPTO",
            AssetClass.Option => "OPT",
            AssetClass.Index => "IND",
            _ => throw new NotSupportedException(
                $"Synthetic smoke runs do not support asset class '{instrument.AssetClass}'."),
        };
        return new Contract(
            instrument.Symbol,
            securityType,
            instrument.Venue,
            instrument.Currency,
            instrument.Venue);
    }

    private static string HashAssembly(Assembly assembly)
    {
        var location = assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            throw new IOException($"Loaded artifact '{assembly.GetName().Name}' has no hashable file location.");
        using var stream = File.OpenRead(location);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static TradeIrSimulatedBacktestResultV1 Rejected(
        string code,
        string path,
        string message) => Rejected([new TradeIrSimulatedBacktestIssueV1(code, path, message)]);

    private static TradeIrSimulatedBacktestResultV1 Rejected(
        IEnumerable<TradeIrSimulatedBacktestIssueV1> issues) => new(
        TradeIrSimulatedBacktestStatusV1.Rejected,
        Report: null,
        Evidence: null,
        issues.OrderBy(static issue => issue.Path, StringComparer.Ordinal)
            .ThenBy(static issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(static issue => issue.Message, StringComparer.Ordinal)
            .ToArray());

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsDeterministicInputException(Exception exception) =>
        exception is ArgumentException or FormatException or InvalidOperationException or
            NotSupportedException or OverflowException or System.Text.Json.JsonException;

    private static string Number(double value) => value switch
    {
        double.NaN => "NaN",
        double.PositiveInfinity => "Infinity",
        double.NegativeInfinity => "-Infinity",
        _ => value.ToString("R", CultureInfo.InvariantCulture),
    };

    private sealed record SyntheticQuoteV1(
        string InstrumentKey,
        long SourceSequence,
        DateTime TimestampUtc,
        long EventTimeUnixMicroseconds,
        double Bid,
        double Ask,
        long BidSize,
        long AskSize);

    private sealed record SyntheticInputDocumentV1(
        string SchemaVersion,
        string GeneratorVersion,
        int Seed,
        SourceIndependentInstrumentRef Instrument,
        IReadOnlyList<SyntheticQuoteV1> Quotes);

    private sealed record RuntimeReceiptDocumentV1(
        string SchemaVersion,
        string ExecutionMode,
        bool IsWorkerIsolated,
        bool IsHistoricalData,
        string SourceCandidateHashSha256,
        string ModuleHashSha256,
        string DefinitionHashSha256,
        string AdmissionManifestHashSha256,
        string SyntheticInputHashSha256,
        string CompilerArtifactHashSha256,
        string RuntimeArtifactHashSha256,
        string ExecutionHostArtifactHashSha256,
        StableReportV1 Report,
        IReadOnlyList<string> GatewayDecisionHashes,
        int SubmittedOrderCount);

    private sealed record StableReportV1(
        DateTime StartUtc,
        DateTime EndUtc,
        string StartingCash,
        string EndingEquity,
        long EventsProcessed,
        IReadOnlyList<StableMetricV1> Metrics,
        IReadOnlyList<StableTradeV1> Trades,
        IReadOnlyList<StableEquityV1> Equity,
        IReadOnlyList<StableInstrumentV1> PerInstrument);

    private sealed record StableMetricV1(string Key, string Value);

    private sealed record StableTradeV1(
        int InstrumentId,
        DateTime EntryUtc,
        DateTime ExitUtc,
        string Side,
        long Quantity,
        string EntryPrice,
        string ExitPrice,
        string GrossPnl,
        string Fees,
        string MaxFavorableExcursion,
        string MaxAdverseExcursion);

    private sealed record StableEquityV1(
        DateTime TimestampUtc,
        string Equity,
        string Balance,
        string Drawdown);

    private sealed record StableInstrumentV1(
        int InstrumentId,
        string NetPnl,
        int TradeCount,
        string WinRate);

    private sealed class StableRandomV1
    {
        private ulong _state;

        public StableRandomV1(int seed) =>
            _state = unchecked((ulong)(long)seed) ^ 0x6a09e667f3bcc909UL;

        public double NextUnit()
        {
            _state += 0x9e3779b97f4a7c15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            value ^= value >> 31;
            return (value >> 11) * (1d / 9_007_199_254_740_992d);
        }
    }
}
