# Research · Strategy · Backtest — reference map (incl. Nautilus)

**Status:** Living index (2026-09-17) — organise work by **references**, not by extra UI chrome  
**Read with:** [`README.md`](README.md) · [`research-strategy-reference-spine.md`](research-strategy-reference-spine.md)  
**Canon IDs:** Dolpago G/U/S/C/Q/V · R01–R28 (do not invent alternate schemes)

This document ties three product lanes and the external / engine references that matter for each.

```text
Research ──(Reference A/B)──► Strategy ──(StrategyVersion + fill model)──► Backtest / Validate / Paper
                                                                              │
                                                                              ▼
                                                                    Execution fidelity
                                                                    (today L1 · later Nautilus-class)
```

---

## 1. What each lane owns

| Lane | Primary job | Handoff unit | Must not pretend to own |
|------|-------------|--------------|-------------------------|
| **Research** | Screen → open chart → indicators → Hyperion → Find similar → **Bookmark A/B** | `ResearchAnalysisReferenceV1` | Fill models, queue, OMS |
| **Strategy** | Turn a **reference** into entry / exit / size / risk rules | `StrategyDraftV1` + condition id/hash → `StrategyVersion` | Re-building chart tools |
| **Backtest / Validate** | Replay the **same** version under a **declared** execution model | RunSpec / validation evidence hash | Silent “realistic” fills without disclosure |

Shared chart component across Research and Builder. Chart tools (indicators, Order book, Footprint, Bookmap) live on the **open chart**, not on Rank.

---

## 2. Reference catalogue (all important)

### 2.1 Product / research references (in-app)

| Reference | Schema / type | Why it matters |
|-----------|---------------|----------------|
| **Reference A / B** | `ResearchAnalysisReferenceV1` | Exact selection + indicator bindings + condition version + optional search hits. **Research↔Strategy spine.** |
| Chart selection | `ResearchChartSelectionV1` | Instrument, TF, observation/outcome ranges, declared data requirements |
| Indicator binding | `ResearchIndicatorBindingV1` | Kind, period, version — not display-name-only |
| Condition | `ResearchConditionDefinitionV1` | Editable params; version hash bumps on edit (U05/V05) |
| Condition search | `ResearchConditionSearchResultV1` | Universe, hits, fails, forward outcomes (R09/U06) |
| Event sample / dataset | `ResearchEventSampleV1` / `ResearchDatasetDefinitionV1` | Labeled evidence; leakage policy |
| Strategy draft | `StrategyDraftV1` | Binds condition id+hash without rewriting entry when adding exits |
| Workspace | `StrategyWorkspaceV1` | Version chain; stale evidence when upstream changes |

### 2.2 Spec / plan references (docs)

| Doc | Lane | Role |
|-----|------|------|
| [`TSD_DaxAlgo_Dolpago_Requirements_v1.md`](TSD_DaxAlgo_Dolpago_Requirements_v1.md) | All | Locked product canon (G/U/S/C/Q/V, R01–R28) |
| [`research-strategy-reference-spine.md`](research-strategy-reference-spine.md) | Research↔Strategy | UI/workflow contract via A/B |
| [`TSD_DaxAlgo_Dolpago_Implementation_Checklist.md`](TSD_DaxAlgo_Dolpago_Implementation_Checklist.md) | All | Progress only — never upgrades V without evidence |
| [`Phase5_Execution_Contracts_2026-09-15.md`](Phase5_Execution_Contracts_2026-09-15.md) | Strategy→Paper | Ownership, grant, confirm vs auto, Paper demos |
| [`TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md`](TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md) | Strategy→OMS | Signal / OrderCandidate field map |
| [`daxalgo-windows-to-macos-execution-parity.md`](daxalgo-windows-to-macos-execution-parity.md) | Backtest/OMS | Lifecycle, partials, restart, broker parity checklist |
| [`daxalgo-research-design-chart-strategy-workflow.md`](daxalgo-research-design-chart-strategy-workflow.md) | Research/Design | Longer event-study workflow |
| [`Volume_Rehearsal_Evidence_2026-09-15.md`](Volume_Rehearsal_Evidence_2026-09-15.md) | Research | First rehearsal evidence + Nautilus posture |
| [`MS_First_Roadmap_2026-09-15.md`](MS_First_Roadmap_2026-09-15.md) | TSD | MS-first build order |
| [`../docs/how-a-strategy-gets-in.md`](../../docs/how-a-strategy-gets-in.md) | Strategy | How strategies enter the Mac app |
| [`../docs/chart-strategy-execution-requirements.md`](../../docs/chart-strategy-execution-requirements.md) | Chart→Exec | Chart / strategy / execution requirements |
| [`../docs/execution-oms-split.md`](../../docs/execution-oms-split.md) | OMS | Execution vs OMS split |
| [`../docs/research/vibe-quant-four-lane-runtime-benchmark.md`](../../docs/research/vibe-quant-four-lane-runtime-benchmark.md) | Backtest | Engine comparison incl. NautilusTrader |

### 2.3 External / engine references

