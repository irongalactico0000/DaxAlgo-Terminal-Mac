# Terminology (read first)

| Say this (product) | Do **not** lead with | What it actually is |
|--------------------|----------------------|---------------------|
| **Saved finding** | “Reference A/B” | One kept research case: chart instrument/period, indicator settings, optional condition + search hits. You can keep **two** findings to compare (working vs failing). |
| **Open chart** | — | Shared chart workspace (indicators + market views). |
| **Strategy draft / version** | — | Trading rules built **from a saved finding**. |
| **Fill model** | Calling L1 “Nautilus” | How Validate/Backtest turns orders into fills. **Today: L1 touch only.** Nautilus-class = later target. |
| **Doc index / lane map** | “Reference map” as product UI | Catalogue of specs and engines (this file). |

**Code names (engineers only):** type `ResearchAnalysisReferenceV1`; UI commands still use slots labeled A and B as *storage keys*, not product vocabulary. Canon R11–R12 say “reference” in the Dolpago spec — prefer **saved finding** in Mac UI and agent prose.

---

# Research · Strategy · Backtest — lane map (incl. Nautilus)

**Status:** Living index (2026-09-17) — organise by **saved findings**, not extra chrome  
**Read with:** [`README.md`](README.md) · [`research-strategy-reference-spine.md`](research-strategy-reference-spine.md)  
**Canon IDs:** Dolpago G/U/S/C/Q/V · R01–R28 (do not invent alternate schemes)

```text
Research ──(saved finding)──► Strategy ──(strategy version + fill model)──► Backtest / Validate / Paper
                                                                              │
                                                                              ▼
                                                                    Execution fidelity
                                                                    (today L1 · later Nautilus-class)
```

---

## 1. What each lane owns

| Lane | Primary job | Handoff unit | Must not pretend to own |
|------|-------------|--------------|-------------------------|
| **Research** | Screen → open chart → indicators → Hyperion → Find similar → **save finding** | Saved finding (`ResearchAnalysisReferenceV1`) | Fill models, queue, OMS |
| **Strategy** | Turn a **saved finding** into entry / exit / size / risk | `StrategyDraftV1` + condition id/hash → strategy version | Re-building chart tools |
| **Backtest / Validate** | Replay the **same** version under a **declared** fill model | RunSpec / validation evidence hash | Silent “realistic” fills without disclosure |

Shared chart across Research and Builder. Chart tools live on the **open chart**, not on Rank.

---

## 2. Catalogue of important links

### 2.1 In-app research objects

| Product name | Type | Why it matters |
|--------------|------|----------------|
| **Saved finding** | `ResearchAnalysisReferenceV1` | Selection + indicator bindings + condition version + optional hits. **Handoff to Strategy.** Two slots exist so you can keep a second finding for contrast — not because “A/B” is a brand. |
| Chart selection | `ResearchChartSelectionV1` | Instrument, TF, observation/outcome ranges, data requirements |
| Indicator binding | `ResearchIndicatorBindingV1` | Kind, period, version |
| Condition | `ResearchConditionDefinitionV1` | Editable params; version hash on edit |
| Condition search result | `ResearchConditionSearchResultV1` | Universe, hits, fails, forward outcomes |
| Event sample / dataset | `ResearchEventSampleV1` / `ResearchDatasetDefinitionV1` | Labeled evidence |
| Strategy draft / version | `StrategyDraftV1` → registered version | Rules from the finding; entry condition id stable when adding exits |
| Workspace | `StrategyWorkspaceV1` | Version chain; stale when upstream changes |

### 2.2 Spec / plan docs

