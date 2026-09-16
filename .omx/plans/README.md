# DaxAlgo plans index (reference spine)

**Product organisation (functional, not button layout):**

| Surface | Owns | Does not own |
|---|---|---|
| **Open chart** | Instrument, timeframe, history, indicators, drawings, Order book / Footprint / Bookmap | Ranking, Hyperion chat, strategy rules |
| **Research Studio** | Market screen, Hyperion, compare, **saved references (A/B)**, condition search | Execution fill models |
| **Strategy Builder** | Entry/exit/sizing from a **reference**, Validate, Paper | Re-implementing chart tools |

**Connection between Research and Strategy = a versioned reference**, not shared chrome:

```text
Chart (same component)
  → observation + indicator bindings + optional condition
  → ResearchAnalysisReferenceV1 (Bookmark A / B)
  → Use in Strategy Builder (draft binds condition id + hash)
  → Validate / Paper under an explicit fill model
```

Code: `ResearchAnalysisReferenceV1` · session restore · `UseObservationInDesign` / `BindResearchConditionToDraft`.

## Canon (read in this order)

1. **[`research-strategy-reference-spine.md`](research-strategy-reference-spine.md)** — how Research and Strategy stay linked through references (this is the UI/workflow contract).
2. **[`TSD_DaxAlgo_Dolpago_Requirements_v1.md`](TSD_DaxAlgo_Dolpago_Requirements_v1.md)** — locked G/U/S/C/Q/V product IDs (R11–R12 / U07 = references).
3. **[`TSD_DaxAlgo_Dolpago_Implementation_Checklist.md`](TSD_DaxAlgo_Dolpago_Implementation_Checklist.md)** — progress only; never upgrades a V-scenario without evidence.
4. **[`research-continuity-matrix.md`](research-continuity-matrix.md)** — short pointer into the above.

## Supporting (do not reinvent the spine)

| Doc | Role |
|---|---|
| [`daxalgo-research-design-chart-strategy-workflow.md`](daxalgo-research-design-chart-strategy-workflow.md) | Longer historical workflow plan |
| [`Phase5_Execution_Contracts_2026-09-15.md`](Phase5_Execution_Contracts_2026-09-15.md) | Execution / Nautilus-style contracts (**separate** from Research) |
| [`MS_First_Roadmap_2026-09-15.md`](MS_First_Roadmap_2026-09-15.md) | TSD body → DaxAlgo adapter order |
| [`TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md`](TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md) | Signal / order-candidate fields |
| [`TSD_MS_Source_Audit_2026-09-15.md`](TSD_MS_Source_Audit_2026-09-15.md) | Source audit notes |
| [`Volume_Rehearsal_Evidence_2026-09-15.md`](Volume_Rehearsal_Evidence_2026-09-15.md) | Volume-condition rehearsal evidence |
| [`daxalgo-windows-to-macos-execution-parity.md`](daxalgo-windows-to-macos-execution-parity.md) | Execution parity notes |

## Agent reporting rule

Always: **Spec ID → user action → code path → verification → remaining**.  
Never invent alternate R-number schemes. Prefer restoring **Reference A/B** over re-adding UI buttons.
