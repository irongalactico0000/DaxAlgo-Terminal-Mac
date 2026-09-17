using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Core.Strategies.Generation;

/// <summary>
/// One host-owned chart overlay the chat may offer. Type ids and drawing shapes are frozen here so
/// the model cannot invent an unsupported indicator contract for famous overlays already present on
/// the native chart (SMA / EMA / RSI / MACD) or for the Bollinger request path.
/// </summary>
public sealed record AuthoredChartOverlayChoiceV1(
    string Id,
    string DisplayName,
    string ShortPromptHint,
    IReadOnlyList<string> Aliases,
    Func<AuthoredChartCompositionV1, AuthoredChartCompositionV1> Apply);

/// <summary>
/// A research-scan template offered in chat. These label or search historical windows; they do not
/// unlock Paper by themselves and must never be confused with a trading rule.
/// </summary>
public sealed record AuthoredResearchScanChoiceV1(
    string Id,
    string DisplayName,
    string ShortPromptHint,
    IReadOnlyList<string> Aliases,
    string ClarificationHint);

/// <summary>
/// Result of matching free-form chat text against the host catalog.
/// </summary>
public sealed record AuthoredChartChoiceResolutionV1(
    IReadOnlyList<AuthoredChartOverlayChoiceV1> Overlays,
    IReadOnlyList<AuthoredResearchScanChoiceV1> ResearchScans,
    bool NeedsClarification,
    string? ClarificationQuestion)
{
    public bool HasOverlays => Overlays.Count > 0;
    public bool HasResearchScans => ResearchScans.Count > 0;
}

/// <summary>
/// Shell event: Strategy Builder resolved host catalog overlays and wants the live chart to preview them.
/// </summary>
public sealed class HostChartOverlayPreviewRequestedEventArgs : EventArgs
{
    public HostChartOverlayPreviewRequestedEventArgs(
        IReadOnlyList<string> overlayIds,
        bool startResearchCapture = false,
        string? preferredSymbol = null,
        ResearchOutcomeGalleryMatchV1? galleryMatch = null,
        DateTime? historyFromUtc = null,
        DateTime? historyToUtc = null,
        BarSize? historyBarSize = null,
        ResearchChartSelectionV1? researchSelection = null,
        IReadOnlyList<ResearchConditionHitV1>? conditionHits = null,
        IReadOnlyList<ValidationChartFillV1>? fillHits = null)
    {
        OverlayIds = overlayIds ?? Array.Empty<string>();
        StartResearchCapture = startResearchCapture;
        PreferredSymbol = string.IsNullOrWhiteSpace(preferredSymbol)
            ? galleryMatch?.CanonicalSymbol ?? researchSelection?.CanonicalSymbol
            : preferredSymbol.Trim();
        GalleryMatch = galleryMatch;
        HistoryFromUtc = historyFromUtc;
        HistoryToUtc = historyToUtc;
        HistoryBarSize = historyBarSize;
        ResearchSelection = researchSelection;
        ConditionHits = conditionHits ?? Array.Empty<ResearchConditionHitV1>();
        FillHits = fillHits ?? Array.Empty<ValidationChartFillV1>();
    }

    public IReadOnlyList<string> OverlayIds { get; }

    /// <summary>
    /// When true, Charts enters observation→outcome brush mode after history loads (research scans).
    /// </summary>
    public bool StartResearchCapture { get; }

    /// <summary>Optional symbol to select on the live Charts window (gallery match handoff).</summary>
    public string? PreferredSymbol { get; }

    /// <summary>When set, Charts loads this gallery event's windows for capture review/send.</summary>
    public ResearchOutcomeGalleryMatchV1? GalleryMatch { get; }

    /// <summary>Optional explicit history window for similar-history A→B open.</summary>
    public DateTime? HistoryFromUtc { get; }

    public DateTime? HistoryToUtc { get; }

    public BarSize? HistoryBarSize { get; }

    /// <summary>
    /// When set (e.g. condition-search hit), Charts loads this observation→outcome as the current
    /// Research space capture — same path as gallery focus.
    /// </summary>
    public ResearchChartSelectionV1? ResearchSelection { get; }

