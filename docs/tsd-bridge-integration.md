# TSD sidecar bridge (Model A) — seamless

User-facing: **DaxAlgo only**. Confirm trades / Execution Console / Alpaca Connect as usual.

Under the hood (invisible):

1. TSD API on `http://127.0.0.1:8000` (`TSD_EXECUTION_MODE=signal`)
2. Appsettings `TsdBridge` (Enabled, SoftFail) → `AddTsdBridge`
3. `TsdBridgeHostedService` polls `/api/bridge/signals|intents`
4. Pending items land in `ITsdPendingConfirmStore` as ordinary confirms
5. **Confirm** → prefills Execution Console manual ticket and submits when the selected book can take a market order
6. After OMS fill → `TsdBridgeClient.PostFillAsync` mirrors ledger (optional next)

If TSD is offline: SoftFail — app still trades via DaxAlgo OMS alone.

## Config

```json
"TsdBridge": {
  "Enabled": true,
  "BaseUrl": "http://127.0.0.1:8000",
  "PollIntervalSeconds": 2,
  "SoftFail": true
}
```

## Code

- `TradingTerminal.Infrastructure/TsdBridge/*`
- Options: `TsdBridgeOptions` in Core
- DI: `services.AddTsdBridge(configuration)` in `ServiceConfiguration`

Requires .NET 9 SDK to build the Mac solution.