| Reference | What it is | Role for DaxAlgo |
|-----------|------------|------------------|
| **NautilusTrader** | Rust+Python trading/backtest engine: data + exec clients, order lifecycle, book-aware matching options | **Target class** for advanced fill fidelity (queue, liquidity, latency, realistic partials). Optional TSD `integrations/nautilus/` — **not** installed/wired into Mac Paper path today. Canon: S06-A mediator first; Nautilus = later S06-B verb surface behind it — never AI→venue. |
| **LEAN / QuantConnect** | Event-driven backtest + live | Comparison reference in four-lane benchmark docs |
| **vectorbt / backtesting.py / Freqtrade** | Research/backtest tooling | Benchmark / inspiration only — not Mac runtime |
| **TSD (MDMS / TSMS / TOMS / PPMS)** | Market data, signals, order admit, paper | Shared desk context; Mac adapter — see MS-first + Phase 5 |
| **IB / Alpaca / cTrader** | Live/Paper brokers | Real OMS paths; Binance stays market-data only |

---

## 3. Research lane — references in practice

**Entry:** Charts menu **or** Market Screen → Rank → Open chart (same chart component).

| Step | Reference you create / use | Done when |
|------|----------------------------|-----------|
| Open instrument | Chart `SelectedInstrument` bound; Hyperion follows | Tools work **without** Rank |
| Indicators | `ResearchIndicatorBindingV1` on chart | Inspectable formula/params |
| Observation (+ outcome if studying return) | `ResearchChartSelectionV1` | Outcome **not** invented |
| Condition | `ResearchConditionDefinitionV1` | Version hash stable until params change |
| Find similar | Same condition version | **No** silent next-day +5% fallback |
| Bookmark | **Reference A / B** | Restore A after B keeps distinct context |
| Handoff | Reference required for Builder | `UseObservationInDesign` creates/uses A |

**Do not:** gate Order book on Rank; mix sessions; declare Depth/Tape without using them.

---

## 4. Strategy lane — references in practice

| Step | Reference | Done when |
|------|-----------|-----------|
| Open from Research | Restored **Reference A/B** | Same condition id + indicator bindings visible |
| Bind to draft | Condition id + `VersionHashSha256` on `StrategyDraftV1` | Entry condition unchanged when adding exits (R18–R19) |
| Build / register | `StrategyVersion` / artifact hash | Workspace bindings point at this revision |
| Paper admit | StrategyVersion + book ownership (Phase 5) | Dual check: admit ≠ execute alone |

Strategy Builder **consumes** research references. It does not re-host Market Screen chrome.

---

## 5. Backtest / Validate — fidelity references

### 5.1 What Mac applies today (honest)

| Setting | Applied? | Implementation |
|---------|----------|----------------|
| L1 quotes | **Yes** | `L1TouchFillModel` |
| Market / limit / stop (basic) | **Yes** | Full remaining qty at touch ± slippage ticks |
| Latency ≠ 0 ms | **No** | UI shows unavailable; engine uses 0 |
| Queue position | **No** | Unavailable |
| Liquidity consumption / book walk | **No** | Unavailable |
| True quantity-limited partials | **No** | Basic lifecycle exists; default fill is full remaining |
| L2/L3 book replay synced to Validate | **No** | Research Order book windows are live/prefer-symbol views, not Validate replay |

UI truth: `StrategyAuthoringViewModel.Validation` — only L1 options selectable.  
Engine: `TradingTerminal.Backtest.Engine/Execution/IFillModel.cs`, `BacktestEngine` rejects non-`L1Touch`.

### 5.2 Nautilus-class target (later, separate deliverable)

Use this as the **acceptance checklist** when implementing advanced fidelity — not as a claim of current behaviour:

| Behaviour | Example acceptance |
|-----------|-------------------|
| Order-book replay | Validate consumes depth stream aligned to replay clock |
| Market fills | Size limited by available liquidity at levels, not always full remainder |
| Limit orders | Touch/cross + queue assumptions explicit |
| Queue position | Join/improve/cancel updates position; fills only when ahead qty clears |
| Liquidity consumption | Fills reduce book; subsequent orders see residual |
| Execution latency | Configured ms delay between decision and venue apply |
| Partial / lifecycle | e.g. limit 10 → fill 4 → remaining 6 → cancel → position 4 |

**Wiring rule:** Advanced model is chosen explicitly on Validate/Paper; Research references stay about **signals/conditions**, not fill physics. Prefer adapting Nautilus (or equivalent) **behind** the same StrategyVersion + book ownership contracts (Phase 5 / S06).

### 5.3 Lifecycle references (parity doc)

For ack / partial / cancel / replace / restart: [`daxalgo-windows-to-macos-execution-parity.md`](daxalgo-windows-to-macos-execution-parity.md) (E-series).  
For Paper ownership demos: [`Phase5_Execution_Contracts_2026-09-15.md`](Phase5_Execution_Contracts_2026-09-15.md) Next 1–4.

---

## 6. One end-to-end path (acceptance narrative)

1. **Research:** Open BTCUSD (or ranked row) → indicators on chart → Hyperion explains behaviour → Apply condition → Find similar → **Bookmark A**.  
2. **Optional:** Bookmark B as a failing / contrast case; restore A.  
3. **Strategy:** Use in Strategy Builder (reference attached) → bind condition → add exit/size.  
4. **Backtest:** Validate with **L1 disclosed** until a Nautilus-class model is selectable and proven.  
5. **Paper:** Same StrategyVersion on one book, one owner (Phase 5).

Failure to keep the same reference / version hash across steps = broken product meaning.

---

## 7. Agent / PR reporting template

`Lane` → `Spec ID (U/R/V/C/S)` → `Reference touched (A/B, StrategyVersion, FillModel)` → `Code path` → `Evidence` → `Remaining`

Never mark Nautilus / queue / latency **done** because Order book UI exists.  
Never mark Research **done** because Builder chrome was rearranged.