    /// <summary>
    /// Bars where a research/Design condition fired — drawn as markers on the Research chart.
    /// Empty when the preview is overlays/selection only.
    /// </summary>
    public IReadOnlyList<ResearchConditionHitV1> ConditionHits { get; }

    /// <summary>
    /// Simulated Validate fills — drawn as a separate layer from <see cref="ConditionHits"/>.
    /// Empty when no last-run trades were stashed.
    /// </summary>
    public IReadOnlyList<ValidationChartFillV1> FillHits { get; }
}

public enum ResearchMarketStructureViewKind
{
    OrderBook = 0,
    VolumeFootprint = 1,
    Bookmap = 2,
}

/// <summary>
/// Shell event: Research Studio selected an instrument and wants an existing market-structure view opened.
/// </summary>
public sealed class HostResearchMarketStructureRequestedEventArgs : EventArgs
{
    public HostResearchMarketStructureRequestedEventArgs(
        ResearchMarketStructureViewKind viewKind,
        string canonicalSymbol)
    {
        ViewKind = viewKind;
        CanonicalSymbol = (canonicalSymbol ?? string.Empty).Trim();
    }

    public ResearchMarketStructureViewKind ViewKind { get; }
    public string CanonicalSymbol { get; }
}

/// <summary>
/// Maps host chat-catalog overlay ids onto the native Charts toggles without requiring Avalonia.
/// </summary>
public readonly record struct NativeChartOverlaySelectionV1(
    bool ShowSma,
    bool ShowEma,
    int EmaPeriod,
    bool ShowRsi,
    bool ShowMacd,
    bool ShowBollinger,
    bool ShowStochastic = false,
    bool ShowAtr = false,
    bool ShowVwap = false,
    bool ShowAdx = false,
    int SmaPeriod = 20)
{
    public static NativeChartOverlaySelectionV1 FromHostOverlayIds(IEnumerable<string> overlayIds)
    {
        ArgumentNullException.ThrowIfNull(overlayIds);
        var ids = overlayIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ids.Count == 0 || (ids.Count == 1 && ids.Contains("candles")))
            return new(false, false, 50, false, false, false);

        var showEma20 = ids.Contains("ema-20");
        var showEma50 = ids.Contains("ema-50");
        var emaFromSuffix = TryParsePeriodId(ids, "ema-", out var emaParsed);
        var showEma = showEma20 || showEma50 || ids.Contains("ema") || emaFromSuffix;
        // Prefer explicit 20/50 catalog ids; otherwise use ema-{n} or default 50.
        var emaPeriod = showEma20 && !showEma50
            ? 20
            : showEma50
                ? 50
                : emaFromSuffix
                    ? emaParsed
                    : 50;

        var showSma20 = ids.Contains("sma-20");
        var smaFromSuffix = TryParsePeriodId(ids, "sma-", out var smaParsed);
        var showSma = showSma20 || ids.Contains("sma") || smaFromSuffix;
        var smaPeriod = showSma20
            ? 20
            : smaFromSuffix
                ? smaParsed
                : 20;

        return new(
            ShowSma: showSma,
            ShowEma: showEma,
            EmaPeriod: emaPeriod,
            ShowRsi: ids.Contains("rsi-14") || ids.Contains("rsi"),
            ShowMacd: ids.Contains("macd-12-26-9") || ids.Contains("macd"),
            ShowBollinger: ids.Contains("bollinger-20") || ids.Contains("bollinger"),
            ShowStochastic: ids.Contains("stochastic-14-3-3") || ids.Contains("stochastic") || ids.Contains("stoch"),
            ShowAtr: ids.Contains("atr-14") || ids.Contains("atr"),
            ShowVwap: ids.Contains("vwap") || ids.Contains("vwap-session"),
            ShowAdx: ids.Contains("adx-14") || ids.Contains("adx"),
            SmaPeriod: smaPeriod);
    }

    private static bool TryParsePeriodId(HashSet<string> ids, string prefix, out int period)
    {
        foreach (var id in ids)
        {
            if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var rest = id[prefix.Length..];
            if (int.TryParse(rest, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out period) &&
                period is >= 2 and <= 500)
                return true;
        }

        period = 0;
        return false;
    }

    /// <summary>
    /// Inverse of <see cref="FromHostOverlayIds"/> for research handoff: persist what the operator
    /// actually had on the chart when the observation/outcome windows were sent.
    /// </summary>
    public IReadOnlyList<string> ToHostOverlayIds()
    {
        var ids = new List<string>();
        if (ShowSma)
            ids.Add(SmaPeriod == 20 ? "sma-20" : $"sma-{Math.Max(2, SmaPeriod)}");
        if (ShowEma)
            ids.Add(EmaPeriod <= 20 ? "ema-20" : EmaPeriod == 50 ? "ema-50" : $"ema-{EmaPeriod}");
        if (ShowRsi) ids.Add("rsi-14");
        if (ShowMacd) ids.Add("macd-12-26-9");
        if (ShowBollinger) ids.Add("bollinger-20");
        if (ShowStochastic) ids.Add("stochastic-14-3-3");
        if (ShowAtr) ids.Add("atr-14");
        if (ShowVwap) ids.Add("vwap");
        if (ShowAdx) ids.Add("adx-14");
        return ids.Count == 0 ? Array.AsReadOnly(new[] { "candles" }) : ids.AsReadOnly();
    }

    /// <summary>
    /// Default overlays applied after research capture labeling (B/C/N) and gallery focus.
    /// </summary>
    public static IReadOnlyList<string> DefaultResearchCaptureOverlayIds { get; } =
        Array.AsReadOnly(["ema-20", "rsi-14", "atr-14"]);
}

