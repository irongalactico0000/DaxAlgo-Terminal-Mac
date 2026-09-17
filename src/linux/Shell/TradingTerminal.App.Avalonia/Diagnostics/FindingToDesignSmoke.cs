using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Authoring;
using TradingTerminal.App.Avalonia.Settings;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Generation;

namespace TradingTerminal.App.Avalonia.Diagnostics;

/// <summary>
/// Owned UI smoke: Save finding → Confirm link → Apply Design proposal, with PNG evidence.
/// Invoke: <c>--smoke-finding-to-design[=/path/out-dir]</c>
/// </summary>
internal static class FindingToDesignSmoke
{
    public static async Task<int> RunAsync(IServiceProvider services, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var reportPath = Path.Combine(outputDirectory, "RESULT.txt");
        var lines = new List<string>
        {
            $"Finding→Design smoke — {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            "Flow: condition → Save finding → Add → Confirm → Apply proposal → Design fields",
            string.Empty,
        };

        StrategyAuthoringWindow? window = null;
        try
        {
            var authoring = services.GetRequiredService<StrategyAuthoringViewModel>();
            authoring.DisplayName = "Momentum";
            authoring.DesignEntryRuleText = "Manual entry before finding";
            window = new StrategyAuthoringWindow
            {
                DataContext = authoring,
                Width = 1100,
                Height = 780,
            };
            window.Show();
            await PumpAsync().ConfigureAwait(true);
            await SavePngAsync(window, Path.Combine(outputDirectory, "01-design-before.png")).ConfigureAwait(true);
            lines.Add("PASS  opened Strategy Builder Design");

            authoring.OpenDesignScreenCommand.Execute(null);
            await PumpAsync().ConfigureAwait(true);

            var start = new DateTimeOffset(2026, 9, 16, 14, 0, 0, TimeSpan.Zero);
            authoring.SetResearchChartSelection(
                new ResearchChartSelectionV1(
                    new InstrumentId(7),
                    "MSFT",
                    BarSize.OneHour,
                    start,
                    start.AddHours(4),
                    start.AddHours(4),
                    start.AddHours(8),
                    StrategyDataRequirement.Bars),
                indicatorBindings: [new ResearchIndicatorBindingV1("ema-20", "ema", 20)]);
            authoring.PendingConditionMultipleText = "2";
            authoring.PendingConditionLookbackText = "20";
            authoring.ApplyPendingResearchConditionCommand.Execute(null);
            authoring.SaveResearchFinding1Command.Execute(null);
            if (!authoring.CanUseObservationInDesign)
            {
                lines.Add("FAIL  Add finding not enabled after Save finding");
                return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
            }

            authoring.UseObservationInDesignCommand.Execute(null);
            await PumpAsync().ConfigureAwait(true);
            if (!authoring.HasPendingAddFindingReview)
            {
                lines.Add("FAIL  Add finding did not stage review panel");
                return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
            }

            authoring.ConfirmAddFindingToStrategyCommand.Execute(null);
            await PumpAsync().ConfigureAwait(true);
            await SavePngAsync(window, Path.Combine(outputDirectory, "02-after-confirm-proposal.png")).ConfigureAwait(true);

            if (!authoring.HasResearchDesignHandoff)
            {
                lines.Add("FAIL  Confirm did not set research handoff");
                return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
            }

            if (authoring.PendingStrategyDraft?.LinkedConditionId is null)
            {
                lines.Add("FAIL  Confirm did not bind condition id/hash");
                return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
            }

            if (!authoring.HasPendingFindingDesignProposal)
            {
                lines.Add("FAIL  Confirm did not stage finding→Design proposal");
                return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
            }

            lines.Add(
                $"PASS  Confirm · condition={authoring.PendingStrategyDraft.LinkedConditionId} · proposal staged");

            if (!string.Equals(authoring.DesignEntryRuleText, "Manual entry before finding", StringComparison.Ordinal))
            {
                lines.Add(
                    $"FAIL  Confirm overwrote ENTRY before Apply: {authoring.DesignEntryRuleText}");
                return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
            }

            authoring.AcceptFindingDesignProposalCommand.Execute(null);
            await PumpAsync().ConfigureAwait(true);
            await SavePngAsync(window, Path.Combine(outputDirectory, "03-after-apply-fields.png")).ConfigureAwait(true);

            if (authoring.HasPendingFindingDesignProposal)
            {
                lines.Add("FAIL  proposal still pending after Apply");
                return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
            }

            if (!string.Equals(authoring.DesignInstrumentText, "MSFT", StringComparison.Ordinal) ||
                !string.Equals(authoring.DesignTimeframeText, "1h", StringComparison.Ordinal) ||
                authoring.DesignEntryRuleText is null ||
                !authoring.DesignEntryRuleText.Contains("volume", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(
                    $"FAIL  Apply did not write Design fields · instrument={authoring.DesignInstrumentText} · tf={authoring.DesignTimeframeText} · entry={authoring.DesignEntryRuleText}");
                return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(authoring.DesignExitRuleText) ||
                !string.IsNullOrWhiteSpace(authoring.DesignSizingRuleText))
            {
                lines.Add(
                    $"FAIL  Apply filled unresolved placeholders · exit={authoring.DesignExitRuleText} · sizing={authoring.DesignSizingRuleText}");
                return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
            }

            if (!authoring.DesignUnresolvedChecklistText.Contains("exit", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"FAIL  checklist should still list exit: {authoring.DesignUnresolvedChecklistText}");
                return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
            }

            lines.Add(
                $"PASS  Apply · INSTRUMENT=MSFT · TIMEFRAME=1h · ENTRY contains volume · EXIT left empty");
            lines.Add("PASS  screenshots 01–03 written");
            lines.Add(string.Empty);
            lines.Add("OVERALL PASS");
            return await WriteAsync(lines, reportPath, 0).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lines.Add($"FAIL  exception: {ex.GetType().Name}: {ex.Message}");
            return await WriteAsync(lines, reportPath, 1).ConfigureAwait(false);
        }
        finally
        {
            if (window is not null)
                await Dispatcher.UIThread.InvokeAsync(() => window.Close());
        }
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(80).ConfigureAwait(true);
    }

    private static async Task SavePngAsync(Window window, string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.UpdateLayout();
            var scale = window.RenderScaling;
            var width = Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(
                new PixelSize(width, height),
                new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            bitmap.Save(path);
        });
    }

    private static async Task<int> WriteAsync(List<string> lines, string reportPath, int exitCode)
    {
        await File.WriteAllLinesAsync(reportPath, lines).ConfigureAwait(false);
        return exitCode;
    }
}
