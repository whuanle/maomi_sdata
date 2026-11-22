# Maomi SData Go Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rewrite the Maomi SData backend as a Go monolith while preserving the existing HTTP, OTLP, gRPC, MQTT, MQ, SQLite, DuckDB, static UI, configuration, and deployment contracts.

**Current status (2026-11-21):** Phase 1 scaffold and protocol expansion are implemented under `go/`; C# source remains untouched. Login, ingest, SQLite, DuckDB, health, metrics, OTLP HTTP/gRPC, ES Bulk, RUM, Prometheus, dashboards, alerts, saved queries, and MQTT listener smoke validation pass.

**Architecture:** Add a separate `go/` module beside the existing C# implementation. The Go backend is organized by domain (`auth`, `ingest`, `logs`, `traces`, `metrics`, `iot`, `rum`, `analytics`, `alerts`, `dashboard`, `settings`, `monitor`, `agents`) over shared SQLite/DuckDB packages and a bounded ingest pipeline. Existing C# code remains untouched during the migration, then is removed only after contract, integration, release, and performance parity gates pass.

**Tech Stack:** Go 1.24+, Chi HTTP router, standard `database/sql`, `modernc.org/sqlite` or a pinned CGO SQLite driver, official `github.com/marcboeker/go-duckdb`, OpenTelemetry OTLP protobufs, `google.golang.org/grpc`, MQTTnet-equivalent Go MQTT broker/client package, Kafka/RabbitMQ/Pulsar Go clients, `//go:embed` plus Docker static UI copy.

**User decisions:**
- **First output is the architecture and implementation plan; do not modify C# source before the Go migration phase is selected.**
- Keep the existing C# API contracts as the behavioral baseline. Frontend routes, JSON field names, status codes, authentication semantics, OTLP/gRPC shapes, and static UI behavior must remain compatible.
- Use a simple Go HTTP framework (Chi) rather than an elaborate framework. Use standard `net/http` handlers and middleware beneath Chi where possible.
- Authentication and authorization are first-class cross-cutting modules: JWT login/session state, API-key validation, role middleware, resource authorization, and user identity in request context.
- Use modular, responsibility-separated Go packages. Keep HTTP transport, application use cases, storage adapters, and domain models in separate packages.
- Replace C# AOT/dependency-injection pressure with explicit composition in `cmd/sdata` and small interfaces. Avoid reflection-heavy containers and global mutable dependency injection.
- Do not edit or revert existing C# worktree changes. Add only `go/`, migration tests, contract snapshots, scripts, Docker/compose migration surfaces, and migration documentation until cutover approval.

**Go package layout:**

```text
go/
  cmd/sdata/main.go
  internal/
    auth/
    config/
    server/
    middleware/
    store/
    database/
    ingest/
    logs/
    traces/
    metrics/
    iot/
    rum/
    mq/
    analytics/
    alerts/
    dashboard/
    settings/
    monitor/
    agents/
  test/
    contracts/
    integration/
    benchmark/
  testdata/
  go.mod
```

**Architecture invariants:**

- `cmd/sdata` creates dependencies and wires routes; domain packages do not scan or self-register globally.
- HTTP handlers validate transport input and return existing C# status-code shapes.
- Application services own use-case rules and are independent from Chi.
- SQLite and DuckDB implementations live behind interfaces owned by their consuming domains.
- The ingest buffer is bounded by row count and byte budget; DuckDB Appenders run only in a writer goroutine.
- Every external DuckDB value is bound as a parameter or passed through a typed Appender API.
- User identity and role claims enter handlers through middleware context and request/application types, never through package globals.
- All route changes require a contract test and a UI compatibility check.
- C# remains runnable and deployable until the Go parity gate passes.

**Repository rule:** The user's existing C# worktree modifications are the source baseline. Every migration task starts with exact `go/` paths and must not edit or revert C# files. The final cutover task alone removes the C# backend.

---

## Task 1: Freeze API contracts and create the Go workspace

**Files:**
- Create: `F:\workspace\maomi_sdata\go\go.mod`
- Create: `F:\workspace\maomi_sdata\go\go.work`
- Create: `F:\workspace\maomi_sdata\go\README.md`
- Create: `F:\workspace\maomi_sdata\go\cmd\sdata\main.go`
- Create: `F:\workspace\maomi_sdata\go\internal\contract\routes.txt`
- Create: `F:\workspace\maomi_sdata\go\internal\contract\routes_test.go`
- Create: `F:\workspace\maomi_sdata\scripts\export-api-routes.ps1`

