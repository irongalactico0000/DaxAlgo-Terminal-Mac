# Phase 5 — Execution contracts & completion criteria (locked)

**Status:** TARGET SPEC (design lock) — not an implementation-complete claim  
**Canon parent:** [`TSD_DaxAlgo_Dolpago_Requirements_v1.md`](./TSD_DaxAlgo_Dolpago_Requirements_v1.md) § S06 / U11–U13 / C08–C10  
**Companion:** [`TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md`](./TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md)

**English framing:** Given the current local implementation, what should we build next, and what must change in this roadmap?

---

## Parallel work rule (replaces “Nothing in Phase 5 blocks Phases 0–4”)

| Track | May proceed? | Gate |
|-------|--------------|------|
| Research / UI / build (Phases 0–4 style) | **Yes** | Keep progressing |
| Generated-code execution | **Only if** OS isolation proven first | Q11 code sandbox |
| Autonomous / auto-order paths | **Only if** Phase 5 ownership, grant, and intake rules apply | S06 + this doc |

Phases 0–4 are **not** assumed fully independent of Phase 5 when they touch codegen or auto-submit.

---

## Six corrections to earlier Phase 5 wording

| Old wording | Correction | Locked requirement |
|-------------|------------|-------------------|
| Every AI mode emits `TradingDecisionV1` | Research-only must **not** invent trade intent | **U11** → Analysis / Condition / Reference only. **U12** → StrategyVersion runs. **U13 / trade judgment** → `TradingDecisionV1` (conceptual; maps to TargetIntent / host admit). |
| TSD endpoint is the single gate | TSD intake is the **shared admit** boundary; execution owner re-checks at submit time | If DaxAlgo OMS executes, DaxAlgo owns pre-trade book/grant/version/risk. TSD TOMS path: TOMS re-checks. **Dual check** — admit ≠ execute authority alone. |
| `10−4−3=3` = retry-safety | That formula is **converge math** only (V09) | Separate: **dedupe**, **revision conflict**, **working reservation**, **restart reconcile** (demo ①–⑦ below). |
| Auto-trade + all intents pending confirm | Confirm-every-intent ≠ autonomy | **Confirm path** (bridge pending) vs **delegated auto path** (inside C10 grant). Validating confirm alone does **not** validate U13. |
| One execution owner per book (in-process only) | Restart / concurrent writers need durable ownership | Persist owner lease; **invalidate prior owner** on transfer; if books share a real account, apply **account-level** risk limits. |
| Sandbox late | Do not run arbitrary generated code then bolt on isolation | **Prove OS isolation before** activating generated strategy code. First Paper integration may use **fixed/declarative** strategy. |

---

## Three product behaviors (must stay distinct)

| User asks | Who re-decides? | What executes |
|-----------|-----------------|---------------|
| Investigate indicators before a breakout | AI research | Analysis job — **no** trade intent required |
| Build strategy from this condition → Paper | Activated **strategy code / condition evaluator** | A specific **StrategyVersion** |
| Manage position inside this grant | **AI re-judges** on schedule/event | TargetIntent tied to grant + version |

Bull/Bear multi-role (TradingAgents) comes **after** this path is green. Roles without clear “who decides / what executes” do not help.

---

## Validity windows (four different clocks)

| Clock | Meaning | Expiry does **not** imply |
|-------|---------|---------------------------|
| Decision admit TTL | How long a judgment may be accepted by host | Auto-flatten of existing position |
| Delegation grant TTL (C10) | How long AI may auto-revise targets | Instant cancel of working orders (policy-defined) |
| Order TIF / TTL | How long a working order stays live | Closing the filled position |
| Position hold policy | How long to keep risk on | Same as decision expiry |

---

## Verification order (apply inside Phase 5; keep phase numbers)

| Step | Work | Pass when |
|------|------|-----------|
| **Now A** | Phase 0 build (`SetResearchChartSelection` wiring) | Clear change set; build + related headless green |
| **Now B** | TSMS `TradingSignal` / `OrderCandidate` ↔ S06 field map | Reuse / extend / gap per field |
| **Next 1** | One symbol · one book · one owner · Paper path | Fixed intent → order → fill → position — **PASS** `tests/test_phase5_next1_paper_path.py` |
| **Next 2** | Dedupe, out-of-order revision, partial fill, restart | Demo ②–⑦ in same file — **PASS** (Stock AAPL ledger restore still Gap; Spot BTC used for ⑥) |
| **Next 3** | Condition evaluator → real intent | Research/monitor/strategy same live verdict + Paper admit — **PASS** `test_phase5_next3_condition_intent` + Mac `ResearchConditionTargetIntentV1Tests` |
| **Next 4** | Single AI changes Paper target inside grant | **PASS (narrow):** `test_phase5_next4_ai_grant.py` — AGENT revises +5→+8 inside grant; owner_lease on paper ledger survives restart; expired grant blocks. Still open: LLM decision object + Avalonia UI evidence chain. |
| **Later** | Generated-code OS isolation + strategy activate | FS / net / secrets / resource limits enforced |
| **Last** | Multi-role vs single AI | Same data+engine+cost; measure judgment quality only |

**First Paper path must be verifiable without AI** so failures split into “judgment” vs “order/state”.

---

## First integration demo (single symbol) — completion criteria

| # | Input / state | Expected |
|---|----------------|----------|
| ① | Target `+10`, filled `+4`, working buy `+3` | Needed buy `+3` (converge math) |
| ② | `+3` submit in flight | Quantity **reserved**; second processor cannot re-submit same exposure |
| ③ | Same dedupe id resent | Return prior result; **no** new order |
| ④ | Same id, target changes to `+12` | **Conflict reject** |
| ⑤ | Newer revision, then older revision arrives | Older **not** applied |
| ⑥ | Lose response after submit; restart | Reconcile existing orders; **no** blind resubmit |
| ⑦ | Grant expired or execution owner changed | Block new risk-increasing exposure; open orders/positions follow **pre-declared** policy |

Local agent scope for now: **build recovery → field map → single-book Paper + recovery proofs**.  
We lock here: **three behaviors, ownership, confirm vs grant, intent lifetime**.  
TradingAgents role expansion: **after** this path passes.
