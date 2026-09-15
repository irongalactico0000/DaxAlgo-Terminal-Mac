# Research continuity work matrix (canonical)

Updated: 2026-09-15  
Requirements: [TSD_DaxAlgo_Dolpago_Requirements_v1.md](TSD_DaxAlgo_Dolpago_Requirements_v1.md) (R01–R28, V01–V12, IG-1)

**Row 1 status (honest):**
- Was: overlay **ID** handoff to Builder pending only (lossy EMA bucket).
- Now targeting: `ResearchIndicatorBindingV1` (kind+period) on send **and** on `ResearchEventSampleV1` for save/restore (R05, R12, V02, V03).
- Not yet IG-1.

## Rows …

See requirements doc for full R-map. Work order: finish R05/R12 sample persistence → R07 condition → R09 search → R15/R16 TSD → R11 monitor → R18 promote.

## Rows (existing → modify → I/O → UI check)

| # | Job | Existing | Modify / add | Input → Output | UI done when |
|---|-----|----------|--------------|----------------|--------------|
| 1 | Exact indicator settings with selection + sample | Chart toggles, `SendResearchSelection`, dataset samples | `ResearchIndicatorBindingV1` on event + `ResearchEventSampleV1` | Chart period → binding → sample JSON | V02/V03: EMA(21) survives label+serialize; samples A≠B keep own bindings |
| 2 | Editable condition | Feature names in `StrategyResearchExperimentRunnerV1`; plan W4 feature/rule IDs | `ResearchConditionDefinitionV1` + version hash | Condition params → versioned condition | Change vol multiple 2→3 → version changes; UI shows new version |
| 3 | Search cases by condition | Gallery scan, `ResearchDataset`, experiment runner | Condition evaluator over store (+ later TSD) | Condition + universe → matches + fail cases + forward outcome | Edit condition → counts/chart hits refresh together |
| 4 | TSD role (not deferred) | MDMS `/api/mdms/bars/history`, orderbook analytics; authoring/agent | Research client calling MDMS/analytics for same request shape as local | Same research request → TSD payload → Mac display | Network/trace shows TSD call; numbers match local fixture on shared bars |
| 5 | In-app reference | Dataset + chart image refs + session store | Reference aggregate: range + overlays + conditions + evidence ids | Save → reopen session | No file re-upload; restores range/indicators/conditions/evidence |
| 6 | Live fire? | Same evaluator as #3 | Evaluate on latest bars | Condition version + now → met/not | Same version as historical search |
| 7 | To strategy | Authoring generate / TradeIR | Bind condition ids into draft; no prose reparse | Condition + exits/sizing → draft | Entry condition ids unchanged after add stop/size |

## TSD map (row 4)

| Need | TSD today | Gap |
|------|-----------|-----|
| Bars for range | `GET /api/mdms/bars/history` | Wire from Mac research request |
| LOB/imbalance features | `mdms/orderbook_analytics.py` | No research HTTP surface yet |
| OMS signals | `/api/bridge/*` | **Wrong pipe** for this flow |
| Python kernels / authoring | `/api/authoring`, `/api/agent` | Parallel codegen; not condition search |

## Start order

Implement **#1** first (this session), then #2, then #3 with local store, then #4 TSD for the same contract.

Hyperion-main: decide only after mapping what Mac LLM/codegen already does; not required for #1–3.
