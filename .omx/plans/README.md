# DaxAlgo plans index (reference spine)

**Product organisation (functional, not button layout):**

| Surface | Owns | Does not own |
|---|---|---|
| **Open chart** | Instrument, timeframe, history, indicators, drawings, Order book / Footprint / Bookmap | Ranking, Hyperion chat, strategy rules |
| **Research Studio** | Market screen, Hyperion, compare, **saved findings**, condition search | Execution fill models |
| **Strategy Builder** | Entry/exit/sizing from a **saved finding**, Validate, Paper | Re-implementing chart tools |

**Connection between Research and Strategy = a saved finding**, not shared chrome:

```text
Chart (same component)
  → observation + indicator bindings + optional condition
  → saved finding (ResearchAnalysisReferenceV1)
  → Use in Strategy Builder (draft binds condition id + hash)
  → Validate / Paper under an explicit fill model
```

Code: `ResearchAnalysisReferenceV1` · session restore · `UseObservationInDesign` / `BindResearchConditionToDraft`.  
**Product language:** say **saved finding**, not “Reference A/B”.

## Canon (read in this order)

1. **[`task-to-interface-map.md`](task-to-interface-map.md)** — what users accomplish in Research vs Builder; outputs; local gaps (Design conflict).
2. **[`interaction-states-wireframes.md`](interaction-states-wireframes.md)** — empty/loading/success/unavailable/restored states + a11y.
3. **[`acceptance-journey-research-to-validate.md`](acceptance-journey-research-to-validate.md)** — runnable Mac checklist.
4. **[`research-strategy-reference-spine.md`](research-strategy-reference-spine.md)** — Research↔Strategy via **saved findings**.
5. **[`research-strategy-backtest-reference-map.md`](research-strategy-backtest-reference-map.md)** — lanes + Nautilus (terminology first).
6. **[`TSD_DaxAlgo_Dolpago_Requirements_v1.md`](TSD_DaxAlgo_Dolpago_Requirements_v1.md)** — locked G/U/S/C/Q/V product IDs.
7. **[`TSD_DaxAlgo_Dolpago_Implementation_Checklist.md`](TSD_DaxAlgo_Dolpago_Implementation_Checklist.md)** — progress only.
8. **[`research-continuity-matrix.md`](research-continuity-matrix.md)** — short pointer.

## Immediate design targets

1. Usable **Research** chart / comparison screen (compare may be blocked until real tiles).  
2. Usable Builder **Design = rule editor** (not “Open Research” as Design home).

Success = acceptance journey completion — no invented % probabilities.

## Supporting (do not reinvent the spine)

| Doc | Role |
|---|---|
| [`daxalgo-research-design-chart-strategy-workflow.md`](daxalgo-research-design-chart-strategy-workflow.md) | Longer historical workflow plan |
| [`Phase5_Execution_Contracts_2026-09-15.md`](Phase5_Execution_Contracts_2026-09-15.md) | Ownership / Paper / grant contracts |
| [`MS_First_Roadmap_2026-09-15.md`](MS_First_Roadmap_2026-09-15.md) | TSD body → DaxAlgo adapter order |
| [`TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md`](TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md) | Signal / order-candidate fields |
| [`TSD_MS_Source_Audit_2026-09-15.md`](TSD_MS_Source_Audit_2026-09-15.md) | Source audit notes |
| [`Volume_Rehearsal_Evidence_2026-09-15.md`](Volume_Rehearsal_Evidence_2026-09-15.md) | Volume-condition rehearsal + Nautilus posture |
| [`daxalgo-windows-to-macos-execution-parity.md`](daxalgo-windows-to-macos-execution-parity.md) | Execution lifecycle parity checklist |

## Agent reporting rule

Always: **Spec ID → user action → code path → verification → remaining**.  
Never invent alternate R-number schemes. Prefer restoring a **saved finding** over re-adding UI buttons. Never lead with “Reference A/B” in user-facing text.
