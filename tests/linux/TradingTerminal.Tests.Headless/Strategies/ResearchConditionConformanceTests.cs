using System.Text.Json;
using TradingTerminal.Core.Strategies.Generation;
using Xunit;

namespace TradingTerminal.Tests.Headless.Strategies;

/// <summary>
/// Cross-engine conformance: same fixture as TSD tests/fixtures/volume_multiple_conformance.json.
/// </summary>
public sealed class ResearchConditionConformanceTests
{
    [Fact]
    public void Volume_multiple_matches_shared_tsd_fixture()
    {
        var fixturePath = FindFixture();
        using var doc = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var root = doc.RootElement;
        var cond = root.GetProperty("condition");
        var expected = root.GetProperty("expected");
        var t0 = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        // Fixture uses epoch offsets; relative indices matter, not absolute wall clock.
        t0 = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var bars = new List<(DateTimeOffset, double, double)>();
        foreach (var row in root.GetProperty("bars").EnumerateArray())
        {
            var i = row.GetProperty("i").GetInt32();
            bars.Add((
                t0.AddMinutes(i),
                row.GetProperty("volume").GetDouble(),
                row.GetProperty("close").GetDouble()));
        }

        var condition = ResearchConditionDefinitionV1.VolumeMultiple(
            cond.GetProperty("threshold").GetDouble(),
            cond.GetProperty("lookback_bars").GetInt32());
        var result = ResearchConditionEvaluatorV1.SearchVolumeMultiple(
            condition,
            bars,
            dataSource: "conformance",
            symbol: "FIXTURE",
            forwardBars: root.GetProperty("forward_bars").GetInt32());

        Assert.Equal(expected.GetProperty("hit_count").GetInt32(), result.HitCount);
        Assert.Equal(expected.GetProperty("live_meets_condition").GetBoolean(), result.LiveMeetsCondition);
        var expectedIndices = expected.GetProperty("hit_indices").EnumerateArray().Select(e => e.GetInt32()).ToHashSet();
        Assert.Contains(result.Hits, hit =>
        {
            var idx = (int)(hit.BarTimeUtc - t0).TotalMinutes;
            return expectedIndices.Contains(idx);
        });
    }

    private static string FindFixture()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "tsd", "tests", "fixtures", "volume_multiple_conformance.json")),
            "/Users/w/Developer/tsd/tests/fixtures/volume_multiple_conformance.json",
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        // Embed minimal twin if TSD tree is unavailable
        var embedded = Path.Combine(Path.GetTempPath(), "volume_multiple_conformance.json");
        File.WriteAllText(embedded, """
            {"condition":{"threshold":2.0,"lookback_bars":20},"forward_bars":5,
             "bars":[{"i":0,"volume":100,"close":100},{"i":1,"volume":100,"close":101},{"i":2,"volume":100,"close":102},{"i":3,"volume":100,"close":103},{"i":4,"volume":100,"close":104},{"i":5,"volume":100,"close":105},{"i":6,"volume":100,"close":106},{"i":7,"volume":100,"close":107},{"i":8,"volume":100,"close":108},{"i":9,"volume":100,"close":109},{"i":10,"volume":100,"close":110},{"i":11,"volume":100,"close":111},{"i":12,"volume":100,"close":112},{"i":13,"volume":100,"close":113},{"i":14,"volume":100,"close":114},{"i":15,"volume":100,"close":115},{"i":16,"volume":100,"close":116},{"i":17,"volume":100,"close":117},{"i":18,"volume":100,"close":118},{"i":19,"volume":100,"close":119},{"i":20,"volume":100,"close":120},{"i":21,"volume":100,"close":121},{"i":22,"volume":100,"close":122},{"i":23,"volume":100,"close":123},{"i":24,"volume":250,"close":130}],
             "expected":{"hit_indices":[24],"live_meets_condition":true,"hit_count":1}}
            """);
        return embedded;
    }
}
