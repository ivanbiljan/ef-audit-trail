# EF Audit Trail — auditing `ExecuteUpdate` / `ExecuteDelete` in EF Core

A `SaveChanges` interceptor is the usual way to record **who, when and what** changed in an EF Core
application. But EF Core's `ExecuteUpdateAsync` / `ExecuteDeleteAsync` skip the change tracker, so they
skip the interceptor too. This sample starts from a complete interceptor-based audit trail and shows two
ways to close that gap. Both write the **same row shape** into the same
`audit.entries` table, so a reader can't tell which path wrote a row (the `source` column says which,
only so the two can be compared).

| | **1 · Trigger** (`AuditSource.Trigger`) | **2 · Snapshot** (`AuditSource.Snapshot`) |
|---|---|---|
| How | `audit.capture()` row trigger, configured from the EF model | `ExecuteAuditedUpdateAsync` / `ExecuteAuditedDeleteAsync`: lock, snapshot, run, snapshot, diff |
| Catches | everything that reaches the table: ExecuteUpdate/Delete, `ExecuteSql`, psql, other services, FK cascades | only calls that go through the extension |
| Diff logic | ported to PL/pgSQL (`jsonb_delta`, `path_matches`, value formatting) | the interceptor's own `AuditEntryBuilder`, unchanged |
| "Who" | `audit.*` session settings published by `AuditSessionInterceptor`; falls back to the DB role | `AuditContext`, the same way the interceptor gets it |
| Cost | +1 round trip per writing command; PL/pgSQL per row | +2 SELECTs, row locks until commit, both snapshots held in memory |
| Gaps | `TRUNCATE`; inserts (by design, as in the interceptor) | anything that doesn't call it, such as raw SQL or a plain `ExecuteUpdate` |

The two can run together. The trigger stands down for statements the application audits itself
(SaveChanges and the snapshot path), so each change is recorded exactly once. FK-cascaded rows are the
exception: the trigger always records those, because the application never saw them.

---

## Running it

Requirements: .NET 10 SDK, Docker.

```bash
dotnet run --project src/EfAuditTrail.AppHost
```

The Aspire dashboard opens. The startup order is:

