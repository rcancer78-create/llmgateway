# Changelog

All notable changes to LLMGateway are documented in this file.

## [Unreleased] - 2026-10-06

### Core & Domain Refactoring
- **Profile Freeze & Immutability**:
  - Moved profile freezing logic to `AccountProfile.Freeze()` in `Accounts.cs`.
  - Removed duplicate `FreezeProfile` and `FreezeAccountWrite` methods across `JsonAccountStore` and `LlmGateway`.
  - Updated `ChatMessage` and `GatewayOptions` record types with `{ get; init; }` for immutability.
  - Implemented `long RevisionId { get; }` on `IAccountStore` and `JsonAccountStore` to track state mutations efficiently.

- **Observed Limit & Probe Caching**:
  - Extracted rate limit tracking and status overlays into thread-safe `ObservedLimitTracker`.
  - Extracted quota and model TTL caching into `ProbeCache`.
  - Refactored `LlmGateway` observation comparison to fast `O(1)` `RevisionId` checks, removing expensive `JsonSerializer.Serialize` overhead per request.

- **Account Store Protection**:
  - Enforced per-provider account limit (`MaxAccountsPerProvider = 20`) in `JsonAccountStore`.
  - Ensured corrupt/invalid JSON triggers `.corrupt-<timestamp>` backup creation while preserving invalid document shape exception bubbling.
  - Updated `GetAll()` to return fast cloned snapshot from `ImmutableArray`.

- **Security & CliArguments**:
  - Enforced strict refusal of dangerous CLI flags with `--dangerously-*` and `--allow-dangerously-*` prefixes.

- **Server & Diagnostics**:
  - Added `preserveLoggingProviders` parameter in `EmbeddedGatewayServer.StartAsync` to give host applications control over logging provider cleanup.
  - Enhanced `App.xaml.cs` unhandled exception reporting in `LLMGateway.ChatApp` with exception type categorization.

### Tests & CI
- Added comprehensive unit and integration test suite (`ReviewImplementationPhase5Tests.cs`) covering:
  - `StopSequenceFilter` surrogate pair emojis and split deltas.
  - `OutputBudget.Fit` surrogate pair boundary preservation.
  - `CliArguments.Parse` unclosed quote exception handling.
  - `CliArguments.RejectUnsafe` dangerous prefix rejection.
  - `JsonAccountStore` corrupt JSON backup creation.
  - `AccountProfile.Clone()` property completeness verification via reflection.
  - `ExecutableResolver` system executable resolution.
- Added GitHub Actions CI pipeline configuration (`.github/workflows/ci.yml`).
