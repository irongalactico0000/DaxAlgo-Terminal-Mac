# TSMS TradingSignal / OrderCandidate ↔ S06 field map

**Date:** 2026-09-15  
**Source:** `/Users/w/Developer/tsd/src/tsd/tsms/signals.py`  
**Canon:** S06 TargetIntent (C08) / OrderIntent (C09) / DelegationGrant (C10)  
**Phase 5:** [`Phase5_Execution_Contracts_2026-09-15.md`](./Phase5_Execution_Contracts_2026-09-15.md)

Legend: **Reuse** = map as-is · **Extend** = keep type, add fields · **Gap** = missing for S06 · **Do not** = must not be treated as trade decision alone

---

## TradingSignal

| Field | Role today | S06 mapping | Action |
|-------|------------|-------------|--------|
| `signal_id` | Identity | Dedupe / causation id seed | **Reuse** → intent/decision id lineage |
| `strategy_id` | Strategy | StrategyVersion id (partial) | **Extend** — need explicit `strategy_version` / hash |
| `timestamp` | Event time | Decision time (not grant TTL) | **Reuse** + separate grant/order clocks |
| `instrument` / `exchange` | Venue | TargetIntent instrument/venue | **Reuse** |
| `signal_type` / `side` | Directional enum | Maps poorly to net target | **Extend** — prefer explicit `target_quantity` for U12/U13 |
| `confidence` / `urgency` | Ensemble weights | Judgment metadata only | **Reuse** as evidence; **Do not** bypass risk |
| `target_quantity` / `target_notional` | Optional sizing | **TargetIntent** core | **Reuse** when set; require exactly one sizing mode |
| `bid/ask/mid` | Context | Research evidence | **Reuse** for audit; not execution price alone |
| `metadata` | Bag | Grant id, condition version, revision | **Extend** — promote typed keys out of bag |

**Verdict:** Good research/ops signal. For U13, promote to **TargetIntent** via host (not TOMS direct). Research-only (U11) may emit signals **without** creating `TradingDecisionV1` / intents.

---

## OrderCandidate

| Field | Role today | S06 mapping | Action |
|-------|------------|-------------|--------|
| `candidate_id` / `source_signal_id` | Lineage | OrderIntent id + parent decision | **Reuse** |
| `timestamp` | Create time | Order admit time | **Reuse** |
| `instrument` / `exchange` / `side` | Order | OrderIntent | **Reuse** |
| `order_type` / `quantity` / `price` / `stop_price` | Order params | OrderIntent / QuoteIntent | **Reuse** — **capability-gated** (C09) |
| `time_in_force` | Order TTL | Order TIF clock (≠ grant TTL) | **Reuse** |
| `score` / component scores | N-way select | Ranking metadata | **Reuse**; not risk authority |
| `passed_filters` / `filter_reasons` | Soft filters | Pre-host hints | **Do not** replace TOMS/OMS hard risk (Q10) |
| `expected_slippage` / `fill_ratio` | Estimates | Audit | **Reuse** |

**Gap vs S06:** No `book_id`, `execution_owner`, `intent_version`, `dedupe_key`, `working_reservation`, `grant_id`. Those belong on **host admit** + execution owner, not only on the candidate.

**Verdict:** Natural carrier for **OrderIntent** after grant. Must still pass HostIntentIntake + designated owner risk. Not a substitute for TargetIntent converge math.

---

## StrategyDecision

| Field | Role today | S06 mapping | Action |
|-------|------------|-------------|--------|
| `decision_id` | Bundle id | Closest to conceptual **TradingDecisionV1** | **Extend** — rename/alias when trade judgment fires |
| `selected_candidates` | Orders to fire | OrderIntents | **Gap** — also support TargetIntent-only decisions (no candidates) |
| `rejected_candidates` | Audit | Admit denies | **Reuse** |

**Rule:** Emit StrategyDecision / TradingDecisionV1 **only at trade-judgment time** (U12 activation tick or U13 re-judge). Not for pure research analysis.

---

## Host / execution owner fields (missing in TSMS DTOs — required)

| Requirement | Where to own |
|-------------|--------------|
| `book_id` + durable execution owner lease | Host + OMS/TOMS (persist; invalidate prior owner) |
| `intent_id` + `intent_version` + dedupe | HostIntentIntake (V09/V08 demo ③–⑤) |
| filled + working reservation | PPMS + TOMS open orders → reconcile |
| grant id / TTL / allow_order_intent | C10 DelegationGrant |
| condition_version / data_version | C03/C04 through to C07 |

---

## Recommended wire path

```text
U11 research     → Analysis/Condition/Reference          (no TradingDecisionV1 required)
U12 strategy tick→ TradingSignal (target_*) → Host TargetIntent → owner
U13 AI re-judge  → TradingDecisionV1 / TargetIntent      → Host → owner
MM / fine exec   → OrderCandidate → Host OrderIntent     → owner (grant.allow_order_intent)
```