/// <summary>
/// Host catalog for chat-driven chart overlays and research scans. Keep this list small and exact:
/// every overlay must produce a launch-valid <see cref="AuthoredChartCompositionV1"/>.
/// </summary>
public static class AuthoredChartChoiceCatalogV1
{
    public const string ChoiceClarificationPrefix = "Choose chart overlays or research scans";

    public static IReadOnlyList<AuthoredChartOverlayChoiceV1> Overlays { get; } =
    [
        new(
            "candles",
            "Candles only",
            "price candles",
            ["candle", "candles", "ohlc", "price only"],
            EnsureCandles),
        new(
            "sma-20",
            "SMA 20",
            "SMA(20)",
            ["sma", "sma20", "sma 20", "simple moving average"],
            drawing => AddIndicatorLine(drawing, "sma-20", "indicator.sma@1", "20")),
        new(
            "ema-20",
            "EMA 20",
            "EMA(20)",
            ["ema", "ema20", "ema 20", "exponential moving average"],
            drawing => AddIndicatorLine(drawing, "ema-20", "indicator.ema@1", "20")),
        new(
            "ema-50",
            "EMA 50",
            "EMA(50)",
            ["ema50", "ema 50"],
            drawing => AddIndicatorLine(drawing, "ema-50", "indicator.ema@1", "50")),
        new(
            "rsi-14",
            "RSI 14",
            "RSI(14)",
            ["rsi", "rsi14", "rsi 14", "relative strength"],
            drawing => AddOscillator(drawing, "rsi", "rsi-14", "indicator.rsi@1", "14", AuthoredChartLayerKindV1.IndicatorLine)),
        new(
            "macd-12-26-9",
            "MACD 12·26·9",
            "MACD",
            ["macd", "macd 12", "macd12"],
            drawing => AddMacd(drawing)),
        new(
            "bollinger-20",
            "Bollinger 20",
            "Bollinger Bands(20)",
            ["bollinger", "bollinger bands", "bbands", "bb"],
            drawing => AddBollinger(drawing)),
        new(
            "stochastic-14-3-3",
            "Stochastic 14·3·3",
            "Stochastic oscillator",
            ["stochastic", "stoch", "stochastics"],
            drawing => AddOscillator(drawing, "stoch", "stoch-k", "indicator.stochastic@1", "14", AuthoredChartLayerKindV1.IndicatorLine)),
        new(
            "atr-14",
            "ATR 14",
            "ATR(14)",
            ["atr", "atr14", "atr 14", "average true range"],
            drawing => AddOscillator(drawing, "atr", "atr-14", "indicator.atr@1", "14", AuthoredChartLayerKindV1.IndicatorLine)),
        new(
            "vwap",
            "VWAP",
            "session VWAP",
            ["vwap", "volume weighted"],
            drawing => AddIndicatorLine(drawing, "vwap", "indicator.vwap@1", "1")),
        new(
            "adx-14",
            "ADX 14",
            "ADX(14)",
            ["adx", "adx14", "adx 14", "average directional"],
            drawing => AddOscillator(drawing, "adx", "adx-14", "indicator.adx@1", "14", AuthoredChartLayerKindV1.IndicatorLine)),
    ];