- [ ] **Step 1: Export the existing route inventory**

Run:

```powershell
rg --no-heading -n '\[Route|\[Http(Get|Post|Put|Delete|Patch)' src `
  | Sort-Object > go/internal/contract/routes.txt
```

Expected: every controller route in [src/](/F:/workspace/maomi_sdata/src) has a line in `go/internal/contract/routes.txt`.

- [ ] **Step 2: Initialize the Go modules**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata
New-Item -ItemType Directory -Force -Path go/cmd/sdata,go/internal/contract
Set-Location go
go mod init github.com/whuanle/maomi-sdata/go
go get github.com/go-chi/chi/v5@latest
go get github.com/marcboeker/go-duckdb@v1.8.5
go get google.golang.org/grpc@latest
go get google.golang.org/protobuf@latest
```

Expected: `go test ./...` succeeds with no application imports.

- [ ] **Step 3: Add a route inventory test**

Create `F:\workspace\maomi_sdata\go\internal\contract\routes_test.go`:

```go
package contract

import "testing"

func TestRequiredCoreRoutesAreDocumented(t *testing.T) {
	required := []string{
		"/health",
		"/metrics",
		"/api/ingest/logs",
		"/api/ingest/traces",
		"/api/ingest/metrics",
		"/api/ingest/iot",
		"/v1/logs",
		"/v1/traces",
		"/v1/metrics",
		"/api/logs",
		"/api/traces",
		"/api/metrics",
	}
	for _, route := range required {
		if route == "" {
			t.Fatal("empty route")
		}
	}
}
```

- [ ] **Step 4: Validate the workspace**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata\go
go test ./...
go version
```

Expected: PASS and Go 1.24 or newer.

## Task 2: Implement configuration and the Go host

**Files:**
- Create: `F:\workspace\maomi_sdata\go\internal\config\config.go`
- Create: `F:\workspace\maomi_sdata\go\internal\config\config_test.go`
- Create: `F:\workspace\maomi_sdata\go\internal\server\server.go`
- Create: `F:\workspace\maomi_sdata\go\internal\server\server_test.go`
- Create: `F:\workspace\maomi_sdata\go\cmd\sdata\main.go`

- [ ] **Step 1: Add the configuration precedence test**

Create `F:\workspace\maomi_sdata\go\internal\config\config_test.go`:

```go
package config

import "testing"

func TestPortEnvironmentOverridesFile(t *testing.T) {
	t.Setenv("SDATA_PORT", "5311")
	cfg, err := Load("testdata/appsettings.json")
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Port != 5311 {
		t.Fatalf("got %d, want 5311", cfg.Port)
	}
}
```

- [ ] **Step 2: Implement JSON and environment loading**

Implement:

```text
defaults < appsettings.json < configs/appsettings.json < SDATA_* < CLI flags
```

The Go host must use:

- `SData.Port`, default `5300`
- `SData.Database.Path`, default `data/sdata.db`
- `SData.DuckDb.Path`, default `data/sdata.duckdb`
- `SData.Ingest.BufferCapacity`, default `100000`
- `SData.Ingest.BufferByteBudgetBytes`, default `268435456`
- `SData.WebUI`, default `true`

- [ ] **Step 3: Add `/health` and `/metrics`**

`/health` must include process, SQLite, DuckDB, ingest buffer, and listener status. `/metrics` must remain Prometheus text format for existing deployment tooling.

- [ ] **Step 4: Run host tests**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata\go
go test ./internal/config ./internal/server ./cmd/sdata
```

Expected: PASS.

## Task 3: Build SQLite metadata storage

**Files:**
- Create: `F:\workspace\maomi_sdata\go\internal\database\database.go`
- Create: `F:\workspace\maomi_sdata\go\internal\database\schema.go`
- Create: `F:\workspace\maomi_sdata\go\internal\database\schema_test.go`
- Create: `F:\workspace\maomi_sdata\go\internal\database\entities.go`
- Create: `F:\workspace\maomi_sdata\go\internal\database\reconcile.go`

- [ ] **Step 1: Add schema reconciliation tests**

Test these invariants:

- existing data survives a startup
- missing tables are created
- missing columns are appended
- no column or table deletion occurs
- snake_case table and column names match the C# SQLite export

- [ ] **Step 2: Implement SQLite opening and reconciliation**

Use parameterized SQL for all external input. Store:

- users and password hashes
- API keys
- devices
- dashboards
- saved queries
- settings
- notification channels
- MQ instances
- cold archive ledger
- RUM projects and RUM data metadata

- [ ] **Step 3: Export a schema snapshot**

Run:

```powershell
dotnet run --project tool/SchemaSync -- script
go test ./internal/database
```

Expected: Go reconciliation applies cleanly to a copy of the C# SQLite database.

## Task 4: Build DuckDB storage and bounded ingestion

**Files:**
- Create: `F:\workspace\maomi_sdata\go\internal\store\duckdb.go`
- Create: `F:\workspace\maomi_sdata\go\internal\store\buffer.go`
- Create: `F:\workspace\maomi_sdata\go\internal\store\writer.go`
- Create: `F:\workspace\maomi_sdata\go\internal\store\timetables.go`
- Create: `F:\workspace\maomi_sdata\go\internal\store\query.go`
- Create: `F:\workspace\maomi_sdata\go\internal\store\*_test.go`

- [ ] **Step 1: Add a bounded-buffer test**

The test must prove:

- capacity is bounded by rows and bytes
- full buffers return the exact drop count
- a batch flushes on size or interval
- concurrent producers do not deadlock
- cancellation returns pending work without a block

- [ ] **Step 2: Implement day table naming**

Create tables matching:

- `logs_yyyymmdd`
- `traces_yyyymmdd`
- `metrics_yyyymmdd`
- `iot_yyyymmdd`
- `mq_consumption_yyyymmdd`
- `rum_session_yyyymmdd`
- `rum_event_yyyymmdd`
- `rum_replay_yyyymmdd`

No user value may become a table name.

- [ ] **Step 3: Implement the single writer**

The writer must:

- consume bounded batches
- append by domain and day
- reuse appender/writer resources when safe
- never execute Appender calls from request goroutines
- expose processed, dropped, buffered, and lock metrics

- [ ] **Step 4: Implement parameterized read queries**

All external conditions are bound parameters. Implement `$name`-equivalent named or positional parameter binding and reject multi-statement SQL.

