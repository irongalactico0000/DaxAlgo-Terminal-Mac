using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.StrategyAgent;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.App.Avalonia.Tests;

public sealed class NativeStrategyRunAuthoringTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public async Task Configured_native_client_suppresses_draft_lanes_and_loads_exact_retained_evidence()
    {
        var client = new RecordingStrategyAgentClient();
        using var viewModel = CreateViewModel(client);

        viewModel.IsDesignScreen.Should().BeTrue();
        viewModel.CanOpenBuildScreen.Should().BeTrue(
            "retained native evidence must not require the removed four-draft confirmation gate");
        viewModel.OpenBuildScreenCommand.CanExecute(null).Should().BeTrue();
        viewModel.OpenBuildScreenCommand.Execute(null);

        viewModel.ShowNativeStrategyRunPanel.Should().BeTrue();
        viewModel.ShowNativeImplementationHeader.Should().BeTrue();
        viewModel.ShowImplementationHeader.Should().BeFalse();
        viewModel.ShowLegacyCandidateBoundary.Should().BeFalse();
        viewModel.ShowBuildCandidateResults.Should().BeFalse();
        viewModel.ShowCandidateEmptyState.Should().BeFalse();
        viewModel.ShowStartImplementationAction.Should().BeFalse();

        viewModel.NativeRunId = "run-1";
        await viewModel.LoadNativeRunCommand.ExecuteAsync(null);

        viewModel.LoadedNativeRunId.Should().Be("run-1");
        viewModel.LoadedNativeSessionId.Should().Be("session-1");
        viewModel.NativeRunStatus.Should().Be("failed");
        viewModel.NativeEvidenceStatus.Should().Be("failed");
        viewModel.NativeManifestSha256.Should().Be(new string('a', 64));
        client.RunEventCursors.Should().Equal(0, 1);

        var research = viewModel.NativeStrategyEvidencePanels.Single(panel => panel.Key == "research");
        research.Status.Should().Be("passed");
        research.Stage.Should().Be("strategy.confirmed");
        research.Events.Should().ContainSingle();
        research.Evidence.Should().Contain("no executable or backtest status");

        var vibequant = viewModel.NativeStrategyEvidencePanels.Single(panel => panel.Key == "vibequant");
        vibequant.Status.Should().Be("passed");
        vibequant.Stage.Should().Be("completed");
        vibequant.Summary.Should().Contain("transcend-0/VibeQuant");
        vibequant.Evidence.Should().Contain("lanes/vibequant/strategy.py");
        vibequant.Evidence.Should().Contain(new string('b', 64));
        vibequant.Evidence.Should().Contain("closed_trade_count");

        var csp = viewModel.NativeStrategyEvidencePanels.Single(panel => panel.Key == "csp");
        csp.Status.Should().Be("failed");
        csp.Stage.Should().Be("csp.run");
        csp.ExactFailure.Should().Be("CSP graph stopped at csp.run: forced fixture failure.");
        csp.Events.Should().ContainSingle(eventRow => eventRow.Stage == "csp.run");

        var comparison = viewModel.NativeStrategyEvidencePanels.Single(panel => panel.Key == "comparison");
        comparison.Status.Should().Be("failed");
        comparison.Summary.Should().Contain("comparison/report.json");
        comparison.Evidence.Should().Contain("scenario_checks");
        comparison.Events.Should().ContainSingle(eventRow => eventRow.Status == "failed");

        viewModel.NativeArtifactPath = "lanes/vibequant/strategy.py";
        await viewModel.LoadNativeArtifactCommand.ExecuteAsync(null);
        viewModel.NativeArtifactContent.Should().Be("class Strategy: pass\n");
        viewModel.NativeArtifactStatus.Should().Contain(new string('b', 64));
        client.ArtifactRequests.Should().ContainSingle().Which.Should().Be(
            ("run-1", "lanes/vibequant/strategy.py"));
    }

    [Fact]
    public async Task Api_failure_is_shown_with_the_service_code_and_detail()
    {
        var client = new RecordingStrategyAgentClient
        {
            GetRunFailure = new StrategyAgentApiException(
                "run_not_found",
                "strategy run does not exist: absent",
                HttpStatusCode.NotFound),
        };
        using var viewModel = CreateViewModel(client);
        viewModel.NativeRunId = "absent";

        await viewModel.LoadNativeRunCommand.ExecuteAsync(null);

        viewModel.NativeRunFailureCode.Should().Be("run_not_found");
        viewModel.NativeRunFailureDetail.Should().Be("strategy run does not exist: absent");
        viewModel.NativeRunLoadStatus.Should().Be(
            "run_not_found: strategy run does not exist: absent");
    }

    [Fact]
    public async Task Failed_event_read_for_run_B_keeps_successfully_loaded_run_A_and_panels()
    {
        var client = new RecordingStrategyAgentClient
        {
            RunStatusOverride = RecordingStrategyAgentClient.CreateRunStatus("run-a", "session-a"),
        };
        using var viewModel = CreateViewModel(client);
        viewModel.NativeRunId = "run-a";
        await viewModel.LoadNativeRunCommand.ExecuteAsync(null);

        var priorIdentity = viewModel.NativeRunIdentityText;
        var priorStatus = viewModel.NativeRunStatus;
        var priorPanels = viewModel.NativeStrategyEvidencePanels.ToDictionary(
            panel => panel.Key,
            panel => (panel.Status, panel.Stage, panel.Summary, panel.ExactFailure, panel.Evidence,
                Events: panel.Events.ToArray()),
            StringComparer.Ordinal);

        client.RunStatusOverride = RecordingStrategyAgentClient.CreateRunStatus("run-b", "session-b");
        client.RunEventsFailure = new StrategyAgentApiException(
            "run_events_unavailable",
            "retained run events could not be read for run-b");
        viewModel.NativeRunId = "run-b";
        await viewModel.LoadNativeRunCommand.ExecuteAsync(null);

        viewModel.LoadedNativeRunId.Should().Be("run-a");
        viewModel.LoadedNativeSessionId.Should().Be("session-a");
        viewModel.NativeRunIdentityText.Should().Be(priorIdentity);
        viewModel.NativeRunStatus.Should().Be(priorStatus);
        viewModel.NativeRunFailureCode.Should().Be("run_events_unavailable");
        foreach (var panel in viewModel.NativeStrategyEvidencePanels)
        {
            var prior = priorPanels[panel.Key];
            panel.Status.Should().Be(prior.Status);
            panel.Stage.Should().Be(prior.Stage);
            panel.Summary.Should().Be(prior.Summary);
            panel.ExactFailure.Should().Be(prior.ExactFailure);
            panel.Evidence.Should().Be(prior.Evidence);
            panel.Events.Should().Equal(prior.Events);
        }
    }

    [Fact]
    public async Task Restarted_service_falls_back_to_real_run_research_events_and_loads_other_lanes()
    {
        var client = new RecordingStrategyAgentClient
        {
            SessionEventsFailure = new StrategyAgentApiException(
                "research_session_not_found",
                "research session does not exist after restart"),
            IncludeRunResearchEvent = true,
        };
        using var viewModel = CreateViewModel(client);
        viewModel.NativeRunId = "run-1";

        await viewModel.LoadNativeRunCommand.ExecuteAsync(null);

        viewModel.LoadedNativeRunId.Should().Be("run-1");
        viewModel.NativeRunFailureCode.Should().BeNull();
        viewModel.NativeResearchSessionUnavailable.Should().BeTrue();
        viewModel.NativeResearchAvailabilityText.Should().Be(
            "Research session transcript unavailable after service restart. Showing retained run-level research events only.");

        var research = viewModel.NativeStrategyEvidencePanels.Single(panel => panel.Key == "research");
        research.Summary.Should().Contain("Session endpoint unavailable");
        research.Evidence.Should().Contain("Only real lane=research events retained with this run are shown");
        research.Events.Should().ContainSingle();
        research.Events[0].Stage.Should().Be("confirmation");
        research.Events[0].Message.Should().Be("Research strategy and frozen run manifest were confirmed.");

        viewModel.NativeStrategyEvidencePanels.Single(panel => panel.Key == "vibequant")
            .Evidence.Should().Contain("lanes/vibequant/strategy.py");
        viewModel.NativeStrategyEvidencePanels.Single(panel => panel.Key == "csp")
            .ExactFailure.Should().Contain("forced fixture failure");
        viewModel.NativeStrategyEvidencePanels.Single(panel => panel.Key == "comparison")
            .Evidence.Should().Contain("scenario_checks");
    }

    [Fact]
    public void Xaml_labels_partial_bridge_and_binds_only_native_run_evidence_actions()
    {
        var root = XDocument.Load(Fixture("StrategyAuthoringWindow.axaml")).Root
            ?? throw new InvalidOperationException("The strategy authoring fixture has no root element.");
        var surface = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Native strategy retained run evidence");

        surface.Attribute("IsVisible")!.Value.Should().Be("{Binding ShowNativeStrategyRunPanel}");
        surface.Descendants(Avalonia + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null)
            .Should().Contain(text => text == "SAVED RESULTS FOR THIS STRATEGY");
        surface.Descendants(Avalonia + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null)
            .Should().Contain(text => text!.Contains(
                "COMPARE · LOAD SAVED RUN OR ADVANCED ID",
                StringComparison.Ordinal));
        surface.Descendants(Avalonia + "Button").Select(element =>
                (string?)element.Attribute("Command"))
            .Should().Contain(
                "{Binding LoadNativeRunCommand}",
                "{Binding RefreshNativeRunCommand}",
                "{Binding StartNativeRunCommand}",
                "{Binding CancelNativeRunCommand}",
                "{Binding LoadNativeArtifactCommand}");
        surface.Descendants(Avalonia + "ItemsControl").Should().Contain(element =>
            (string?)element.Attribute("ItemsSource") == "{Binding NativeStrategyEvidencePanels}");
        surface.Descendants(Avalonia + "TextBox").Should().Contain(element =>
            (string?)element.Attribute("Text") == "{Binding NativeArtifactContent, Mode=OneWay}" &&
            (string?)element.Attribute("IsReadOnly") == "True");
        surface.Descendants(Avalonia + "Border").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Research session restart fallback" &&
            (string?)element.Attribute("IsVisible") ==
                "{Binding NativeResearchSessionUnavailable}");
        surface.Descendants(Avalonia + "Border").Should().Contain(element =>
            (string?)element.Attribute("AutomationProperties.Name") ==
                "Strategy version results");

        var legacyNotice = root.Descendants(Avalonia + "Border").Single(element =>
            (string?)element.Attribute("IsVisible") == "{Binding ShowLegacyCandidateBoundary}");
        legacyNotice.Descendants(Avalonia + "TextBlock").Should().Contain(element =>
            ((string?)element.Attribute("Text"))!.Contains(
                "Four AI generation agents",
                StringComparison.Ordinal));
    }

    private static StrategyAuthoringViewModel CreateViewModel(IStrategyAgentClient client) => new(
        new StubCompiler(),
        new StubRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance,
        sessionRepository: new EmptySessionRepository(),
        strategyAgentClient: client);

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static JsonElement Json(string value) =>
        JsonDocument.Parse(value).RootElement.Clone();

    private sealed class RecordingStrategyAgentClient : IStrategyAgentClient
    {
        public StrategyAgentApiException? GetRunFailure { get; init; }
        public StrategyAgentApiException? RunEventsFailure { get; set; }
        public StrategyAgentApiException? SessionEventsFailure { get; init; }
        public StrategyAgentRunStatus? RunStatusOverride { get; set; }
        public bool IncludeRunResearchEvent { get; init; }
        public List<long> RunEventCursors { get; } = [];
        public List<(string RunId, string Path)> ArtifactRequests { get; } = [];

        public Task<StrategyAgentRunStatus> GetRunAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            if (GetRunFailure is not null) throw GetRunFailure;
            return Task.FromResult(RunStatusOverride ?? CreateRunStatus());
        }

        public Task<StrategyAgentEventPage> GetSessionEventsAsync(
            string sessionId,
            long afterSequence = 0,
            int limit = 200,
            CancellationToken cancellationToken = default)
        {
            if (SessionEventsFailure is not null) throw SessionEventsFailure;
            return Task.FromResult(new StrategyAgentEventPage(
                [new StrategyAgentEvent(
                    1,
                    sessionId,
                    null,
                    "research",
                    "strategy.confirmed",
                    "passed",
                    DateTimeOffset.Parse("2026-08-08T01:00:00Z"),
                    "User confirmation created one immutable native run manifest.",
                    Json("{\"manifest_sha256\":\"" + new string('a', 64) + "\"}"))],
                1,
                false,
                true));
        }

        public Task<StrategyAgentEventPage> GetRunEventsAsync(
            string runId,
            long afterSequence = 0,
            int limit = 200,
            CancellationToken cancellationToken = default)
        {
            if (RunEventsFailure is not null) throw RunEventsFailure;
            RunEventCursors.Add(afterSequence);
            if (afterSequence == 0)
            {
                if (IncludeRunResearchEvent)
                {
                    return Task.FromResult(new StrategyAgentEventPage(
                        [new StrategyAgentEvent(
                            1,
                            "session-1",
                            runId,
                            "research",
                            "confirmation",
                            "passed",
                            DateTimeOffset.Parse("2026-08-08T01:00:30Z"),
                            "Research strategy and frozen run manifest were confirmed.",
                            Json("{\"source\":\"retained_run_event\"}"))],
                        1,
                        true,
                        false));
                }

                return Task.FromResult(new StrategyAgentEventPage(
                    [new StrategyAgentEvent(
                        1,
                        "session-1",
                        "run-1",
                        "vibequant",
                        "completed",
                        "passed",
                        DateTimeOffset.Parse("2026-08-08T01:01:00Z"),
                        "VibeQuant retained a native AKQuant result.",
                        Json("{\"artifact\":\"lanes/vibequant/strategy.py\"}"))],
                    1,
                    true,
                    false));
            }

            var sequenceOffset = IncludeRunResearchEvent ? 1 : 0;
            return Task.FromResult(new StrategyAgentEventPage(
                [
                    new StrategyAgentEvent(
                        2 + sequenceOffset,
                        "session-1",
                        "run-1",
                        "csp",
                        "csp.run",
                        "failed",
                        DateTimeOffset.Parse("2026-08-08T01:02:00Z"),
                        "CSP graph stopped at csp.run: forced fixture failure.",
                        Json("{\"code\":\"fixture_failure\"}")),
                    new StrategyAgentEvent(
                        3 + sequenceOffset,
                        "session-1",
                        "run-1",
                        "comparison",
                        "evidence_report",
                        "failed",
                        DateTimeOffset.Parse("2026-08-08T01:03:00Z"),
                        "The retained comparison report contains a failing check.",
                        Json("{\"evidence_status\":\"failed\"}")),
                ],
                3 + sequenceOffset,
                false,
                true));
        }

        public Task<StrategyAgentArtifact> GetArtifactAsync(
            string runId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            ArtifactRequests.Add((runId, relativePath));
            return Task.FromResult(new StrategyAgentArtifact(
                runId,
                relativePath,
                new string('b', 64),
                21,
                "utf-8",
                "class Strategy: pass\n"));
        }

        public Task<StrategyAgentRunStatus> StartAsync(
            string runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RunStatusOverride ?? CreateRunStatus());

        public Task<StrategyAgentRunStatus> CancelAsync(
            string runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RunStatusOverride ?? CreateRunStatus());

        public Task<StrategyAgentSessionStatus> CreateSessionAsync(
            JsonElement frozenContext,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StrategyAgentSessionStatus> GetSessionAsync(
            string sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StrategyAgentSessionStatus> SubmitMessageAsync(
            string sessionId,
            string message,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StrategyAgentRunStatus> ConfirmAsync(
            string sessionId,
            StrategyAgentRunManifest manifest,
            string inputWorkspace,
            JsonElement confirmedIntent,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public static StrategyAgentRunStatus CreateRunStatus(
            string runId = "run-1",
            string sessionId = "session-1")
        {
            var manifestSha = new string('a', 64);
            var vibeSha = new string('b', 64);
            return new StrategyAgentRunStatus(
                runId,
                sessionId,
                manifestSha,
                "failed",
                false,
                ["vibequant", "csp"],
                new Dictionary<string, string>
                {
                    ["vibequant"] = "passed",
                    ["csp"] = "failed",
                },
                new Dictionary<string, StrategyAgentLaneResult>
                {
                    ["vibequant"] = new(
                        "daxalgo-native-lane-result/v1",
                        runId,
                        "vibequant",
                        manifestSha,
                        "passed",
                        "completed",
                        "transcend-0/VibeQuant",
                        "0.1.0",
                        "lanes/vibequant/strategy.py",
                        ["lanes/vibequant/strategy.py"],
                        new Dictionary<string, string>
                        {
                            ["lanes/vibequant/strategy.py"] = vibeSha,
                        },
                        Json("{\"closed_trade_count\":1}"),
                        null),
                    ["csp"] = new(
                        "daxalgo-native-lane-result/v1",
                        runId,
                        "csp",
                        manifestSha,
                        "failed",
                        "csp.run",
                        "Point72 CSP",
                        "0.18.0",
                        "lanes/csp/strategy.py",
                        ["lanes/csp/strategy.py"],
                        new Dictionary<string, string>(),
                        Json("{}"),
                        "CSP graph stopped at csp.run: forced fixture failure."),
                },
                new StrategyAgentComparison(
                    "comparison/report.json",
                    new string('c', 64),
                    Json("{\"evidence_status\":\"failed\",\"scenario_checks\":[]}")),
                "failed",
                3);
        }
    }

    private sealed class EmptySessionRepository : IAuthoringSessionRepository
    {
        public IReadOnlyList<AuthoringSessionSnapshot> List() => [];
        public bool Save(AuthoringSessionSnapshot session) => true;
        public void Delete(string strategyId) { }
    }

    private sealed class StubCompiler : IStrategyCompiler
    {
        public StrategyCompileResult Compile(StrategyScript script) => StrategyCompileResult.Failed([]);
    }

    private sealed class StubRegistry : IBacktestStrategyRegistry
    {
        public IReadOnlyList<BacktestStrategyOption> All => [];
        public BacktestStrategyOption? Find(string id) => null;
        public void Register(BacktestStrategyOption option) { }
        public bool Remove(string id) => false;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