    public static IReadOnlyList<AuthoredResearchScanChoiceV1> ResearchScans { get; } =
    [
        new(
            "next-day-plus-5",
            "Next-day +5% gallery",
            "charts that rose ≥5% the next day",
            ["+5%", "5%", "next day", "next-day", "plus 5", "rose 5"],
            "This is a research scan, not a trading rule. The host opens the research chart and scans local history (daily preferred, then 1H) for next-bar ≥+5% events. Label B/C/N after reviewing a gallery hit or a manual brush."),
        new(
            "pre-breakout",
            "Pre-breakout windows",
            "windows just before a breakout",
            ["pre-breakout", "pre breakout", "before breakout", "돌파 직전"],
            "Research scan: host gallery looks for tight prior ranges followed by a ≥+3% next day. Capture or accept a hit, then label — not Paper."),
        new(
            "pre-crash",
            "Pre-crash windows",
            "windows just before a crash",
            ["pre-crash", "pre crash", "before crash", "폭락 직전"],
            "Research scan: host gallery looks for next-bar ≤−5% events on local history (daily preferred, then 1H). Label after review — not Paper."),
    ];

    public static IReadOnlyList<AuthoredChartOverlayChoiceV1> MergeWithUserIndicators(
        IEnumerable<UserChartIndicatorDefinitionV1>? userIndicators)
    {
        if (userIndicators is null)
            return Overlays;

        var extras = new List<AuthoredChartOverlayChoiceV1>();
        foreach (var user in UserChartIndicatorCatalogV1.Validate(userIndicators))
        {
            if (Overlays.Any(item => string.Equals(item.Id, user.Id, StringComparison.Ordinal)))
                continue;
            var aliases = string.IsNullOrWhiteSpace(user.Alias)
                ? Array.Empty<string>()
                : new[] { user.Alias! };
            extras.Add(new AuthoredChartOverlayChoiceV1(
                user.Id,
                user.DisplayName,
                $"{user.Kind}({user.Period})",
                aliases,
                drawing => user.Kind is UserChartIndicatorKindV1.Rsi or UserChartIndicatorKindV1.Atr
                    ? AddOscillator(
                        drawing,
                        user.Kind.ToString().ToLowerInvariant(),
                        user.Id,
                        UserChartIndicatorCatalogV1.ToAuthoredTypeId(user.Kind),
                        user.Period.ToString(CultureInfo.InvariantCulture),
                        AuthoredChartLayerKindV1.IndicatorLine)
                    : AddIndicatorLine(
                        drawing,
                        user.Id,
                        UserChartIndicatorCatalogV1.ToAuthoredTypeId(user.Kind),
                        user.Period.ToString(CultureInfo.InvariantCulture))));
        }

        return extras.Count == 0 ? Overlays : Overlays.Concat(extras).ToArray();
    }

