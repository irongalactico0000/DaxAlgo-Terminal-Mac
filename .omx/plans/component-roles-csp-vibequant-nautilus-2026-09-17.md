# Component roles: CSP, VibeQuant/AKQuant, Nautilus (2026-09-17)

**HEAD note:** local `main` includes `764228f` (saved results) and later work. Linked PRs pointing at older SHAs (e.g. `94fd021`) will not show these commits until updated.

## Roles (honest)

| Component | Provides | Place in DaxAlgo |
|-----------|----------|------------------|
| **VibeQuant → AKQuant** | Research experiments / preliminary BT (`aq.run_backtest`) | Native Compare research lane; Hyperion/Research Studio — **not** Validate fills |
| **Point72 CSP** | Event-driven indicator/signal graphs | Optional Research / native CSP panel — **not** a trading backtest |
| **NautilusTrader** | Reference for queue/liquidity/latency matching | **Not a Mac dependency today** — UI gates say “Nautilus-class later” |
| **DaxAlgo L1TouchFillModel** | Actual Validate/backtest fills (L1 touch, optional max-per-touch partials, latency delay) | Execution Validate backend **now** |

## Proven fixture (first-party L1)

`L1ExecutionLifecycleFixtureV1` (Infrastructure): target **+50** → market 50 → one touch fills **25** → cancel → final position **+25**.

**Validate UI:** **Attach L1 lifecycle demo** → `AttachL1ExecutionLifecycleDemoCommand` → `UpsertExecutionLifecycleResult` into Saved results (`StrategyVersionResultKind.ExecutionLifecycle`). Explicit fixed fixture — not extracted from the historical run; not Nautilus.

## Compare panels (task-typed)

- Research (analysis)
- VibeQuant / AKQuant (research BT)
- Point72 CSP (indicators)
- Native compare report

None of these claim Nautilus matching. Execution continuity uses Validate + Saved results.
