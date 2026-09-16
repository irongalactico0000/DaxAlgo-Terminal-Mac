# MS-first roadmap — TSD Agent OS + DaxAlgo Kernel adapter

**Status:** TARGET ORDER (2026-09-15)  
**Principle:** Wire **real MS** on used paths → typed contracts → DaxAlgo adapter → closed feedback loop → autonomous agent. Do **not** lead with LLM role microservices.

**Architecture lock:** `/Users/w/Developer/tsd/.omx/plans/Architecture_TSD_Agent_OS_x_DaxAlgo_Kernel_2026-09-15.md`  
**Canon:** [`TSD_DaxAlgo_Dolpago_Requirements_v1.md`](./TSD_DaxAlgo_Dolpago_Requirements_v1.md) · Phase 5: [`Phase5_Execution_Contracts_2026-09-15.md`](./Phase5_Execution_Contracts_2026-09-15.md)

---

## Two goals (dependency order, not calendar)

| # | Goal | Meaning |
|---|------|---------|
| **①** | TSD Agent OS + deterministic MS body | Observe → research/decide → TargetIntent → order/fill → position → reassess (headless OK) |
| **②** | DaxAlgo as capability adapter | Workspace/UI + Virtual Book + OMS for Dax-owned books (Model A); not a merged engine |

AI modes (research / strategy ops / delegated autonomy) sit **on** ①–②. They do not replace MS wiring.

---

## What DaxAlgo actually calls today (Sep local)

| Mac caller | TSD endpoint | MS | Local status |
|------------|--------------|-----|--------------|
| `ResearchConditionSearchV1` | `POST /api/research/scan` | TSMS research + MDMS bars | **Real** (Binance public + shared evaluator) |
| same (fallback) | `GET /api/mdms/bars/history` | MDMS | **Real** |
| `TsdBridgeClient` | `/api/bridge/*` | Bridge → (signal) DaxAlgo OMS | **Real** signal divert |
| — | `/api/mdms/ohlcv\|orderbook\|ticker` | MDMS | **Real** (BinancePublicClient) — Mac visualizer **not** on these yet |
| — | `/api/tsms/backtest` | TSMS | **Real engine** when data loads; **fail-closed** if no history (no random) |
| — | `/api/toms`, `/api/ppms` | TOMS/PPMS | **Gateway/tracker** on paper path; Mac Model A does not need them for Alpaca |
| TSD dashboard WS | `market_data_simulator` in `main.py` | was random | **→ rewired to MDMS public ticker** (this change) |

**Not called by Mac yet:** full DBMS HTTP research store, visualizer→MDMS book, TSMS Composer catalog vs seed `/catalog`.

---

## MS use in autonomous trading (①)

| MS | Must do | Connection note |
|----|---------|-----------------|
| MDMS | Live state + features | API + strategy pump must use real feed/calc; show stale/gap |
| DBMS | History for research/BT | Complete collect/store/query for **used** datasets only |
| TSMS | Analyze / select / run → TargetIntent | Real backtest; start/stop; MDMS+PPMS in, host intent out |
| TOMS | Risk + submit + track | Only if TSD owns the book; Model A Alpaca → DaxAlgo OMS instead |
| PPMS | Positions/P&L/reconcile | Fill bridge + restart; no double-count |

Loop: DBMS/TSMS backtest → MDMS now → PPMS state → TSMS/AI target → TOMS (or DaxAlgo) → PPMS update → repeat.

---

## Execution ownership (choose before AI roles)

| Mode | Orders | TSD role |
|------|--------|----------|
| TSD solo | TOMS | Full MS loop |
| DaxAlgo book (Model A) | DaxAlgo OMS | Data/research/strategy/intent + receive fills |
| DaxAlgo UI + TSD exec | TOMS | Full trade; paint state in DaxAlgo |

**Never** dual-submit the same book from TOMS and DaxAlgo.

---

## Development order (replaces “AI first”)

| Step | Build | Pass when |
|------|-------|-----------|
| **1** | Mac build green + call-graph of live APIs | This table stays accurate |
| **2** | Mock→**real MS** on paths we call | Real data/BT on those routes |
| **3** | TSMS strategy → designated orders → PPMS | Paper E2E **without AI** (Next 1–3 done) |
| **4** | DaxAlgo selection/research/condition/reference | **Done (automated):** session reload restores brush + A/B without re-upload (`ResearchReferenceContinuityTests`) |
| **5** | AI analysis/authoring calling real TSD | AI uses wired MS, not mocks |
| **6** | Delegated autonomy inside grant | AI reads MDMS+PPMS; host admits |
| **7** | TradingAgents roles + A/B | Same data/engine; measure judgment only |

**Parallel rule:** research/UI may proceed; codegen / auto-order must obey Phase 5 gates.

---

## First slice for Model A (DaxAlgo owns Alpaca)

Need now: MDMS + research/TSMS + bridge + DaxAlgo OMS/Paper.  
**Do not** block on every TOMS broker adapter.  
Need TOMS+PPMS when goal is **TSD solo** crypto/KR books.