    /// <summary>
    /// Matches named overlays/scans in free text. When the user asks for “famous indicators”
    /// without naming any, returns a host clarification listing the catalog.
    /// </summary>
    public static AuthoredChartChoiceResolutionV1 Resolve(
        string requestText,
        string? latestReply = null,
        IReadOnlyList<UserChartIndicatorDefinitionV1>? userIndicators = null)
    {
        var overlayCatalog = MergeWithUserIndicators(userIndicators);
        var haystack = string.IsNullOrWhiteSpace(latestReply)
            ? requestText ?? string.Empty
            : $"{requestText}\n{latestReply}";
        var normalized = Normalize(haystack);

        var overlays = MatchOverlays(normalized, latestReply, overlayCatalog);
        var scans = MatchResearchScans(normalized);

        // Ask for concrete overlays even when a research-scan phrase is already present in the
        // thread (for example "+5% next day" plus "do you have indicators?").
        if (overlays.Count == 0 && AsksForFamousIndicators(normalized))
        {
            return new AuthoredChartChoiceResolutionV1(
                [],
                scans,
                NeedsClarification: true,
                ClarificationQuestion: BuildOverlayClarificationQuestion(overlayCatalog));
        }

        if (overlays.Count == 0 &&
            scans.Count == 0 &&
            AsksForResearchGallery(normalized))
        {
            return new AuthoredChartChoiceResolutionV1(
                [],
                [],
                NeedsClarification: true,
                ClarificationQuestion: BuildResearchClarificationQuestion());
        }

        return new AuthoredChartChoiceResolutionV1(
            overlays,
            scans,
            NeedsClarification: false,
            ClarificationQuestion: null);
    }

    public static AuthoredChartCompositionV1 ComposeDrawing(
        IEnumerable<AuthoredChartOverlayChoiceV1> overlays)
    {
        var drawing = EnsureCandles(EmptyPriceDrawing());
        foreach (var overlay in overlays)
        {
            if (string.Equals(overlay.Id, "candles", StringComparison.Ordinal))
                continue;
            drawing = overlay.Apply(drawing);
        }

        return drawing;
    }

    /// <summary>
    /// Builds a launch-valid display-only specification from host catalog overlays without calling a
    /// model. Returns null when candidates or overlays are missing, or when launch validation fails.
    /// </summary>
    public static AuthoredUnitSpecificationV1? TryCreateHostVisualizerSpecification(
        string unitId,
        string rawRequest,
        IReadOnlyList<AuthoredUnitInstrumentCandidateV1> candidates,
        IReadOnlyList<AuthoredChartOverlayChoiceV1> overlays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawRequest);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(overlays);
        if (candidates.Count == 0 || overlays.Count == 0)
            return null;

        var instrument = candidates[0];
        if (instrument.AvailableBrokers is not { Count: > 0 })
            return null;

        var namedOverlays = overlays
            .Where(static item => !string.Equals(item.Id, "candles", StringComparison.Ordinal))
            .Select(static item => item.DisplayName)
            .ToArray();
        var name = namedOverlays.Length == 0
            ? $"{instrument.CanonicalSymbol} candles"
            : $"{instrument.CanonicalSymbol} · {string.Join(" + ", namedOverlays)}";

        var specification = StrategyInteractionBindingsFactoryV1.UpgradeTrustedSpecification(
            new AuthoredUnitSpecificationV1(
            AuthoredUnitSpecificationV1.CurrentSchemaVersion,
            unitId.Trim(),
            name,
            rawRequest.Trim(),
            AuthoredUnitSourceKindV1.Text,
            AuthoredUnitKindV1.Visualizer,
            [
                new AuthoredInstrumentRequestV1(
                    "primary",
                    instrument.CanonicalSymbol,
                    instrument.InstrumentId,
                    instrument.AssetClass,
                    instrument.AvailableBrokers[0]),
            ],
            new AuthoredUnitTimeframeV1("1 hour", TimeSpan.FromHours(1)),
            StrategyDataRequirement.Bars,
            [],
            ComposeDrawing(overlays),
            [],
            [],
            AuthoredUnitExecutionIntentV1.None));

