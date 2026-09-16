# DaxAlgo Terminal macOS — Codex guide

This private repository owns the macOS/Avalonia edition only. The Windows public core and
Professional overlay are separate repositories and outside the default blast radius.

## Session start

1. Read `.claude/context/linux/index.md`, `symbols.md`, and `deps.json`.
2. Read `.claude/context/PROTOCOL.md`.
3. Inspect `git status --short` and preserve unrelated work.
4. For material changes, create `tasks/YYYY-MM-DD-HHMM-slug.md` from `tasks/README.md`.

Navigate through the smallest generated index or symbol shard before opening source. Do not inspect,
mirror, or coordinate with a Windows repository unless the user explicitly places it in scope.

## Invariants

- Core has no Avalonia, broker SDK, storage implementation, or host dependency.
- MarketData depends on Core and stays below Infrastructure; broker SDK types stay in Infrastructure.
- `InstrumentId` is canonical and market-data provenance is preserved.
- Ingest is tick-primary and non-blocking; view models consume hub/ingest/store seams.
- MVVM remains strict; streaming UI is bounded and deterministically disposable.
- Sidecars bind to `127.0.0.1`. Live order execution is allowed only for **Alpaca / Interactive Brokers / cTrader** when composed with Keychain-backed `ILiveExecutionConfirmationStore`, per-broker `AllowLiveExecution`, and typed **LIVE** confirmation. **Binance stays market-data only** (no order adapter). Strategy → OMS only via `SandboxExecutionReplicator` (never kernel `PlaceOrder`).
- **Product lanes** (keep separate — see `docs/product-lanes-and-regulated-fences.md`): (1) Research → Paper, (2) Operator API-key Console, (3) Marketplace strategy-as-software on the user’s own keys. Do **not** ship follow / pool / ETF-share UX until counsel clears a regulated entity model.
- **TSD × Dolpago research:** Canon `.omx/plans/TSD_DaxAlgo_Dolpago_Requirements_v1.md` (G/U/S/C/Q/V; **S06** agent posture: TargetIntent + gated OrderIntent, U11–U13, triple sandbox). Progress: `TSD_DaxAlgo_Dolpago_Implementation_Checklist.md`. **Research↔Strategy spine:** `.omx/plans/research-strategy-reference-spine.md` (handoff = Reference A/B, not shared chrome). Plans index: `.omx/plans/README.md`. Report `Spec ID → user action → code → verification → remaining`. Never invent alternate R-number schemes.

## Verification

Always name the target:

```bash
dotnet build TradingTerminal.Mac.slnx
dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj
```

Use `powershell -File .claude/context/manage-context.ps1 check` for structural context checks and
`deep-check` after changing projects, routed source sets, or context machinery.
