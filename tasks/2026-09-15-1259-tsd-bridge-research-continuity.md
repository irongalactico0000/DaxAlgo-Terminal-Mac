# TSD bridge + research condition continuity

## Goal

Land Model A TsdBridge/PendingConfirms in git and close research continuity gaps:
versioned conditions, edit→re-scan, draft condition refs, session restore, live validity,
plus TSD research scan and governance locks.

## Plan

1. Phase 0: verify Mac.slnx build; commit Mac bridge + research seams with this task record.
2. Phase 1: scripted bridge E2E (signal → confirm → fill mirror) + SoftFail offline.
3. Phase 2: condition handoff into StrategyDraftV1; persist pending condition/search in session; still-valid badge.
4. Phase 3: TSD `POST /api/research/scan` + raise/extend bars access for BTCUSDT proof.
5. Phase 4: single bridge-contract canon; Python↔C# conformance fixture; two-gate order refusal test.

## Blast radius

- Mac: `TsdBridge/`, `PendingConfirmsViewModel`, research condition search/evaluator, StrategyDraft, AuthoringSessionStore, OMS risk tests, docs.
- TSD: `/api/research/scan`, optional mdms limit extension, bridge (already committed upstream).

## Build filter

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build TradingTerminal.Mac.slnx
dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj --filter "FullyQualifiedName~ResearchCondition|FullyQualifiedName~ResearchDataset|FullyQualifiedName~StrategyDraft|FullyQualifiedName~Bridge|FullyQualifiedName~Risk"
```

## Tests

Documented under Verification as checks run.

## Findings

- `TradingTerminal.Mac.slnx` builds 0 errors (8 warnings). Headless: 1086 pass / 3 unrelated fails / 6 skipped.
- `ResearchConditionDefinitionV1` already exists (not planning-only).
- TSD bridge committed as `22a3b69` in `/Users/w/Developer/tsd`.

## Diff summary

- TSD `22a3b69` bridge; `0977176` research scan + MDMS ≤5000; `8a67d10` ASGI BTCUSDT proof.
- Mac `db658f5` TsdBridge + conditions; `019cf23` draft bind, session restore, two-gate, conformance.

## Verification

- `dotnet build TradingTerminal.Mac.slnx` → 0 errors
- Headless filter ResearchCondition|StrategyDraft|TwoGate|Conformance → 9 passed
- `pytest tests/test_bridge.py tests/test_research_scan.py tests/test_research_conformance.py` → green
- ASGI: signal→intent confirm→fill ledger; POST `/api/research/scan` BTCUSDT hit_count
- SoftFail: documented; lab mode ≠ signal (unit)

## Risks/deferred

- Live `:8000` process must be restarted to pick up `/api/research/scan` (old process 404s until restart).
- Full Avalonia GUI confirm-card E2E not automated; HTTP/ASGI covers both bridge halves.
- Cross-engine conformance is volume-multiple only (fixture twin).
