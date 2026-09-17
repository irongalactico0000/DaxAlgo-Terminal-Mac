# Three workstreams — Research · Builder · Validate

**Status:** Locked framing (2026-09-17)  
**North star (precise):**  
> Complete multi-chart indicator research and its handoff to Strategy Builder; develop and verify realistic execution simulation alongside it.

**Not enough:** shipping a chart grid *or* naming “Nautilus” without criteria.  
**Parallelism:** Validate may proceed with a **fixed strategy** and **small deterministic market-data fixtures**. It does **not** wait for multi-chart UI. Multi-chart research does **not** wait for advanced matching.

Maps to: [`task-to-interface-map.md`](task-to-interface-map.md) · [`research-strategy-backtest-reference-map.md`](research-strategy-backtest-reference-map.md) · [`acceptance-journey-research-to-validate.md`](acceptance-journey-research-to-validate.md)

---

## Workstream 1 — Research (multi-chart → saved findings)

| Capability | Completion criterion |
|------------|----------------------|
| Multiple charts | Selected instruments appear together with clearly identified **data sources** and **intervals**. |
| Shared indicators | Same indicator **definition and parameters** applied across selected charts. |
| Numerical comparison | Indicator values, changes, condition matches, and differences — not only instrument names. |
| Historical comparison | Compare selected periods, **including cases where the proposed pattern failed**. |
| Hyperion analysis | Explains the **calculated** results and proposes **testable** indicator changes. |
| Saved findings | Reopen charts, periods, settings, and findings **without uploading files**. |

**Local now (honest):** up to 3 live Compare tiles + numeric strip + finding slots. Shared indicator params across tiles and Hyperion-cites-numbers remain incomplete until verified on Mac.

**Does not own:** fill models, queue, OMS.

---

## Workstream 2 — Builder (Research → executable rules)

**The missing middle.** Viewing three charts does not establish Research → Strategy.

A finding such as *“relative volume increased before these moves”* must become an **editable rule** with:

| Element | Required |
|---------|----------|
| Defined calculation | Exact indicator / condition definition (kind, inputs, period/params) |
| Threshold | Explicit numeric or relational threshold |
| Evaluation time | When the rule is evaluated (bar close, tick, session, etc.) |
| Entry | When/how the strategy enters |
| Exit | When/how it exits |
| Sizing | Size / exposure rule |
| Risk | Max loss / stops / forbidden regimes |

**Handoff unit:** saved finding (`ResearchAnalysisReferenceV1`) → Design rules → Build → strategy version hash.  
**Local now:** Design rule fields + “Use rules as request” (composer only) + finding handoff; full auto-mapping of finding → complete executable rule set is **not** claimed done.

**Does not own:** re-implementing chart tools; inventing TradeIR without Build.

---

## Workstream 3 — Validate (event-driven execution simulation)

**Product target name:** **event-driven execution validation** (NautilusTrader is the **reference architecture**, not a checkbox).  
Docs: [Nautilus backtesting](https://nautilustrader.io/docs/latest/concepts/backtesting/) · [Nautilus orders](https://nautilustrader.io/docs/latest/concepts/orders/)

Separate capabilities to verify (do not conflate):

| Requirement | What Validate must demonstrate |
|-------------|-------------------------------|
| Order books | Reconstruct the available historical book at each replay timestamp. |
| Market/limit execution | Apply order type, price constraints, available quantity, and venue rules. |
| Queue position | Apply a **stated** queue model; mark estimates vs data-supported facts. |
| Liquidity consumption | Prevent simulated orders from repeatedly consuming the same available liquidity. |
| Latency | Process submissions, amendments, and cancellations at modeled arrival times. |
| Partial fills / lifecycle | Track remaining qty, fills, cancellations, rejections, expiry correctly. |
| Accounting | Reconcile fills with positions, fees, cash, and P&L. |

### Canonical fixture acceptance (IOC limit)

> Submit a **buy limit IOC for 100 units at 100.01**.  
> At arrival, asks: **30 @ 100.00**, **50 @ 100.01**, and additional quantity above the limit.

| Expected | Value |
|----------|-------|
| Fill | **80** units |
| Cancel remainder | **20** |
| Average fill price | \((30 × 100.00 + 50 × 100.01) ÷ 80 = 100.00625\) |

This proves price constraints, available liquidity, partial execution, cancellation, and accounting. IOC permits immediate partial fill then cancel of remainder.

### Local now (honest)

| Setting | Applied? |
|---------|----------|
| L1 touch ± slippage | **Yes** |
| Latency ms + quantity-capped partials (max 4/touch demo) | **Yes** when enabled |
| L2/L3 book replay, queue, liquidity walk, full IOC book walk | **No** — target for this workstream |

**Start now:** fixed strategy + deterministic book fixtures; headless tests first. UI Validate remains L1-disclosed until a richer model is selectable **and** proven by fixtures like the IOC case above.

---

## Dependency matrix

| | Research multi-chart | Builder handoff | Event-driven Validate |
|--|---------------------|-----------------|------------------------|
| Blocks Research? | — | No | No |
| Blocks Builder? | Soft (better findings help) | — | No |
| Blocks Validate engine? | **No** | **No** (use fixed strategy) | — |

---

## Agent reporting

`Workstream` → `Capability row` → `Code` → `Evidence (fixture or Mac note)` → `Remaining`  

Never mark event-driven Validate done because an Order book **window** exists.  
Never mark Research→Builder done because Compare shows three charts.
