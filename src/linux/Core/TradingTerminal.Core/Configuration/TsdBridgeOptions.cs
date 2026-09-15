namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Silent link to the local TSD Python sidecar (Model A). When enabled, the app polls
/// TSD for strategy signals/intents in the background. The user only sees normal
/// confirm / Execution Console flows — not "bridge" or "TOMS" jargon.
/// Bound from the <c>TsdBridge</c> section.
/// </summary>
public sealed class TsdBridgeOptions
{
    public const string SectionName = "TsdBridge";

    /// <summary>Master switch. Off = no HTTP to TSD (app unchanged).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>TSD API base, e.g. http://127.0.0.1:8000</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";

    /// <summary>Poll interval for signals/intents. Failures stay silent in the UI.</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>When true, unreachable TSD only logs Debug — never blocks startup.</summary>
    public bool SoftFail { get; set; } = true;
}