        return AuthoredUnitSpecificationValidatorV1.ValidateForLaunch(specification).Count == 0
            ? specification
            : null;
    }

    /// <summary>
    /// True when free text clearly asks for a position-changing strategy rather than a chart overlay.
    /// </summary>
    public static bool LooksLikeTradingRequest(string requestText)
    {
        var normalized = Normalize(requestText);
        if (normalized.Length == 0) return false;
        return ContainsToken(normalized, "buy") ||
               ContainsToken(normalized, "sell") ||
               ContainsToken(normalized, "enter") ||
               ContainsToken(normalized, "exit") ||
               ContainsToken(normalized, "rebalance") ||
               ContainsToken(normalized, "stop loss") ||
               ContainsToken(normalized, "take profit") ||
               (ContainsToken(normalized, "trade") && ContainsToken(normalized, "strategy")) ||
               (ContainsToken(normalized, "paper") &&
                (ContainsToken(normalized, "order") || ContainsToken(normalized, "position")));
    }

    /// <summary>
    /// True when the user is asking for analysis/explanation of indicators, not merely to apply them.
    /// </summary>
    public static bool LooksLikeAnalyticalResearchQuestion(string requestText)
    {
        var normalized = Normalize(requestText);
        if (normalized.Length == 0) return false;
        return ContainsToken(normalized, "why") ||
               ContainsToken(normalized, "how") ||
               ContainsToken(normalized, "explain") ||
               ContainsToken(normalized, "compare") ||
               ContainsToken(normalized, "difference") ||
               ContainsToken(normalized, "differ") ||
               ContainsToken(normalized, "fail") ||
               ContainsToken(normalized, "failed") ||
               ContainsToken(normalized, "analyse") ||
               ContainsToken(normalized, "analyze") ||
               ContainsToken(normalized, "what happened") ||
               ContainsToken(normalized, "where did");
    }

    public static string BuildOverlayClarificationQuestion(
        IReadOnlyList<AuthoredChartOverlayChoiceV1>? overlayCatalog = null)
    {
        var catalog = overlayCatalog ?? Overlays;
        var builder = new StringBuilder();
        builder.Append(ChoiceClarificationPrefix);
        builder.Append(" (reply with names or numbers): ");
        for (var i = 0; i < catalog.Count; i++)
        {
            if (i > 0) builder.Append("; ");
            builder.Append(CultureInfo.InvariantCulture, $"{i + 1}) {catalog[i].DisplayName}");
        }

        builder.Append(". Famous indicators here are display overlays only — they do not place Paper orders.");
        return builder.ToString();
    }

    public static string BuildResearchClarificationQuestion()
    {
        var builder = new StringBuilder();
        builder.Append("Choose a research scan (reply with names or numbers): ");
        for (var i = 0; i < ResearchScans.Count; i++)
        {
            if (i > 0) builder.Append("; ");
            builder.Append(CultureInfo.InvariantCulture, $"{i + 1}) {ResearchScans[i].DisplayName}");
        }

        builder.Append(". These label historical windows for study; they are not strategies and do not unlock Paper.");
        return builder.ToString();
    }

    public static string DescribeSelection(AuthoredChartChoiceResolutionV1 resolution)
    {
        var parts = new List<string>();
        if (resolution.Overlays.Count > 0)
            parts.Add("overlays: " + string.Join(", ", resolution.Overlays.Select(static item => item.DisplayName)));
        if (resolution.ResearchScans.Count > 0)
            parts.Add("research scans: " + string.Join(", ", resolution.ResearchScans.Select(static item => item.DisplayName)));
        return parts.Count == 0 ? "none" : string.Join(" · ", parts);
    }

    private static IReadOnlyList<AuthoredChartOverlayChoiceV1> MatchOverlays(
        string normalizedHaystack,
        string? latestReply,
        IReadOnlyList<AuthoredChartOverlayChoiceV1> catalog)
    {
        var matched = new List<AuthoredChartOverlayChoiceV1>();
        var replyNormalized = Normalize(latestReply ?? string.Empty);

        // Numbered replies from the host clarification ("1 3 5" or "2,4").
        foreach (Match match in Regex.Matches(replyNormalized, @"\b(\d{1,2})\b"))
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;
            if (index < 1 || index > catalog.Count)
                continue;
            var choice = catalog[index - 1];
            if (matched.All(item => item.Id != choice.Id))
                matched.Add(choice);
        }

        foreach (var overlay in catalog)
        {
            if (overlay.Aliases.Any(alias => ContainsToken(normalizedHaystack, Normalize(alias))) ||
                ContainsToken(normalizedHaystack, Normalize(overlay.DisplayName)))
            {
                if (matched.All(item => item.Id != overlay.Id))
                    matched.Add(overlay);
            }
        }

        // "famous indicators" with no names is handled by NeedsClarification; if the user later
        // answers only with numbers we already captured them above.
        return matched;
    }

    private static IReadOnlyList<AuthoredResearchScanChoiceV1> MatchResearchScans(string normalizedHaystack)
    {
        var matched = new List<AuthoredResearchScanChoiceV1>();
        foreach (var scan in ResearchScans)
        {
            if (scan.Aliases.Any(alias => ContainsToken(normalizedHaystack, Normalize(alias))) ||
                ContainsToken(normalizedHaystack, Normalize(scan.DisplayName)))
            {
                matched.Add(scan);
            }
        }

        // Numbered research clarification uses the same 1..N indices as ResearchScans.
        foreach (Match match in Regex.Matches(normalizedHaystack, @"\b(\d{1,2})\b"))
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;
            if (index < 1 || index > ResearchScans.Count)
                continue;
            // Only treat bare numbers as research picks when the haystack already looks like a
            // research clarification reply (contains the scan catalog prefix or prior scan words).
            if (!normalizedHaystack.Contains("research scan", StringComparison.Ordinal) &&
                matched.Count == 0 &&
                !AsksForResearchGallery(normalizedHaystack))
                continue;
            var choice = ResearchScans[index - 1];
            if (matched.All(item => item.Id != choice.Id))
                matched.Add(choice);
        }

        return matched;
    }

    private static bool AsksForFamousIndicators(string normalized) =>
        normalized.Contains("famous indicator", StringComparison.Ordinal) ||
        normalized.Contains("popular indicator", StringComparison.Ordinal) ||
        normalized.Contains("standard indicator", StringComparison.Ordinal) ||
        normalized.Contains("common indicator", StringComparison.Ordinal) ||
        (normalized.Contains("indicator", StringComparison.Ordinal) &&
         (normalized.Contains("which", StringComparison.Ordinal) ||
          normalized.Contains("choose", StringComparison.Ordinal) ||
          normalized.Contains("pick", StringComparison.Ordinal) ||
          normalized.Contains("show me", StringComparison.Ordinal) ||
          normalized.Contains("add some", StringComparison.Ordinal) ||
          normalized.Contains("have some", StringComparison.Ordinal) ||
          normalized.Contains("any indicator", StringComparison.Ordinal) ||
          normalized.Contains("what indicator", StringComparison.Ordinal) ||
          normalized.Contains("see indicator", StringComparison.Ordinal) ||
          normalized.Contains("show indicator", StringComparison.Ordinal) ||
          normalized.Contains("want to see indicator", StringComparison.Ordinal)));

    private static bool AsksForResearchGallery(string normalized) =>
        normalized.Contains("gallery", StringComparison.Ordinal) ||
        normalized.Contains("similar history", StringComparison.Ordinal) ||
        normalized.Contains("historically similar", StringComparison.Ordinal) ||
        (normalized.Contains("find", StringComparison.Ordinal) &&
         (normalized.Contains("rose", StringComparison.Ordinal) ||
          normalized.Contains("crash", StringComparison.Ordinal) ||
          normalized.Contains("breakout", StringComparison.Ordinal)));

    private static AuthoredChartCompositionV1 EmptyPriceDrawing() =>
        new(
            [new AuthoredChartPaneV1("price", AuthoredChartPaneRoleV1.Price, 0, "Price")],
            []);

    private static AuthoredChartCompositionV1 EnsureCandles(AuthoredChartCompositionV1 drawing)
    {
        var panes = drawing.Panes.Count == 0
            ? new[] { new AuthoredChartPaneV1("price", AuthoredChartPaneRoleV1.Price, 0, "Price") }
            : drawing.Panes.ToArray();
        if (drawing.Layers.Any(static layer =>
                layer.Kind == AuthoredChartLayerKindV1.Candles ||
                string.Equals(layer.TypeId, "price.candles@1", StringComparison.Ordinal)))
        {
            return drawing with { Panes = panes };
        }

        var layers = drawing.Layers.ToList();
        layers.Insert(0, new AuthoredChartLayerV1(
            "candles",
            panes[0].PaneId,
            AuthoredChartLayerKindV1.Candles,
            "price.candles@1",
            new Dictionary<string, string>()));
        return new AuthoredChartCompositionV1(panes, layers);
    }

    private static AuthoredChartCompositionV1 AddIndicatorLine(
        AuthoredChartCompositionV1 drawing,
        string layerId,
        string typeId,
        string period)
    {
        drawing = EnsureCandles(drawing);
        if (drawing.Layers.Any(layer => string.Equals(layer.LayerId, layerId, StringComparison.Ordinal)))
            return drawing;

        var layers = drawing.Layers.ToList();
        layers.Add(new AuthoredChartLayerV1(
            layerId,
            "price",
            AuthoredChartLayerKindV1.IndicatorLine,
            typeId,
            new Dictionary<string, string> { ["period"] = period }));
        return drawing with { Layers = layers };
    }

    private static AuthoredChartCompositionV1 AddOscillator(
        AuthoredChartCompositionV1 drawing,
        string paneId,
        string layerId,
        string typeId,
        string period,
        AuthoredChartLayerKindV1 kind)
    {
        drawing = EnsureCandles(drawing);
        var panes = drawing.Panes.ToList();
        if (panes.All(pane => !string.Equals(pane.PaneId, paneId, StringComparison.Ordinal)))
        {
            panes.Add(new AuthoredChartPaneV1(
                paneId,
                AuthoredChartPaneRoleV1.Indicator,
                panes.Count,
                paneId.ToUpperInvariant()));
        }

        var layers = drawing.Layers.ToList();
        if (layers.All(layer => !string.Equals(layer.LayerId, layerId, StringComparison.Ordinal)))
        {
            layers.Add(new AuthoredChartLayerV1(
                layerId,
                paneId,
                kind,
                typeId,
                new Dictionary<string, string> { ["period"] = period }));
        }

        return new AuthoredChartCompositionV1(panes, layers);
    }

    private static AuthoredChartCompositionV1 AddMacd(AuthoredChartCompositionV1 drawing)
    {
        drawing = EnsureCandles(drawing);
        var panes = drawing.Panes.ToList();
        if (panes.All(pane => !string.Equals(pane.PaneId, "macd", StringComparison.Ordinal)))
        {
            panes.Add(new AuthoredChartPaneV1("macd", AuthoredChartPaneRoleV1.Indicator, panes.Count, "MACD"));
        }

        var layers = drawing.Layers.ToList();
        void Add(string layerId, AuthoredChartLayerKindV1 kind, string series)
        {
            if (layers.Any(layer => string.Equals(layer.LayerId, layerId, StringComparison.Ordinal)))
                return;
            layers.Add(new AuthoredChartLayerV1(
                layerId,
                "macd",
                kind,
                "indicator.macd@1",
                new Dictionary<string, string>
                {
                    ["fast"] = "12",
                    ["slow"] = "26",
                    ["signal"] = "9",
                    ["series"] = series,
                }));
        }

        Add("macd-line", AuthoredChartLayerKindV1.IndicatorLine, "macd");
        Add("macd-signal", AuthoredChartLayerKindV1.IndicatorLine, "signal");
        Add("macd-hist", AuthoredChartLayerKindV1.Histogram, "histogram");
        return new AuthoredChartCompositionV1(panes, layers);
    }

    private static AuthoredChartCompositionV1 AddBollinger(AuthoredChartCompositionV1 drawing)
    {
        drawing = EnsureCandles(drawing);
        var layers = drawing.Layers.ToList();
        void Add(string layerId, string band)
        {
            if (layers.Any(layer => string.Equals(layer.LayerId, layerId, StringComparison.Ordinal)))
                return;
            layers.Add(new AuthoredChartLayerV1(
                layerId,
                "price",
                AuthoredChartLayerKindV1.IndicatorLine,
                "indicator.bollinger@1",
                new Dictionary<string, string>
                {
                    ["period"] = "20",
                    ["stddev"] = "2",
                    ["band"] = band,
                }));
        }

        Add("bb-mid", "middle");
        Add("bb-upper", "upper");
        Add("bb-lower", "lower");
        return drawing with { Layers = layers };
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static bool ContainsToken(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle))
            return false;
        if (needle.Contains(' ', StringComparison.Ordinal))
            return haystack.Contains(needle, StringComparison.Ordinal);
        return Regex.IsMatch(haystack, $@"\b{Regex.Escape(needle)}\b", RegexOptions.CultureInvariant);
    }
}