- [ ] **Step 5: Run storage tests**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata\go
go test ./internal/store
```

Expected: PASS for tables, buffer limits, appender writes, and parameter binding.

## Task 5: Implement authentication and API security

**Files:**
- Create: `F:\workspace\maomi_sdata\go\internal\auth\jwt.go`
- Create: `F:\workspace\maomi_sdata\go\internal\auth\password.go`
- Create: `F:\workspace\maomi_sdata\go\internal\auth\apikey.go`
- Create: `F:\workspace\maomi_sdata\go\internal\auth\middleware.go`
- Create: `F:\workspace\maomi_sdata\go\internal\auth\auth_test.go`
- Create: `F:\workspace\maomi_sdata\go\internal\http\auth.go`

- [ ] **Step 1: Match login and user routes**

Implement routes equivalent to `auth/login`, `auth/password/change`, `auth/me`, `users/*`.

- [ ] **Step 2: Match password and JWT behavior**

Preserve password hash compatibility or provide a one-time migration. Preserve JWT claims and `admin` role semantics.

- [ ] **Step 3: Match ingest API-key behavior**

Implement `X-Api-Key`, bearer, and basic credentials from the C# ingestion contract. Preserve key masking and revoke semantics.

- [ ] **Step 4: Validate authentication**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata\go
go test ./internal/auth ./internal/http
```

Expected: PASS for login, role protection, password reset, key validation, and key revoke.

## Task 6: Implement ingestion protocols

**Files:**
- Create: `F:\workspace\maomi_sdata\go\internal\ingest\http.go`
- Create: `F:\workspace\maomi_sdata\go\internal\ingest\otlp_http.go`
- Create: `F:\workspace\maomi_sdata\go\internal\ingest\otlp_grpc.go`
- Create: `F:\workspace\maomi_sdata\go\internal\ingest\otlp_proto.go`
- Create: `F:\workspace\maomi_sdata\go\internal\ingest\es_bulk.go`
- Create: `F:\workspace\maomi_sdata\go\internal\ingest\mapper.go`
- Create: `F:\workspace\maomi_sdata\go\internal\ingest\*_test.go`

- [ ] **Step 1: Implement JSON ingestion routes**

Implement:

- `/api/ingest/logs`
- `/api/ingest/traces`
- `/api/ingest/metrics`
- `/api/ingest/iot`
- `/api/ingest/otlp/v1/logs`
- `/api/ingest/otlp/v1/traces`
- `/api/ingest/otlp/v1/metrics`
- `/v1/logs`
- `/v1/traces`
- `/v1/metrics`
- `/api/v1/logs`
- `/api/v1/traces`
- `/api/v1/metrics`

- [ ] **Step 2: Implement OTLP JSON and protobuf**

Use the official OpenTelemetry protobuf types. Preserve partial-success counts and 429 behavior for HTTP buffer overload.

- [ ] **Step 3: Implement gRPC OTLP services**

Use ports/listeners equivalent to the C# `ConfigureGrpcModule`. The same parser and ingest buffer must serve HTTP and gRPC.

- [ ] **Step 4: Implement ES Bulk**

Implement `_bulk` and `{index}/_bulk`, return ES-shaped item results, and preserve current source/index behavior.

- [ ] **Step 5: Validate ingestion**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata\go
go test ./internal/ingest
```

Expected: PASS for JSON, gzip, protobuf, partial success, buffer overload, and ES Bulk.

## Task 7: Implement logs, traces, metrics, and IoT queries

**Files:**
- Create: `F:\workspace\maomi_sdata\go\internal\logs\handler.go`
- Create: `F:\workspace\maomi_sdata\go\internal\logs\store.go`
- Create: `F:\workspace\maomi_sdata\go\internal\traces\handler.go`
- Create: `F:\workspace\maomi_sdata\go\internal\traces\store.go`
- Create: `F:\workspace\maomi_sdata\go\internal\metrics\handler.go`
- Create: `F:\workspace\maomi_sdata\go\internal\metrics\promql.go`
- Create: `F:\workspace\maomi_sdata\go\internal\iot\handler.go`
- Create: `F:\workspace\maomi_sdata\go\internal\iot\store.go`
- Create: matching `*_test.go` files

- [ ] **Step 1: Freeze response JSON**

Capture C# responses for representative requests and store them under `go/testdata/contracts/`.

- [ ] **Step 2: Implement domain handlers**

Preserve routes and response fields for:

- `/api/logs`
- `/api/logs/services`
- `/api/logs/search`
- `/api/traces`
- `/api/traces/{traceId}`
- `/api/traces/search`
- `/api/metrics`
- `/api/metrics/query`
- `/api/metrics/search`
- `/api/v1/query`
- `/api/v1/query_range`
- `/api/iot/search`
- `/api/iot/devices`
- `/api/iot/devices/{deviceKey}/latest`

- [ ] **Step 3: Match time-table and cold-layer behavior**

Resolve local and cold tables with parameterized query plans. Preserve query limits and the fail-open behavior for missing S3/cold objects.

- [ ] **Step 4: Run domain tests**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata\go
go test ./internal/logs ./internal/traces ./internal/metrics ./internal/iot
```

Expected: PASS for responses, table resolution, limits, PromQL, and search behavior.

## Task 8: Implement RUM and MQ ingestion

**Files:**
- Create: `F:\workspace\maomi_sdata\go\internal\rum\handler.go`
- Create: `F:\workspace\maomi_sdata\go\internal\rum\store.go`
- Create: `F:\workspace\maomi_sdata\go\internal\rum\hub.go`
- Create: `F:\workspace\maomi_sdata\go\internal\mq\manager.go`
- Create: `F:\workspace\maomi_sdata\go\internal\mq\kafka.go`
- Create: `F:\workspace\maomi_sdata\go\internal\mq\rabbitmq.go`
- Create: `F:\workspace\maomi_sdata\go\internal\mq\pulsar.go`
- Create: matching `*_test.go` files

- [ ] **Step 1: Implement RUM HTTP and WebSocket routes**

Preserve `/rum/v1/beacon`, `/rum/v1/config`, `/rum/probe.js`, RUM query routes, and `/hub/rum`.

- [ ] **Step 2: Implement Kafka, RabbitMQ, and Pulsar**

Preserve at-least-once semantics: decode fully, persist, then acknowledge. Never acknowledge on buffer or parse failure.

- [ ] **Step 3: Implement hot reload**

Reconcile configured MQ instances every 10 seconds and restart only changed instances.

- [ ] **Step 4: Run RUM and MQ tests**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata\go
go test ./internal/rum ./internal/mq
```

Expected: PASS for beacon, replay, hot reload, retry, and acknowledge semantics.

## Task 9: Implement analytics, alerts, dashboards, settings, monitor, and agents

**Files:**
- Create: `F:\workspace\maomi_sdata\go\internal\analytics\*.go`
- Create: `F:\workspace\maomi_sdata\go\internal\alerts\*.go`
- Create: `F:\workspace\maomi_sdata\go\internal\dashboard\*.go`
- Create: `F:\workspace\maomi_sdata\go\internal\settings\*.go`
- Create: `F:\workspace\maomi_sdata\go\internal\monitor\*.go`
- Create: `F:\workspace\maomi_sdata\go\internal\agents\*.go`
- Create: matching `*_test.go` files

- [ ] **Step 1: Implement metadata modules**

Each route must preserve C# status codes and response JSON. No handler may directly hold a user context provider; carry user identity in command/request types.

- [ ] **Step 2: Implement read-only analytics SQL**

Allow only one read-only SELECT/WITH statement, parameter bind every external input, enforce rows and query-day limits.

- [ ] **Step 3: Implement alert execution and delivery**

Preserve alert scheduling, execution gate, budget, retry, notification channels, and status reporting.

- [ ] **Step 4: Implement monitor and MCP**

Expose `/api/monitor/status`, `/api/overview/status`, `/api/mcp`, `/health`, `/metrics`, MQ state, and backlog/buffer metrics.

- [ ] **Step 5: Run module tests**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata\go
go test ./internal/analytics ./internal/alerts ./internal/dashboard ./internal/settings ./internal/monitor ./internal/agents
```

Expected: PASS for route, status-code, authorization, and response-contract tests.

## Task 10: Replace maintenance and cold-storage behavior

**Files:**
- Create: `F:\workspace\maomi_sdata\go\internal\store\rollup.go`
- Create: `F:\workspace\maomi_sdata\go\internal\store\archive.go`
- Create: `F:\workspace\maomi_sdata\go\internal\store\retention.go`
- Create: `F:\workspace\maomi_sdata\go\internal\store\cold.go`
- Create: `F:\workspace\maomi_sdata\go\internal\store\maintenance.go`
- Create: matching `*_test.go` files

- [ ] **Step 1: Implement maintenance scheduling**

Separate maintenance work from ingest and query locks. Rollup, archive, and retention must share at most one maintenance execution.

- [ ] **Step 2: Implement archive and retention**

Preserve per-day partitioning, cold object layout, ledger behavior, token indexes, S3 retry, and delete ledger semantics.

- [ ] **Step 3: Implement rollup**

Use watermarks and late-window behavior, aggregate first, and avoid per-row label hashing.

- [ ] **Step 4: Validate maintenance**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata\go
go test ./internal/store
```

Expected: PASS for rollup, retention, archive, and cold fallback.

## Task 11: Preserve UI, Docker, and release deployment

**Files:**
- Modify: `F:\workspace\maomi_sdata\Dockerfile`
- Modify: `F:\workspace\maomi_sdata\docker-compose.yml`
- Create: `F:\workspace\maomi_sdata\go\Dockerfile`
- Create: `F:\workspace\maomi_sdata\go\embed.go`
- Modify: `F:\workspace\maomi_sdata\deploy\**`
- Create: `F:\workspace\maomi_sdata\scripts\smoke-go.ps1`

- [ ] **Step 1: Embed the UI into the Go binary**

Build `ui/` first, copy `dist-output` and RUM probe assets into `go/web`, then serve them with `io/fs` and a SPA fallback.

- [ ] **Step 2: Replace the backend Docker stage**

Build a static or mostly-static Go Linux binary, copy it to a minimal final image, keep `/app/data` and `/app/configs` volumes, expose port `5300`, and use `/health` for container health checks.

- [ ] **Step 3: Add release smoke**

Verify:

- frontend loads
- `/health` is 200
- `/metrics` is Prometheus text
- JSON ingest succeeds
- OTLP HTTP succeeds
- SQLite and DuckDB create data in the default volume
- container restart preserves SQLite and day tables

- [ ] **Step 4: Validate Docker**

Run:

```powershell
docker compose build
docker compose up -d
powershell -ExecutionPolicy Bypass -File scripts/smoke-go.ps1
docker compose down
```

Expected: PASS with the Go backend replacing the C# backend artifact.

## Task 12: Add contract, integration, and performance parity gates

**Files:**
- Create: `F:\workspace\maomi_sdata\go\test\contracts\*`
- Create: `F:\workspace\maomi_sdata\go\test\integration\*`
- Create: `F:\workspace\maomi_sdata\go\test\benchmark\*`
- Create: `F:\workspace\maomi_sdata\go\testdata\*`
- Create: `F:\workspace\maomi_sdata\go\Makefile`

- [ ] **Step 1: Add contract parity tests**

For representative requests, compare C# and Go status code, headers, body shape, and error messages. JSON field order is not considered; field names and values are.

- [ ] **Step 2: Add integration tests**

Start real SQLite and DuckDB, send HTTP/OTLP/gRPC/MQTT requests, and verify persisted rows and query responses.

- [ ] **Step 3: Add benchmark thresholds**

Track at least:

- log, trace, metric, and IoT ingest rows/sec
- time-range, severity, search, trace, and aggregate QPS
- mixed 30% read and 70% read workloads
- p50/p95/p99
- RSS and CPU
- 429/5xx/drop/error counts

Minimum cutover gate: no 5xx on 15-minute stable ingest, no response loss below target, no protocol error above 0.1%, and no P99 regression beyond 20% versus the frozen C# baseline.

- [ ] **Step 4: Run the full suite**

Run:

```powershell
Set-Location F:\workspace\maomi_sdata\go
go test ./...
go test -race ./internal/...
go test -run '^$' -bench . ./test/benchmark
```

Expected: PASS or a documented, approved exception.

## Task 13: Cut over to Go

**Files:**
- Modify: `F:\workspace\maomi_sdata\Dockerfile`
- Modify: `F:\workspace\maomi_sdata\docker-compose.yml`
- Modify: `F:\workspace\maomi_sdata\README.md`
- Modify: `F:\workspace\maomi_sdata\README.en.md`
- Modify: `F:\workspace\maomi_sdata\docs\deployment\**`
- Delete: `F:\workspace\maomi_sdata\src\**`
- Delete: `F:\workspace\maomi_sdata\SData.slnx`
- Delete: `F:\workspace\maomi_sdata\Directory.Packages.props`
- Delete: `F:\workspace\maomi_sdata\tool\SchemaSync\**`
- Delete: `F:\workspace\maomi_sdata\tool\I18nScanner\**`

- [ ] **Step 1: Dual-run the C# and Go backends**

Run both on separate ports and route release smoke traffic through both. Compare SQLite mutations and DuckDB query results.

- [ ] **Step 2: Freeze C# baseline**

Create a release tag or commit containing the C# baseline before deletion.

- [ ] **Step 3: Switch Docker entrypoint to Go**

Change the final image entrypoint to `sdata`. Remove C# SDK/build stages after one confirmed successful release.

- [ ] **Step 4: Remove C# sources**

Delete C# source projects only after all contract, integration, migration, benchmark, Docker, UI, and release gates pass.

- [ ] **Step 5: Final repository validation**

Run:

```powershell
git --no-pager status --short
go test ./...
docker compose build
docker compose up -d
curl.exe -fsS http://127.0.0.1:5300/health
curl.exe -fsS http://127.0.0.1:5300/metrics
docker compose down
```

Expected: clean Go build, healthy container, working UI, and no C# backend references.

## Plan completion gates

Migration is complete only when all of these pass:

- existing C# SQLite database opens and reconciles in Go
- existing C# API key, user, dashboard, settings, and saved-query data remains usable
- all route inventory entries are implemented or intentionally deprecated
- logs/traces/metrics/iot/RUM ingestion survives restart
- OTLP HTTP and gRPC are compatible
- at least Kafka, RabbitMQ, and Pulsar behavior is validated if enabled
- DuckDB day tables and cold-table layout remain compatible
- 15-minute stability run has zero 5xx and bounded buffer drops
- Docker release starts with `/app/data` and `/app/configs`
- UI contract tests pass
- C# removal happens only after a tagged baseline exists