1. **postgres** — `postgres:16`, plus **pgweb** for looking at `audit.entries` directly.
2. **migrator** (one-shot) — `EnsureCreated` (including Warp's tables), installs `audit.sql`, registers
   every audited table, seeds reference data, then replays a burst of activity through **every** path:
   SaveChanges, plain ExecuteUpdate/Delete, the snapshot extension, and an out-of-band psql-style
   UPDATE. Set `AUDIT_DEMO_PRESERVE_DB=1` to keep the database between runs.
3. **api** — starts after the migrator finishes (`WaitForCompletion`). It serves Warp HTTP endpoints
   built as vertical slices.
4. **web** — the Blazor (interactive server) back office:
   - **Audit trail** (`/`): tiles for each source, filters, keyset paging, a live toggle, a per-entity
     history link, and before/after tables with PII masked unless you reveal it.
   - **Playground** (`/playground`): pick a persona, then run each operation in **Trigger** or
     **Snapshot** mode and compare the rows each produces.

## Solution layout

```
src/
  EfAuditTrail.AppHost          Aspire: postgres:16 + pgweb, migrator → api → web
  EfAuditTrail.ServiceDefaults  OTel, health, service discovery
  EfAuditTrail.Data
    Auditing/                       the baseline: AuditAnnotations, attributes, AuditContext,
                                      AuditConvention, AuditInterceptor, AuditEntryBuilder, JsonDelta
    Auditing/Triggers/              approach 1: audit.sql (embedded) + AuditTriggerInstaller
    Auditing/Snapshots/             approach 2: AuditedBulkExtensions + KeyFilter
    Auditing/Session/               AuditSessionInterceptor, TriggerCapture, AuditTags
    Entities/ Configurations/       AuditEntry + the sample shop domain (see "Sample domain")
    Seeding/                        DemoSeeder, ActivityGenerator, Persona, OutOfBandSql
  EfAuditTrail.Migrator         one-shot schema + triggers + seed
  EfAuditTrail.Api              Warp HTTP, one file per slice under Features/<Area>/
  EfAuditTrail.Web              Blazor back office (talks to the API only)
tests/
  EfAuditTrail.Tests.Unit         InMemory / model-only: builder, convention, delta, registrations, plumbing
  EfAuditTrail.Tests.Integration  Testcontainers postgres:16: trigger, snapshot, parity, API end to end
  EfAuditTrail.Tests.EndToEnd     Aspire.Hosting.Testing: the whole graph (slow, [Trait Category=EndToEnd])
```

---

## Approach 1 — the trigger

`Auditing/Triggers/audit.sql`:

- **One generic trigger, configured from the EF model.** `AuditTriggerInstaller.BuildRegistrations(model)`
  reads the runtime annotations `AuditConvention` already writes (`[AuditIgnore]`, `[AuditPii(paths)]`,
  `jsonb`, PK/tenant/timestamps ignored), plus table and column names from the relational model. It
  calls `audit.enable(table, 'Product', key_columns, company_column, columns_json)` for each audited
  entity. The attributes stay the single source of truth, and `audit.enable` rejects unknown columns so
  schema drift fails the deploy.
- **Same `changes` shape as the interceptor.** Keys are CLR property names. `jsonb` columns are diffed to
  leaves (`Profile/deliveryContact/phone`) by `audit.jsonb_delta`, a port of `JsonDelta`. `pii_paths`
  uses `audit.path_matches`, a port of `AuditAnnotations.Matches`. Timestamps are formatted exactly as
  System.Text.Json writes them. Deletes snapshot every tracked column. Updates that touch only ignored
  columns (`updated_at`, `view_count`) write nothing.
- **Who and what, through session settings.** Before every writing command, `AuditSessionInterceptor`
  publishes the scope's `AuditContext` (actor, channel, IP, UA, on-behalf-of, and the operation the Warp
  pipeline pushed) with `set_config('audit.*', …, false)`. The settings are session-level so they
  survive autocommit, and they are re-sent before every write so pooling or a rollback can't leave stale
  values behind. LINQ reads are skipped. Without that context (psql, another service), the row says
  `channel = Database`, `actor_display = <db role>`, `operation = unattributed`.
- **No double counting.** For `SaveChanges` and for anything inside `TriggerCapture.Suppress()` (the
  snapshot path), the interceptor sets `audit.suppress = on` and the trigger returns early.
- **BEFORE, named `zz_audit_capture`.** In PostgreSQL, rows deleted by an FK cascade fire BEFORE
  triggers at `pg_trigger_depth() = 2`, but their AFTER triggers run at depth 1, mixed in with the
  parent statement's. So only a BEFORE trigger can tell a cascade apart from a suppressed statement, and
  cascaded rows are recorded even when the app audited the parent. The `zz_` prefix makes it fire last
  among BEFORE triggers, so it sees the final `NEW`. If the statement fails, the transaction removes the
  audit row along with it.
- `SECURITY DEFINER` on `audit.record` means the app role needs no INSERT on `audit.entries`.
  `audit.untracked_tables` is the "every table is audited" guard, and a test asserts it is empty.

Moving to EF migrations: `migrationBuilder.Sql(AuditTriggerInstaller.InstallScript)` installs the
functions. The `audit.enable` calls must be re-run whenever audited columns change. Either call
`AuditTriggerInstaller.InstallAsync` from a migrator (as here; it is idempotent) or emit the calls
from `BuildRegistrations` into the migration.

## Approach 2 — the snapshot extension

```csharp
await db.Products
    .Where(x => x.Category == "Outdoor")
    .ExecuteAuditedUpdateAsync(db, s =>
    {
        s.SetProperty(x => x.Price, x => Math.Round(x.Price * 1.05m, 2));
        s.SetProperty(x => x.UpdatedAt, now);
    }, ct);

await db.Orders.Where(x => x.Status == OrderStatus.Cancelled).ExecuteAuditedDeleteAsync(db, ct);
```

Everything runs in one transaction: the caller's if one is open, otherwise one opened inside the
execution strategy.

