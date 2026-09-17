# Volume-condition rehearsal evidence — 2026-09-15

**Purpose:** Prove V05 (Mac→TSD path) and V04 (A/B reference round-trip) **without** requiring the user to click Avalonia.  
**Not a claim** that full product G/U/S/C/Q/V is complete.

## Environment

| Item | Value |
|------|--------|
| TSD | `127.0.0.1:8000`, `TSD_EXECUTION_MODE=signal`, uvicorn `tsd.api.main:app` |
| Tree | `/Users/w/Developer/tsd` (local Sep + MDMS unmock) |
| Mac | `/Users/w/Developer/DaxAlgo-Terminal-Mac` headless tests |

## V05 — Mac HTTP path against live TSD (automated)

Same endpoints `ResearchConditionSearchV1` / bridge use:

| Step | Call | Result |
|------|------|--------|
| Bridge | `GET /api/bridge/status` | `execution_mode=signal`, Model A Alpaca + Model C KR/crypto |
| Bars | `GET /api/mdms/bars/history?symbol=BTCUSDT&interval=1m&limit=5` | Real OHLCV |
| OHLCV (was mock) | `GET /api/mdms/ohlcv/BTC-USD?timeframe=1h&limit=2` | Real bars |
| Book | `GET /api/mdms/orderbook/BTCUSDT?depth=3` | Real bids/asks |
| Ticker | `GET /api/mdms/ticker/BTCUSDT` | Real 24h ticker |
| Scan A (k=2) | `POST /api/research/scan` volume_multiple_of_avg | `hit_count=55`, evaluated=480, data_source=`tsd_mdms_binance` |
| Scan B (k=3) | same, threshold=3 | `hit_count=26` (≠ A — version difference visible) |

**Trace claim:** Sidecar compute path is live. Avalonia click→UI paint still needs a human/UI run for full V05 UX proof.

## V04 — Saved findings + session continuity (Step 4)

**Canonical JSON (headless):** `ResearchAnalysisReferenceV1Tests` — A (k=2, BTCUSDT 1m) ≠ B (k=3, ETHUSDT 5m); selections + bindings round-trip.

**Authoring session reload (Avalonia tests, quit/reopen stand-in):** `ResearchReferenceContinuityTests` (2 passed):
1. Save selection A + condition k=2 → ref A; selection B + k=3 → ref B → serialize with `AuthoringSessionStore` JSON options → new VM restores pending brush + refs; Restore A/B swap without re-upload.
2. Brush alone (no labeled sample) persists `ResearchChartSelectionJson` / bindings.

**Code:** session fields `ResearchChartSelectionJson` + `ResearchIndicatorBindingsJson`; Save on brush/condition/ref restore.

**Still open for full V04 UX:** one manual Avalonia quit→reopen click path (optional; automated path covers restore seam).

---

## Visualizer panes → TSD MDMS

DaxAlgo Order Book / Composer embed use **DaxAlgo hub / venue clients** (`TradingTerminal.OrderBook`, RealBinanceClient, etc.), **not** `GET /api/mdms/orderbook|ticker` yet.

| Path | Status |
|------|--------|
| Research | Already on TSD scan + bars |
| Execution bridge | Already on `/api/bridge/*` |
| Visualizer book/ticker | **Gap** — own market-data stack; optional later adapter to MDMS for crypto when Model C / shared desk context is required |

Do **not** force-wire visualizer to TSD until U01 shared market context is designed (symbol/venue ownership).

## NautilusTrader (“Naturalist”) at this point

| Fact | Detail |
|------|--------|
| What it is | Production trading engine (Rust+Python): data clients + exec clients → `TradingNode` / backtest engine |
| In TSD repo | Optional package `src/tsd/integrations/nautilus/` (adapters, `TSDNautilusBridge`, `NautilusOrderExecutor`) |
| Installed? | **`nautilus_trader` not in local `.venv`** |
| Wired into paper path? | **No** — no imports from `api/`, `toms/` paper gateway, or `try_create_paper_executors` |
| Role vs MS | Candidate **extra execution backend** behind TOMS `OrderExecutor`, not a replacement for MDMS/TSMS/PPMS/DBMS |
| Role vs Mac Validate | **Not** the Mac Validate fill model. Mac Validate = **L1 touch ± slippage** (+ optional capped partials/latency). Nautilus-class queue/L2 = later target only. |

**Decision:** Canon **S06-A**. Keep Nautilus optional Stage-later. Do not block Dolpago research or Model A on installing it.

## Product scope reminder

Volume rehearsal = first integration demo. Canon still requires multi-indicator AND/OR (U05), situation study (V02), strategy same-verdict (V06), Paper (V07).
