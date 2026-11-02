# EF Audit Trail — one trigger for every write in EF Core

A `SaveChanges` interceptor is the usual way to record **who, when and what** changed in an EF Core
application. But EF Core's `ExecuteUpdateAsync` / `ExecuteDeleteAsync` skip the change tracker, so they
skip the interceptor too. This sample records everything in one place instead: a PostgreSQL row trigger
writes `audit.entries`, and the application's only job is to tell the database who is asking.

| | |
|---|---|
| How | `audit.capture()` row trigger, configured from the EF model |
| Catches | everything that reaches the table: SaveChanges, ExecuteUpdate/Delete, `ExecuteSql`, psql, other services, FK cascades |
| "Who" | `audit.*` session settings published by `AuditSessionInterceptor`; falls back to the DB role |
| Cost | PL/pgSQL per row on the database, see [Cost](#cost) |
| Gaps | `TRUNCATE`; inserts (by design) |

---

## Running it

Requirements: .NET 10 SDK, Docker.

```bash
dotnet run --project src/EfAuditTrail.AppHost
```

The Aspire dashboard opens. The startup order is:

1. **postgres** — `postgres:16`, plus **pgweb** for looking at `audit.entries` directly.
2. **migrator** (one-shot) — `EnsureCreated` (including Warp's tables), installs `audit.sql`, registers
   every audited table, seeds reference data, then replays a burst of activity through every kind of
   write: SaveChanges, ExecuteUpdate/Delete, and an out-of-band psql-style UPDATE. Set `AUDIT_DEMO_PRESERVE_DB=1` to keep the database between runs.
3. **api** — starts after the migrator finishes (`WaitForCompletion`). It serves Warp HTTP endpoints
   built as vertical slices.
4. **web** — the Blazor (interactive server) back office:
   - **Audit trail** (`/`): an entry count, filters, keyset paging, a live toggle, a per-entity
     history link, and before/after tables with PII masked unless you reveal it.
   - **Playground** (`/playground`): pick a persona, run each operation and see the rows it produces.

## Solution layout

```
src/
  EfAuditTrail.AppHost          Aspire: postgres:16 + pgweb, migrator → api → web
  EfAuditTrail.ServiceDefaults  OTel, health, service discovery
  EfAuditTrail.Data
    Auditing/                       AuditAnnotations, attributes, AuditContext, AuditConvention
    Auditing/Triggers/              audit.sql (embedded) + AuditTriggerInstaller
    Auditing/Session/               AuditSessionInterceptor
    Entities/ Configurations/       AuditEntry + the sample shop domain (see "Sample domain")
    Seeding/                        DemoSeeder, ActivityGenerator, Persona, OutOfBandSql
  EfAuditTrail.Migrator         one-shot schema + triggers + seed
  EfAuditTrail.Api              Warp HTTP, one file per slice under Features/<Area>/
  EfAuditTrail.Web              Blazor back office (talks to the API only)
tests/
  EfAuditTrail.Tests.Unit         InMemory / model-only: convention, registrations, session delivery
  EfAuditTrail.Tests.Integration  Testcontainers postgres:16: trigger, SQL rules, parity, bulk flows, API end to end
  EfAuditTrail.Tests.EndToEnd     Aspire.Hosting.Testing: the whole graph (slow, [Trait Category=EndToEnd])
```

---

## The trigger

`Auditing/Triggers/audit.sql`:

- **One generic trigger, configured from the EF model.** `AuditTriggerInstaller.BuildRegistrations(model)`
  reads the runtime annotations `AuditConvention` already writes (`[AuditIgnore]`, `[AuditPii(paths)]`,
  `jsonb`, PK/tenant/timestamps ignored), plus table and column names from the relational model. It
  calls `audit.enable(table, 'Product', key_columns, company_column, columns_json)` for each audited
  entity. The attributes stay the single source of truth, and `audit.enable` rejects unknown columns so
  schema drift fails the deploy.
- **`changes` shape.** Keys are CLR property names. `jsonb` columns are diffed to leaves
  (`Profile/deliveryContact/phone`) by `audit.jsonb_delta`. `pii_paths` uses `audit.path_matches`. Timestamps are formatted exactly as
  System.Text.Json writes them. Deletes snapshot every tracked column. Updates that touch only ignored
  columns (`updated_at`, `view_count`) write nothing.
- **Who and what, through session settings.** `AuditSessionInterceptor` publishes the scope's
  `AuditContext` (actor, channel, IP, UA, on-behalf-of, and the operation the Warp pipeline pushed) as
  `audit.*` session settings. They are session-level so they survive autocommit, and they are re-sent
  with every write so pooling or a rollback can't leave stale values behind. LINQ reads are skipped.
  Without that context (psql, another service), the row says `channel = Database`,
  `actor_display = <db role>`, `operation = unattributed`.
- **The context travels with the statement,** so most writes cost no extra round trip:

  | Command | How the context arrives | Extra round trips |
  |---|---|---|
  | `SaveChanges` | `audit.publish(…)` inside each `UPDATE`/`DELETE`'s `WHERE` | 0 |
  | `ExecuteUpdate` / `ExecuteDelete` | leading `select set_config(…)` in the same command | 0 |
  | `ExecuteSql` | separate command first | 1 |

  `SaveChanges` can't take a leading statement: EF reads rows affected by statement index, so one would
  shift every count and stale rows would stop raising `DbUpdateConcurrencyException`. `audit.publish` goes
  on every statement, not only the first, because a stale first row would never run it. Raw SQL stays
  separate because some of it (`VACUUM`) can't run in a multi-statement command.
- **The rewrite depends on EF's SQL format.** `AddPublishClause` matches the `UPDATE`/`DELETE`/`WHERE`
  lines EF writes. If a statement ever doesn't match, it throws instead of letting the write be recorded
  without context, so a format change after a package upgrade fails loudly.
- **A host that never wired the channel fails the write.** `SaveChanges` and bulk statements throw
  rather than being recorded as an out-of-band change.
- **BEFORE, named `zz_audit_capture`.** The `zz_` prefix makes it fire last among BEFORE triggers, so it
  sees the final `NEW`. If the statement fails, the transaction removes the audit row along with it.
- `SECURITY DEFINER` on `audit.record` means the app role needs no INSERT on `audit.entries`.
  `audit.untracked_tables` is the "every table is audited" guard, and a test asserts it is empty.

Moving to EF migrations: `migrationBuilder.Sql(AuditTriggerInstaller.InstallScript)` installs the
functions. The `audit.enable` calls must be re-run whenever audited columns change. Either call
`AuditTriggerInstaller.InstallAsync` from a migrator (as here; it is idempotent) or emit the calls
from `BuildRegistrations` into the migration.

## Cost

The application spends no round trips on most writes, and every audited row does its work in PL/pgSQL.
Updates that leave jsonb columns alone cost about the same as writing audit rows from the application.
Changes inside a jsonb document cost roughly twice as much.

Measured against the previous design, where `SaveChanges` rows were written by the application and the
same trigger handled bulk statements without today's shortcuts: median milliseconds, Release build,
`postgres:16-alpine` in Docker with autovacuum off and a fresh connection per run. "5 ms" adds that delay
to each client-to-server send.

| Flow | Rows | Before | Now | Before, 5 ms | Now, 5 ms | Postgres CPU |
|---|---:|---:|---:|---:|---:|---:|
| `SaveChanges`, update one column | 1 | 2.8 | 3.9 | 18 | 8.7 | |
| | 10 | 3.3 | 5.2 | 18 | 15 | |
| | 100 | 22 | 18 | 26 | 24 | |
| | 1,000 | 72 | 86 | 96 | 94 | 1.4× |
| | 10,000 | 806 | 808 | 1,000 | 843 | 1.5× |
| `SaveChanges`, jsonb with PII paths | 1,000 | 70 | 152 | 92 | 163 | 2.5× |
| `SaveChanges`, delete | 5,000 | 379 | 481 | 479 | 514 | 1.8× |
| `SaveChanges`, insert (not audited) | 5,000 | 231 | 220 | 293 | 270 | 1.0× |
| `ExecuteUpdate` | 10,000 | 2,068 | 405 | 2,093 | 429 | 0.2× |
| `ExecuteUpdate`, jsonb with PII paths | 10,000 | 1,277 | 912 | 1,308 | 940 | 0.7× |
| `ExecuteDelete` | 10,000 | 817 | 602 | 844 | 626 | 0.7× |
| `ExecuteDelete`, FK cascade | 10,100 | 472 | 393 | 501 | 418 | 0.8× |
| 8 concurrent `SaveChanges` | 4,000 | 116 | 130 | | | 1.5× |

- **Unchanged data is the expensive case to get wrong.** Every update compares each audited column, and
  walking an unchanged jsonb document was 75% of the trigger's time. `audit.record` now skips unchanged
  columns before any per-column work, and `audit.jsonb_delta` skips equal subtrees.
- **What remains is per-row PL/pgSQL.** The `INSERT` into `audit.entries` is about 7 µs of a row's cost and
  `audit.publish` about 2 µs.
- **Bytes sent drop** by about half for `SaveChanges` updates, and to under a quarter for deletes, because
  audit rows no longer cross the wire.

---

## Who / when / what, end to end

- **Who:** each host fills `AuditContext` when its scope opens. In the API, `AuditContextMiddleware`
  reads it from headers (demo only: the Blazor app sends the persona you picked, and a real host would
  use the authenticated principal). The migrator, `ActivityGenerator` and tests use `Persona`.
- **What:** `AuditOperationBehavior<,>` is an open-generic Warp `IPipelineBehavior`, registered by the
  source generator. It pushes `Feature.RequestType` (for example `Products.AdjustProductPrices`)
  for every request, so every row carries it.
- **When:** `occurred_at` is `now()`: the start of the transaction.

## API (Warp HTTP, vertical slices)

| Slice | Route | Path exercised |
|---|---|---|
| `Audit/GetAuditFeed` | `GET /api/audit?entityType=&entityKey=&action=&actor=&operation=&before=&take=&revealPersonalData=` | read; PII masked by default |
| `Audit/GetAuditFacets` | `GET /api/audit/facets` | read |
| `Products/ListProducts`, `Customers/ListCustomers` | `GET /api/products`, `/api/customers` | read |
| `Products/RenameProduct` | `POST /api/products/rename` | SaveChanges |
| `Customers/UpdateDeliveryContact` | `POST /api/customers/delivery-contact` | SaveChanges, jsonb, PII |
| `Products/AdjustProductPrices` | `POST /api/products/adjust-prices` `{category, percent}` | bulk update |
| `Products/ArchiveExpiredProducts` | `POST /api/products/archive-expired` | bulk enum change |
| `Orders/CancelPendingOrders` | `POST /api/orders/cancel-pending` `{productId?}` | bulk update |
| `Orders/PurgeCancelledOrders` | `POST /api/orders/purge-cancelled` | bulk delete |
| `Customers/UpgradeLoyaltyTier` | `POST /api/customers/upgrade-tier` `{from, to}` | bulk enum change |
| `Products/RecordProductViews` | `POST /api/products/record-views` | `[AuditIgnore]` column → no rows |
| `Playground/RunOutOfBandSql` | `POST /api/playground/out-of-band` | separate connection, no context |
| `Playground/GenerateActivity` | `POST /api/playground/activity` | everything, several personas |

## Tests

```bash
dotnet test tests/EfAuditTrail.Tests.Unit
dotnet test tests/EfAuditTrail.Tests.Integration         # needs Docker (Testcontainers, postgres:16-alpine)
dotnet test tests/EfAuditTrail.Tests.EndToEnd            # needs Docker; boots the whole AppHost
```

- **Unit:** the convention (ignored, json, PII patterns, misuse fails the model build, class opt-out);
  trigger registrations from the Npgsql model; session delivery per command type; the `audit.publish`
  rewrite, including failing on an unexpected shape.
- **Integration, trigger:** ExecuteUpdate and SaveChanges with full context; both in one transaction;
  rollback; ignored-only updates; delete snapshots and PII; out-of-band attribution; jsonb leaves;
  timestamp format; `ExecuteSql` context; `VACUUM` through `ExecuteSql`; FK cascades; stale rows in
  single and batched saves; missing channel; unattributed operations; inserts; the untracked-tables
  guard; idempotent install.
- **Integration, SQL rules:** `audit.jsonb_delta` and `audit.is_personal` held to their expected answers.
- **Integration, bulk flows:** saves that cross EF's batch boundary, a stale row late in a large save,
  large tracked deletes and bulk updates.
- **Parity:** two identical customers get the same update and the same delete via SaveChanges and via
  ExecuteUpdate/ExecuteDelete. `changes` and `pii_paths` are compared as jsonb.
- **API:** bulk and tracked writes through Warp HTTP, carrying the caller's identity and the operation from the
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
- `AuditAction { Update, Delete }`, `AuditActorKind { Anonymous, Customer, Staff, System }` and
  `AuditChannel { Api, BackOffice, Job, Seeder, Database }`. `Database` means "no application context".
  The trigger relies on these enums being stored as text (`HasConversion<string>()`).
- `ICompanyScoped { int CompanyId }` copies the tenant onto the audit row.
  `IHaveTimestamps { CreatedAt, UpdatedAt }` holds housekeeping timestamps that are never audited.
- `EfAuditTrailDbContext` is registered with plain `AddDbContext` (Warp needs the scoped context) and
  with no retrying execution strategy.

### Adopting it in your own application

Copy `Auditing/`, then:
1. Register `AuditConvention` and `AuditSessionInterceptor` the way `DataServiceCollectionExtensions`
   does.
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
