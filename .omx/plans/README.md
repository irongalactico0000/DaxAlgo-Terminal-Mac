# DaxAlgo plans index (reference spine)

**Product organisation (functional, not button layout):**

| Surface | Owns | Does not own |
|---|---|---|
| **Open chart** | Instrument, timeframe, history, indicators, drawings, Order book / Footprint / Bookmap | Ranking, Hyperion chat, strategy rules |
| **Research Studio** | Market screen, Hyperion, compare, **saved findings**, condition search | Execution fill models |
| **Strategy Builder** | Entry/exit/sizing/risk from a **saved finding** (or start from rules), Validate, Paper | Re-implementing chart tools |

**North star:** Complete multi-chart indicator research and its **handoff to Strategy Builder**; develop and verify **realistic execution simulation alongside it**.

**Connection Research → Strategy = saved finding → editable executable rules** (calculation, threshold, evaluation time, entry, exit, sizing, risk) — not shared chrome and not “three charts alone”:

```text
Chart (same component)
  → observation + indicator bindings + optional condition
  → saved finding (ResearchAnalysisReferenceV1)
  → Use in Strategy Builder (draft binds condition id + hash + rules)
  → Validate / Paper under an explicit fill model
```

Three parallel workstreams: [`workstreams-research-builder-validate.md`](workstreams-research-builder-validate.md).  
Validate may use fixed strategy + deterministic fixtures **without** waiting for multi-chart UI.

Code: `ResearchAnalysisReferenceV1` · session restore · `UseObservationInDesign` / `BindResearchConditionToDraft`.  
**Product language:** say **saved finding**, not “Reference A/B”.

## Canon (read in this order)

1. **[`design-workspace-authoring-problem-2026-09-17.md`](design-workspace-authoring-problem-2026-09-17.md)** — main remaining issue: Design rules as center + shared draft vs Hyperion prompt.
2. **[`workstreams-research-builder-validate.md`](workstreams-research-builder-validate.md)** — Research / Builder / event-driven Validate criteria + IOC fixture.
3. **[`task-to-interface-map.md`](task-to-interface-map.md)** — what users accomplish in Research vs Builder; outputs; local gaps.
4. **[`interaction-states-wireframes.md`](interaction-states-wireframes.md)** — empty/loading/success/unavailable/restored states + a11y.
5. **[`acceptance-journey-research-to-validate.md`](acceptance-journey-research-to-validate.md)** — runnable Mac checklist.
6. **[`research-strategy-reference-spine.md`](research-strategy-reference-spine.md)** — Research↔Strategy via **saved findings**.
7. **[`research-strategy-backtest-reference-map.md`](research-strategy-backtest-reference-map.md)** — lanes + fill fidelity.
8. **[`TSD_DaxAlgo_Dolpago_Requirements_v1.md`](TSD_DaxAlgo_Dolpago_Requirements_v1.md)** — locked G/U/S/C/Q/V product IDs.
9. **[`TSD_DaxAlgo_Dolpago_Implementation_Checklist.md`](TSD_DaxAlgo_Dolpago_Implementation_Checklist.md)** — progress only.
10. **[`research-continuity-matrix.md`](research-continuity-matrix.md)** — short pointer.

## Immediate design targets

1. Multi-chart research that produces **numerical** findings (shared indicators, failed cases, Hyperion on calculated results).  
2. **Handoff:** finding → editable executable rules in Builder.  
3. **Validate (parallel):** event-driven models proven by fixtures (IOC limit case), while UI stays L1-honest until selectable.

Success = acceptance journey + fixture evidence — no invented % probabilities.

## Supporting (do not reinvent the spine)

| Doc | Role |
|---|---|
| [`daxalgo-research-design-chart-strategy-workflow.md`](daxalgo-research-design-chart-strategy-workflow.md) | Longer historical workflow plan |
| [`Phase5_Execution_Contracts_2026-09-15.md`](Phase5_Execution_Contracts_2026-09-15.md) | Ownership / Paper / grant contracts |
| [`MS_First_Roadmap_2026-09-15.md`](MS_First_Roadmap_2026-09-15.md) | TSD body → DaxAlgo adapter order |
| [`TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md`](TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md) | Signal / order-candidate fields |
| [`TSD_MS_Source_Audit_2026-09-15.md`](TSD_MS_Source_Audit_2026-09-15.md) | Source audit notes |
| [`Volume_Rehearsal_Evidence_2026-09-15.md`](Volume_Rehearsal_Evidence_2026-09-15.md) | Volume-condition rehearsal + Nautilus posture |
| [`component-roles-csp-vibequant-nautilus-2026-09-17.md`](component-roles-csp-vibequant-nautilus-2026-09-17.md) | CSP / VibeQuant / Nautilus vs L1 Validate roles |
| [`daxalgo-windows-to-macos-execution-parity.md`](daxalgo-windows-to-macos-execution-parity.md) | Execution lifecycle parity checklist |

## Agent reporting rule

Always: **Spec ID → user action → code path → verification → remaining**.  
Never invent alternate R-number schemes. Prefer restoring a **saved finding** over re-adding UI buttons. Never lead with “Reference A/B” in user-facing text.
