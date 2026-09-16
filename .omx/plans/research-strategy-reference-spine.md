# Research ↔ Strategy through saved findings

**Status:** Operational contract for Mac UI and agents (2026-09-17)  
**Canon IDs:** U07 / C05 / V04 / V07 · R11–R12 · R18–R19  
**Type (code):** `ResearchAnalysisReferenceV1` — call it a **saved finding** in product language.

## Terminology

| Product | Avoid leading with |
|---------|-------------------|
| **Saved finding** | “Reference A/B” |
| Two findings to compare | Treating “A” and “B” as product brands |

Two storage slots exist so you can keep a **working** finding and a **contrast** finding. Letters are implementation keys only.

## Why saved findings (not more buttons)

Research Studio and Strategy Builder stay **separate windows**.  
They share meaning through a **saved finding**, not duplicated toolbars.

A saved finding stores:

- chart selection (instrument, timeframe, observation / outcome when selected)
- exact indicator bindings (kind, period, version)
- condition definition + version hash (when applied)
- optional last search result (hits / fails)

Restore must reopen that package without re-uploading files.

## Functional map

```text
┌─────────────────────────────┐
│ Open chart (shared Charts)  │  instrument · indicators · market views
└──────────────┬──────────────┘
               │ same data / same bindings
┌──────────────▼──────────────┐
│ Research Studio + Hyperion  │  screen · ask · compare · Find similar
│                             │  Save finding  ←── handoff unit
└──────────────┬──────────────┘
               │ Use in Strategy Builder (requires / creates a finding)
┌──────────────▼──────────────┐
│ Strategy Builder            │  entry / exit / size from that finding
│                             │  Validate · Paper (fill model explicit)
└─────────────────────────────┘
```

## User journeys (acceptance)

### Research-only (no strategy yet)

1. Open chart (Charts or Rank → Open chart).
2. Toggle / develop indicators on **that** chart.
3. Ask Hyperion an analytical question (must explain, not only “overlay applied”).
4. Optionally select observation + outcome; Apply condition; Find similar (same condition — never silent next-day +5%).
5. **Save finding** (and a second finding if you need contrast). Restoring the first after the second must keep distinct ranges/bindings.

### Research → Strategy

1. Restore the intended **saved finding**, or save before handoff.
2. **Use in Strategy Builder** — Builder opens with that evidence; Studio stays session-matched.
3. Bind condition id+hash into the draft; add exits/sizing without rewriting the entry condition.
4. Validate under the declared fill model (today: honest L1; queue/liquidity/latency = later).

### What not to do

- Do not gate Order book / Footprint / Bookmap on Rank.
- Do not open another session’s Studio from Builder.
- Do not invent outcome intervals or declare Depth/Tape when the chart only used bars.
- Do not pretend “Chart grid” is multi-chart until real tiles exist.
- Do not add Rank-adjacent tool buttons; put chart tools on the chart.
- Do not say “Reference A/B” in user-facing copy.

## Code anchors

| Concern | Location |
|---|---|
| Saved finding record | `ResearchAnalysisReferenceV1.cs` |
| Save / restore slots | `StrategyAuthoringViewModel.ResearchDataset.cs` |
| Studio handoff | `UseObservationInDesign` → `StrategyBuilderHandoffRequested` |
| Session-matched Studio | `MainWindow.WireResearchStudioRequest` |
| Durable Builder handoff | `MainWindow.WireStrategyBuilderHandoff` |
| Chart-owned market views | `ChartsViewModel` `OpenChart*Command` |

## Execution (separate lane)

Nautilus-class fill behaviour belongs in Validate/Paper — not Research UI.

Full lane map: [`research-strategy-backtest-reference-map.md`](research-strategy-backtest-reference-map.md).  
Phase 5: [`Phase5_Execution_Contracts_2026-09-15.md`](Phase5_Execution_Contracts_2026-09-15.md).