| Doc | Lane | Role |
|-----|------|------|
| [`TSD_DaxAlgo_Dolpago_Requirements_v1.md`](TSD_DaxAlgo_Dolpago_Requirements_v1.md) | All | Locked product canon |
| [`research-strategy-reference-spine.md`](research-strategy-reference-spine.md) | Research↔Strategy | Workflow via **saved findings** |
| [`TSD_DaxAlgo_Dolpago_Implementation_Checklist.md`](TSD_DaxAlgo_Dolpago_Implementation_Checklist.md) | All | Progress only |
| [`Phase5_Execution_Contracts_2026-09-15.md`](Phase5_Execution_Contracts_2026-09-15.md) | Strategy→Paper | Ownership, grant, Paper demos |
| [`TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md`](TSMS_Signal_OrderCandidate_Field_Map_2026-09-15.md) | Strategy→OMS | Field map |
| [`daxalgo-windows-to-macos-execution-parity.md`](daxalgo-windows-to-macos-execution-parity.md) | Backtest/OMS | Lifecycle / partials / restart |
| [`daxalgo-research-design-chart-strategy-workflow.md`](daxalgo-research-design-chart-strategy-workflow.md) | Research/Design | Longer event-study plan |
| [`Volume_Rehearsal_Evidence_2026-09-15.md`](Volume_Rehearsal_Evidence_2026-09-15.md) | Research | Rehearsal evidence + Nautilus posture |
| [`MS_First_Roadmap_2026-09-15.md`](MS_First_Roadmap_2026-09-15.md) | TSD | Build order |
| [`../docs/how-a-strategy-gets-in.md`](../../docs/how-a-strategy-gets-in.md) | Strategy | Intake |
| [`../docs/chart-strategy-execution-requirements.md`](../../docs/chart-strategy-execution-requirements.md) | Chart→Exec | Requirements |
| [`../docs/execution-oms-split.md`](../../docs/execution-oms-split.md) | OMS | Split |
| [`../docs/research/vibe-quant-four-lane-runtime-benchmark.md`](../../docs/research/vibe-quant-four-lane-runtime-benchmark.md) | Backtest | Engine comparison (incl. NautilusTrader) |

### 2.3 External engines / venues

| Name | What it is | Role for DaxAlgo |
|------|------------|------------------|
| **NautilusTrader** | Trading/backtest engine with richer matching options | **Later fill-fidelity target** (queue, liquidity, latency, realistic partials). Optional in TSD; **not** Mac Paper today. Behind S06 mediator — never AI→venue. |
| **LEAN / QuantConnect** | Event-driven backtest + live | Benchmark comparison |
| **vectorbt / backtesting.py / Freqtrade** | Research tooling | Benchmark only |
| **TSD (MDMS / TSMS / TOMS / PPMS)** | Data, signals, admit, paper | Shared desk; Mac adapter |
| **IB / Alpaca / cTrader** | Brokers | Live/Paper OMS; Binance = market data only |

---

## 3. Research lane

| Step | You create / use | Done when |
|------|------------------|-----------|
| Open instrument | Chart bound; Hyperion follows | Tools work **without** Rank |
| Indicators | Bindings on chart | Inspectable params |
| Observation (+ outcome if studying return) | Chart selection | Outcome **not** invented |
| Condition | Condition definition | Version hash stable until edit |
| Find similar | Same condition | **No** silent next-day +5% |
| Save finding | One (or a second for contrast) | Restore first after saving second → distinct context |
| Handoff | **Saved finding** required | Builder opens with that package |

---

## 4. Strategy lane

| Step | You use | Done when |
|------|---------|-----------|
| Open from Research | Restored **saved finding** | Same condition + indicator bindings |
| Bind to draft | Condition id + version hash | Entry unchanged when adding exits |
| Build / register | Strategy version / artifact hash | Workspace points at this revision |
| Paper admit | Version + book ownership | Admit ≠ execute alone (Phase 5) |

---

## 5. Backtest / Validate — fill fidelity

### Today (honest)

| Setting | Applied? |
|---------|----------|
| L1 touch ± slippage, full remaining qty | **Yes** (`L1TouchFillModel`) |
| Latency ms, quantity-capped partials (max N per L1 touch) | **Yes** when enabled on Validate (applied by session + engine) |
| Queue position, liquidity walk, L2/L3 books | **No** (UI marks unavailable) |

### Later (Nautilus-class target — not claimed done)

Order-book / L2 replay · queue position · liquidity consumption · uncapped realistic partials beyond the demo max-per-touch.

Chosen explicitly on Validate/Paper. Research **saved findings** stay about signals/conditions, not fill physics.

---

## 6. End-to-end narrative

1. Research on the open chart → save a **finding**.  
2. Optionally save a second finding as contrast; restore the first.  
3. Strategy Builder from that finding → bind condition → exits/size.  
4. Validate with **L1 disclosed** (optional capped partials + latency). Nautilus-class queue/L2 is a **later fill-fidelity target**, not claimed when Validate runs L1.  
5. Paper: same strategy version, one book, one owner.

**Design → TradeIR honesty:** Design “Use rules as request” only fills the composer. TradeIR requires Build/confirm — no silent IR from rule text.

---

## 7. Agent reporting

`Lane` → `Spec ID` → `Object (saved finding / strategy version / fill model)` → `Code` → `Evidence` → `Remaining`

Never mark Nautilus done because an Order book window exists.  
Never say “Reference A/B” in user-facing copy.
