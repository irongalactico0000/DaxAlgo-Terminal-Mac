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
│ Strategy Builder            │  finding → editable rules:
│                             │  calculation · threshold · eval time
│                             │  entry · exit · sizing · risk
│                             │  Validate · Paper (fill model explicit)
└─────────────────────────────┘
```

Three charts without that rule package ≠ Research→Strategy done.  
Parallel Validate criteria: [`workstreams-research-builder-validate.md`](workstreams-research-builder-validate.md).

## User journeys (acceptance)

### Research-only (no strategy yet)

1. Open chart (Charts or Rank → Open chart).
2. Toggle / develop indicators on **that** chart (shared defs across Compare tiles when comparing).
3. Ask Hyperion an analytical question (must explain **calculated** results, not only “overlay applied”).
4. Optionally select observation + outcome; Apply condition; Find similar (same condition — never silent next-day +5%). Include failing cases when claiming a pattern.
5. **Save finding** (and a second finding if you need contrast). Restoring the first after the second must keep distinct ranges/bindings.

### Research → Strategy

1. Restore the intended **saved finding**, or save before handoff.
2. **Use in Strategy Builder** — Builder opens with that evidence; Studio stays session-matched.
3. Turn the finding into an **editable executable rule**: calculation, threshold, evaluation time, entry, exit, sizing, risk. Bind condition id+hash; do not rewrite entry when adding exits.
4. Validate under the declared fill model (today: L1 + optional capped partials + latency ms). Event-driven book/queue/liquidity = separate workstream with fixtures.

### What not to do

- Do not gate Order book / Footprint / Bookmap on Rank.
- Do not open another session’s Studio from Builder.
- Do not invent outcome intervals or declare Depth/Tape when the chart only used bars.
- Do not treat multi-chart tiles alone as Research→Strategy handoff.
- Do not add Rank-adjacent tool buttons; put chart tools on the chart.
- Do not say “Reference A/B” in user-facing copy.
- Design → TradeIR: **Use rules as request** copies rules into the composer only — Build still required; no invented TradeIR.
- Do not mark event-driven Validate done because an Order book window exists.

## Code anchors

| Concern | Location |
|---|---|
| Saved finding record | `ResearchAnalysisReferenceV1.cs` |
| Save / restore slots | `StrategyAuthoringViewModel.ResearchDataset.cs` |
| Studio handoff | `UseObservationInDesign` → `StrategyBuilderHandoffRequested` |
| Session-matched Studio | `MainWindow.WireResearchStudioRequest` |
| Durable Builder handoff | `MainWindow.WireStrategyBuilderHandoff` |
| Chart-owned market views | `ChartsViewModel` `OpenChart*Command` |

## Execution (separate parallel lane)

Event-driven execution validation belongs in Validate/Paper — not Research UI. May proceed with a fixed strategy + deterministic fixtures while Research multi-chart continues.

Full lane map: [`research-strategy-backtest-reference-map.md`](research-strategy-backtest-reference-map.md).  
Workstreams: [`workstreams-research-builder-validate.md`](workstreams-research-builder-validate.md).  
Phase 5: [`Phase5_Execution_Contracts_2026-09-15.md`](Phase5_Execution_Contracts_2026-09-15.md).