1. **Before:** run `source.AsNoTracking()` tagged `audit:for-update`. The session interceptor appends
   `FOR UPDATE`, so the rows are locked until commit.
2. **Run** the bulk statement, **pinned to the locked keys** (`WHERE id IN (…)` via `KeyFilter`; composite
   keys are supported), inside `TriggerCapture.Suppress()`. Rows that a concurrent insert adds later
   aren't touched, which keeps the audit exact.
3. **After:** re-read the same keys (skipped for deletes).
4. **Diff with the existing builder.** Each before row is attached to a *scratch* context, and either
   `CurrentValues.SetValues(after)` is applied or it is marked `Deleted`. Then `AuditEntryBuilder.Build`
   runs unchanged, so value converters, JSON leaf diffs, PII paths, ignored properties and the
   "Channel not set" guard all behave exactly as in the interceptor. The scratch context saves the rows
   on the caller's connection and transaction. It is never the caller's change tracker, so the caller's
   pending changes are neither flushed nor audited.

---

## Who / when / what, end to end

- **Who:** each host fills `AuditContext` when its scope opens. In the API, `AuditContextMiddleware`
  reads it from headers (demo only: the Blazor app sends the persona you picked, and a real host would
  use the authenticated principal). The migrator, `ActivityGenerator` and tests use `Persona`.
- **What:** `AuditOperationBehavior<,>` is an open-generic Warp `IPipelineBehavior`, registered by the
  source generator. It pushes `Feature.RequestType` (for example `Products.AdjustProductPrices`)
  for every request, so rows from all three paths carry it.
- **When:** `occurred_at`: `DateTime.UtcNow` in the app paths, `now()` (transaction start) in the trigger.

## API (Warp HTTP, vertical slices)

| Slice | Route | Path exercised |
|---|---|---|
| `Audit/GetAuditFeed` | `GET /api/audit?entityType=&entityKey=&source=&action=&actor=&operation=&before=&take=&revealPersonalData=` | read; PII masked by default |
| `Audit/GetAuditFacets` | `GET /api/audit/facets` | read |
| `Products/ListProducts`, `Customers/ListCustomers` | `GET /api/products`, `/api/customers` | read |
| `Products/RenameProduct` | `POST /api/products/rename` | SaveChanges → interceptor |
| `Customers/UpdateDeliveryContact` | `POST /api/customers/delivery-contact` | SaveChanges, jsonb, PII |
| `Products/AdjustProductPrices` | `POST /api/products/adjust-prices` `{category, percent, mode}` | bulk update, `mode` = `Trigger` \| `Snapshot` |
| `Products/ArchiveExpiredProducts` | `POST /api/products/archive-expired` `{mode}` | bulk enum change |
| `Orders/CancelPendingOrders` | `POST /api/orders/cancel-pending` `{productId?, mode}` | bulk update |
| `Orders/PurgeCancelledOrders` | `POST /api/orders/purge-cancelled` `{mode}` | bulk delete |
| `Customers/UpgradeLoyaltyTier` | `POST /api/customers/upgrade-tier` `{from, to, mode}` | bulk enum change |
| `Products/RecordProductViews` | `POST /api/products/record-views` | `[AuditIgnore]` column → no rows |
| `Playground/RunOutOfBandSql` | `POST /api/playground/out-of-band` | separate connection, no context → trigger |
| `Playground/GenerateActivity` | `POST /api/playground/activity` | everything, several personas |

## Tests

```bash
dotnet test tests/EfAuditTrail.Tests.Unit
dotnet test tests/EfAuditTrail.Tests.Integration         # needs Docker (Testcontainers, postgres:16-alpine)
dotnet test tests/EfAuditTrail.Tests.EndToEnd            # needs Docker; boots the whole AppHost
```

- **Unit:** the existing builder against InMemory (updates, ignored-only changes, enums, jsonb leaves
  with PII, deletes, missing channel, inserts); the convention (ignored, json, PII patterns, misuse fails
  the model build, class opt-out); `JsonDelta`; trigger registrations from the Npgsql model; `KeyFilter`
  (single and composite keys); the `TriggerCapture` flow; the session interceptor's classification.
