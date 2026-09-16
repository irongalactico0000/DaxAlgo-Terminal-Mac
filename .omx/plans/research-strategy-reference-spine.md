# Research ↔ Strategy through references

**Status:** Operational contract for Mac UI and agents (2026-09-17)  
**Canon IDs:** U07 / C05 / V04 / V07 · R11–R12 · R18–R19  
**Type:** `ResearchAnalysisReferenceV1` (`research-analysis-reference/v1`)

## Why references (not more buttons)

Research Studio and Strategy Builder stay **separate windows**.  
They share meaning through a **saved reference**, not duplicated toolbars.

A reference stores:

- chart selection (instrument, timeframe, observation / outcome ranges when selected)
- exact indicator bindings (kind, period, version — not display names alone)
- condition definition + version hash (when applied)
- optional last search result (hits / fails)

Restore A or B must reopen that package without re-uploading files.

## Functional map

```text
┌─────────────────────────────┐
│ Open chart (shared Charts)  │  instrument · indicators · market views
└──────────────┬──────────────┘
               │ same data / same bindings
┌──────────────▼──────────────┐
│ Research Studio + Hyperion  │  screen · ask · compare · Find similar
│                             │  Bookmark A / B  ←── product handoff unit
└──────────────┬──────────────┘
               │ Use in Strategy Builder (requires / creates reference)
┌──────────────▼──────────────┐
│ Strategy Builder            │  entry / exit / size from reference
│                             │  Validate · Paper (fill model explicit)
└─────────────────────────────┘
```

## User journeys (acceptance)

### Research-only (no strategy yet)

1. Open chart (Charts menu or Rank → Open chart) for an instrument.
2. Toggle / develop indicators on **that** chart.
3. Ask Hyperion an analytical question about those series (must explain, not only “overlay applied”).
4. Optionally select observation + outcome; Apply condition; Find similar (same condition — never silent next-day +5%).
5. **Bookmark A** (and B for a second case). Reopen A after saving B → distinct ranges/bindings.

### Research → Strategy

1. Restore the intended reference (A or B), or Bookmark before handoff.
2. **Use in Strategy Builder** — Builder opens with that evidence; Studio stays session-matched.
3. Bind condition id+hash into the draft; add exits/sizing without rewriting the entry condition.
4. Validate under the declared fill model (today: honest L1; queue/liquidity/latency = later engine work).

### What not to do

- Do not gate Order book / Footprint / Bookmap on Rank.
- Do not open another session’s Studio from Builder.
- Do not invent outcome intervals or declare Depth/Tape when the chart only used bars.
- Do not pretend “Chart grid” is multi-chart until real tiles exist.
- Do not add Rank-adjacent tool buttons; put chart tools on the chart.

## Code anchors

| Concern | Location |
|---|---|
| Reference record | `ResearchAnalysisReferenceV1.cs` |
| Save / restore A·B | `StrategyAuthoringViewModel.ResearchDataset.cs` |
| Studio handoff | `UseObservationInDesign` → `StrategyBuilderHandoffRequested` |
| Session-matched Studio | `MainWindow.WireResearchStudioRequest` (DataContext match) |
| Durable Builder handoff | `MainWindow.WireStrategyBuilderHandoff` (handler kept) |
| Chart-owned market views | `ChartsViewModel` `OpenChart*Command` + `ChartsPanel` Views |

## Execution (separate lane)

Nautilus-style behaviour (order-book replay, queue, liquidity, latency, quantity-limited partials) belongs in the **execution engine** and Builder Validate — not in Research reference UI.

Full map of Research · Strategy · Backtest references (in-app types, docs, Nautilus, TSD, brokers):  
[`research-strategy-backtest-reference-map.md`](research-strategy-backtest-reference-map.md).

Phase 5 ownership/Paper: [`Phase5_Execution_Contracts_2026-09-15.md`](Phase5_Execution_Contracts_2026-09-15.md).
