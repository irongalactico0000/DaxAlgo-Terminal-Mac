# TSD × DaxAlgo × Dolpago — Requirements v1

**Status:** Locked product scope for agents  
**Canon sources:** `/Users/w/Developer/DaxAlgo-Terminal-Mac` (local; may be ahead of GitHub) · `/Users/w/Developer/tsd` (local Sep)  
**Do not treat as done:** docs alone, overlay-ID handoff alone, build success, OMS `/api/bridge` smoke  
**First integration gate (IG-1):** chart selection → **real TSD analysis** → condition search → success/fail → in-app save → edit condition → re-search  

**Product one-liner:**  
DaxAlgo shows the market; Hyperion/Vibe Quant helps investigate indicators and conditions; TSD supplies data/analysis for case compare; research is saved and monitored; the **same** condition becomes a visualizer and/or executable strategy.

Related plans (extend, don’t reinvent):  
- `.omx/plans/daxalgo-research-design-chart-strategy-workflow.md`  
- `.omx/plans/research-continuity-matrix.md`  
- Window/visualizer: `daxalgo-window-eng-requirements` (catalog → open → paint)

---

## Requirement IDs (R01–R28)

| ID | Area | User can… | Done only when |
|----|------|-----------|----------------|
| R01 | Visualizer | Ask for panes (e.g. two books + heatmap) | Artifact in catalog **and** window paints live/sim data |
| R02 | Visualizer | Open saved visualizer from catalog | Same bindings as at save |
| R03 | Situation | Brush observation + outcome on chart | Selection reaches Builder with symbol/TF/ranges |
| R04 | Situation | Ask “what changed before this rise?” | Multiple indicators on that range + compare windows |
| R05 | Indicators | Change period/inputs/normalization | Chart, analysis, and search use **identical** settings |
| R06 | Indicators | Use user-defined indicators | Period/kind versioned; not filename-only |
| R07 | Conditions | Define editable condition (e.g. vol ≥ k×SMA) | Version changes when params change |
| R08 | Conditions | AND/OR combine conditions | Semantics + evaluation time explicit |
| R09 | History search | “Where else did this fire?” | Universe size, hits, fails, forward outcomes |
| R10 | History search | Distinguish P(feat\|outcome) vs P(outcome\|feat) | Both shown; no single-window “proof” |
| R11 | Reference | Save analysis in-app | No mandatory local file re-upload |
| R12 | Reference | Reopen reference A after saving B | A and B keep **their own** ranges/settings/conditions |
| R13 | Monitor | “Which symbols meet this now?” | Met / not met / insufficient data |
| R14 | Monitor | Same condition version as historical search | Shared evaluator |
| R15 | TSD | Bars/history for research range | Request → TSD MDMS → chart/analysis |
| R16 | TSD | Analytics (imbalance, etc.) when needed | Request → TSD compute → Mac display |
| R17 | TSD | Soft-fail if TSD down for US desk | DaxAlgo still usable; research TSD steps show unavailable |
| R18 | Strategy | Promote condition → draft | Entry condition ids preserved |
| R19 | Strategy | Add size/exit/order rules | Entry condition unchanged |
| R20 | Strategy | Visualizer shares feature/rule ids with strategy | Draw uses same ids as targets |
| R21 | Validate | Historical validation on exact revision | Stale revision rejected |
| R22 | Paper | Run exact revision on Paper book | Orders→fills→position→P&L |
| R23 | Safety | Outcome window never enters features | Leakage policy enforced |
| R24 | Safety | No dual order boss on same book | Model A/C ownership respected |
| R25 | Entry points | Start from chart, condition, or reference | Not forced linear wizard |
| R26 | Hyperion | NL → structured research/authoring request | Does not silently replace condition A with B |
| R27 | Provenance | Hash/version for dataset, indicators, conditions | Restart restores exact versions |
| R28 | Evidence | Every agent todo cites R-id + I/O + proof | No “integrated” without IG scenario |

---

## Verification scenarios (V01–V12)

| ID | Scenario | Pass |
|----|----------|------|
| V01 | Brush → Builder | Ranges + **exact** indicator settings visible |
| V02 | Non-default EMA period (e.g. 21) | Builder + calc use 21, not coerced to 20/50 |
| V03 | Label sample A, change indicators, label B, reopen | A and B restore distinct bindings |
| V04 | Situation study | Pre-rise indicators vs compare window |
| V05 | Condition edit 2→3 | Version bump; hit counts refresh |
| V06 | History search | Universe, hits, fails, forward returns |
| V07 | Reference reopen | No file upload; full context |
| V08 | Live monitor | Same condition version |
| V09 | TSD bars path | Trace shows TSD call; values match fixture |
| V10 | TSD analytics path | Trace + display |
| V11 | Promote to strategy | Entry condition ids stable after exits/size |
| V12 | Paper path | Exact revision fills + P&L |

**IG-1** = V01 + V03 + V05 + V06 + V07 + V09 (minimum first integration).

---

## Agent todo template (mandatory)

`Rxx` → user action → existing functions to touch → input/output → evidence (Vyy or screenshot/trace)

---

## Row 1 honesty (2026-09-15)

| Claim | Reality |
|-------|---------|
| “Indicator settings preserved” | **Too wide** — only host overlay **IDs** were handed to Builder pending state |
| EMA period | Chart allows arbitrary `EmaPeriod`; ID map collapsed to ema-20 / ema-50 → **lossy** |
| Sample persistence | Pending overlays cleared on label; **not** written onto `ResearchEventSampleV1` |

Next code: exact `ResearchIndicatorBindingV1` + store on sample (R05, R12, V02, V03).