- **Integration, trigger:** ExecuteUpdate with full context; SaveChanges recorded once (the trigger
  stands down); suppression not leaking into a later bulk statement in the same transaction; rollback;
  ignored-only updates; delete snapshots and PII; out-of-band attribution; jsonb leaves; timestamp
  format parity; `ExecuteSql` context; FK cascade under suppression; the untracked-tables guard; idempotent install.
- **Integration, snapshot:** rows and trigger silence; no-op rows skipped; delete; joins and rolls back
  with the caller's transaction; doesn't flush or audit the caller's pending changes; `FOR UPDATE`
  issued before an UPDATE pinned to keys; empty match; cascades still caught.
- **Parity:** three identical customers get the same update via the interceptor, the trigger and the
  snapshot path, and the same delete via the trigger and the snapshot path. `changes` and `pii_paths`
  are compared as jsonb.
- **API:** both modes through Warp HTTP, carrying the caller's identity and the operation from the
  pipeline; PII masking; ignored columns; keyset paging and facets; 400 on invalid input.

## Sample domain

A small multi-tenant shop: `Company` (tenant), `Customer`, `Supplier`, `Product` and `Order`. It is
built to exercise every audit feature:

- **Personal data:** customer name, email and phone, and a supplier's contact person (`[AuditPii]`).
  `Customer.Profile` (jsonb) marks only `/deliveryContact/**` and `/identity/**` as personal.
  `Order.Delivery` (jsonb) marks `/parcels/*/recipient` and `/parcels/*/phone`.
- **Ignored data:** `[AuditIgnore]` on `Product.ViewCount` and `Order.InternalNotes`. Keys, `CompanyId`
  and `CreatedAt`/`UpdatedAt` are ignored by convention.
- **Value handling:** enums stored as text, decimals, `DateOnly`, a tracked `timestamptz`
  (`Product.LastReviewedAt`), and jsonb leaf diffs (`Product.Attributes`).
- **FK cascades:** orders cascade from products and customers.

### Audit contracts

- `AuditEntry` maps to `audit.entries`. Its column names are pinned because the trigger writes them.
  `Source` (Interceptor / Trigger / Snapshot) defaults to `Interceptor`.
- `AuditAction { Update, Delete }`, `AuditActorKind { Anonymous, Customer, Staff, System }` and
  `AuditChannel { Api, BackOffice, Job, Seeder, Database }`. `Database` means "no application context".
  The trigger relies on these enums being stored as text (`HasConversion<string>()`).
- `ICompanyScoped { int CompanyId }` copies the tenant onto the audit row.
  `IHaveTimestamps { CreatedAt, UpdatedAt }` holds housekeeping timestamps that are never audited.
- `EfAuditTrailDbContext(options, AuditContext)` exposes `Audit`, so the snapshot path stamps rows with
  the scope's context. It is registered with plain `AddDbContext` (Warp needs the scoped context) and
  with no retrying execution strategy.

### Adopting it in your own application

Copy `Auditing/`, then:
1. Register `AuditConvention`, `AuditInterceptor` and `AuditSessionInterceptor` the way
   `DataServiceCollectionExtensions` does.
2. Map `AuditEntry`.
3. Run `AuditTriggerInstaller.InstallAsync` after migrations.
4. Fill `AuditContext` wherever a scope opens: middleware, jobs, or a Warp pipeline behavior.

**Packages:** Warp 7.3.0, Aspire 13.4.6, EF Core 10.0.5, Npgsql EF 10.0.1, xUnit v3 (MTP), Shouldly,
Testcontainers 4.11.

## Testing without Docker

The integration tests start a `postgres:16-alpine` Testcontainer by default. To use an existing
PostgreSQL 14+ server instead, set an admin connection string. Each test creates and drops its own
database:

```bash
AUDIT_TEST_POSTGRES="Host=localhost;Username=postgres;Password=postgres;Database=postgres" \
  dotnet test --project tests/EfAuditTrail.Tests.Integration
```

The SQL ports (`jsonb_delta`, `path_matches`, timestamp formatting) were also checked against their C#
originals on thousands of randomly generated inputs, with no mismatches.
