# Task → interface map (Research Studio & Strategy Builder)

**Status:** Design target from local audit conclusions (2026-09-17)  
**Language:** **saved finding** (not “Reference A/B”). Chart tools live on the **open chart**.  
**Companion deliverables:** [`interaction-states-wireframes.md`](interaction-states-wireframes.md) · [`acceptance-journey-research-to-validate.md`](acceptance-journey-research-to-validate.md)

**Immediate product targets:**  
> Complete multi-chart indicator research and its **handoff to Strategy Builder**; develop and verify **realistic execution simulation alongside it**.

Three parallel workstreams (Validate does **not** wait for multi-chart UI):  
[`workstreams-research-builder-validate.md`](workstreams-research-builder-validate.md).

Do not assign success probabilities; completion is measured by acceptance journeys + declared fixtures.

External patterns (not layouts to copy): [TradingView screener](https://www.tradingview.com/support/solutions/43000718885-tradingview-screeners-walkthrough/), [TrendSpider](https://help.trendspider.com/), [QuantConnect Research Engine](https://www.quantconnect.com/docs/v2/research-environment/key-concepts/research-engine).

---

## 0. Studio split (locked)

| Place | Users accomplish | Primary output |
|-------|------------------|----------------|
| **Research Studio** | Find instruments, study charts, develop/compare indicators, find similar periods, save findings | **Saved finding** (instrument, period, indicator defs/settings, optional condition, notes/evidence) |
| **Strategy Builder** | Author trading rules, build, validate, run | **Strategy version** that may **link** a saved finding’s indicator definitions |

Someone who already knows their rules starts in Builder (**Start from rules** / template). Research stays one click away when an assumption needs investigation.

### Local conflict to resolve (screenshot)

| Current Builder Design behaviour | Target |
|----------------------------------|--------|
| Design opens editable entry/exit/sizing/risk/orders; Research optional | Design opens **editable trading rules** immediately |
| “Attach chart” mixes appearance, indicators, historical patterns, related instruments | Split: appearance → chart settings; indicators → chart tools; patterns/similar → Research; related symbols → Research screener; existing finding → Builder “Use saved research” |

---

## 1. Research Studio — complete workflows (not one mandatory sequence)

Selecting an observation **only narrows the period**. It must **not** invent an outcome, require B/C/N labels, or start a strategy.

### 1.1 Find instruments

| | |
|--|--|
| **Entry** | Research Studio → Markets / screener |
| **Actions** | Choose universe, metric, period/bar size, Top N → Rank; select rows; Open chart (one or several) |
| **Visible result** | Ranked table **or real chart grid**; coverage (requested / usable history / excluded / returned); data source (e.g. simulated) |
| **Saved state** | Screener query + selected symbols (session) |
| **Next** | Study one chart · Compare cases |
| **Local now** | Rank + table/detail; Compare hosts up to 3 live tiles (sources/intervals still need Mac verification) |

### 1.2 Study one chart

| | |
|--|--|
| **Entry** | Open chart from screener **or** Charts menu (same chart component) |
| **Actions** | Set instrument, interval, history; toggle/edit indicators; drawings; Order book / Footprint / Bookmap when data allows; optional period select; ask Hyperion |
| **Visible result** | Large inspectable chart; tool status or clear “data unavailable” reason; Hyperion addresses **this** chart |
| **Saved state** | Optional: period highlight (temporary until saved finding) |
| **Next** | Develop indicator · Find similar · Save finding · Compare |
| **Local now** | Chart-owned Views; Hyperion analytical path; Rank not required for tools |

### 1.3 Develop an indicator

| | |
|--|--|
| **Entry** | Chart indicator tools **or** Hyperion “develop / define” |
| **Actions** | Edit name, formula/kind, parameters, inputs; preview on chart; see errors |
| **Visible result** | Named definition + calculated series on the chart |
| **Saved state** | Indicator definition version (kind, params, inputs) — reusable in a saved finding |
| **Next** | Compare · Save finding · Use in Strategy Builder (defines **condition**, not the whole strategy) |
| **Local now** | Editable SMA/EMA periods on the chart rail → live series reload → exact periods in finding bindings |

### 1.4 Compare cases

| | |
|--|--|
| **Entry** | Select 2–N instruments/periods (from screener or saved findings) → Compare |
| **Actions** | Shared interval/indicator settings; numerical comparison panel; include unsuccessful cases |
| **Visible result** | Multiple charts **and** calculated similarities/differences Hyperion can cite |
| **Saved state** | Comparison set id (optional) or notes on a saved finding |
| **Next** | Save finding(s) · Develop indicator · **Use in Strategy Builder** (finding → editable rules) |
| **Done when** | See workstream 1 table in [`workstreams-research-builder-validate.md`](workstreams-research-builder-validate.md) — shared defs, numerical deltas, failed cases, Hyperion on calculated results, reopen without upload |
| **Local now** | Up to 3 live chart tiles + numeric strip (return %, vol÷avg, EMA20 slope). Shared params across tiles + Hyperion citing strip + failed-case compare still thin. |

**Gap:** chart tiles alone are **not** Research→Strategy. A finding must become an editable rule (calculation, threshold, evaluation time, entry, exit, sizing, risk).

### 1.5 Find similar observations

| | |
|--|--|
| **Entry** | Chart with optional selected period + applied condition / similarity definition |
| **Actions** | Choose what “similar” means; search; open hits on chart |
| **Visible result** | Matches under that **explicit** definition (never a silent unrelated outcome scan) |
| **Saved state** | Condition version + last search hits (with finding) |
| **Next** | Compare hits · Save finding |
| **Local now** | Condition search only; silent +5% fallback removed |

### 1.6 Save and reuse findings

| | |
|--|--|
| **Entry** | After study/compare (condition recommended for Builder handoff) |
| **Actions** | Save finding 1 or 2; restore; notes; Use in Strategy Builder |
| **Visible result** | List/slots with summary; restore reopens instrument, period, indicators, condition, evidence |
| **Saved state** | `ResearchAnalysisReferenceV1` (product: **saved finding**) |
| **Next** | Builder · further Research |
| **Local now** | Save finding 1/2 UI; handoff requires finding |

**Example question the UI must support:**  
“Top 10 by estimated traded value → compare three → what changed in volume and EMA before breakouts?”  
→ **three charts + calculated comparison**; Hyperion explains those visible results.

---

## 2. Strategy Builder — working surface

### 2.1 Start choices (same editable specification)

| Start | Opens |
|-------|--------|
| **Use saved research** | Design with linked finding + indicator defs; empty/partial rules to fill |
| **Start from rules** | Design with blank/minimal rules (no research required) |
| **Use a strategy template** | Design prefilled template rules (editable) |

### 2.2 Builder activities

| Activity | Visible | Produces |
|----------|---------|----------|
| **Design** | Instruments, data inputs, **entry**, **exit**, **sizing**, **risk**, **order instructions** — editable immediately; **Use rules as request** → composer only (Build still required) | Draft rules with calculation + threshold + evaluation time when bound from a finding |
| **Review research inputs** | Linked saved findings; exact indicator definitions/settings used by rules | Explicit link; stale if research indicator version changes until user updates |
| **Build** | Generated implementation, compile results, errors tied to rules | Artifact / strategy version hash |
| **Validate** | Declared fill model (today L1; event-driven book/queue/liquidity = workstream 3) | Validation evidence bound to version |
| **Run** | Mode, account/book, active version, orders, positions, start/stop | Live/Paper run under ownership rules |

### 2.3 Research → Builder (required middle)

A finding is incomplete until Design holds an **executable rule package**:

calculation · threshold · evaluation time · entry · exit · sizing · risk  

Example: finding *“relative volume increased before these moves”* → rule with volume÷avg definition, threshold \(k\), bar-close evaluation, plus entry/exit/size/risk. Three charts without that package ≠ handoff done.

### 2.4 Indicator connection (concrete)

```text
Research: EMA(20, close) on chart
  → Save finding
  → Use in Strategy Builder
  → Builder references that definition
  → User writes rule: close crosses above EMA(20) (+ exit/size/risk)
```

The indicator does **not** set entry order, exit, size, or risk — Builder exposes those.  
If research later changes to EMA(30), the strategy **keeps** its prior definition until the user explicitly updates the link.

---

## 3. Shared chart component

| Area | Research Studio | Strategy Builder |
|------|-----------------|------------------|
| Main working area | Chart, chart grid, or comparison | Rules, build output, or validation results |
| Navigation | Markets, studies, indicators, saved findings | Strategies, versions, test runs |
| Contextual controls | Selected indicator, period, comparison | Selected rule, parameter, execution setting |
| Hyperion | Explain, calculate, compare, develop indicators | Propose/edit rules; explain build & test |
| Chart tools | Full applicable tools | Same tools when inspecting rules or trades |
| Hyperion chrome | Collapsible companion — must not permanently crush the chart | Same |

On small Mac windows: collapse/reflow panels; keep the **current task** usable.

---

## 4. Implementation priority (from this map)

1. **Research compare** = multi-chart + shared indicator defs + numerical/historical compare + saved findings (workstream 1).  
2. **Builder handoff** = finding → editable executable rules (calculation/threshold/eval time/entry/exit/size/risk) (workstream 2).  
3. **Validate** = event-driven execution models with fixtures (workstream 3) — **parallel**, fixed strategy OK.  
4. Design rule editor chrome remains usable without Research (**Start from rules**).

Canon: [`workstreams-research-builder-validate.md`](workstreams-research-builder-validate.md).
