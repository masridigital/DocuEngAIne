# DocuEngAIne — Next Items

Working plan as of 2026-08-28, at `be7465b` (PR #11 merged, PR #12 open).

This is a build-stage plan, not a launch checklist. Nothing here says the project is late or
broken; it says which order the remaining work pays off in. It supplements
[`MASRI-NATIVE-PLAN.md`](MASRI-NATIVE-PLAN.md) §8 rather than replacing it — that document still
owns the product direction.

## The read

Phase 2A went **wide** fast: five providers (Halo, NinjaOne, CIPP, Meraki, and UniFi in PR #12)
now pull companies through StackJack Compact, each with its own mapper, live-payload fixtures, and
tenant-isolation tests. That breadth is real and the mappers are good.

Two things did not keep pace with it, and both get more expensive per provider added:

1. **Company identity never converged.** Each connection matched only its own mapping rows, so the
   same client was created once per provider. At one provider that is invisible. At five it is a
   4× duplicate rate on every tenant, and it directly contradicts the plan's own success criterion:
   *"a tech opens a Company and sees Halo + Ninja identity."*
2. **No pull has ever run live.** Every mapper is tested against a canned string. The one component
   that talks to Compact — `HttpMcpClient` — has no test and has never been exercised against the
   real endpoint.

So the highest-value next work is **depth, not the sixth provider**: make companies converge, then
prove one pull works end to end against live Compact.

## Sequence

### 0. Merge PR #12 (UniFi)

Green on `build-and-test`, same shape as #9–#11. Merging it closes the Compact provider set and
stops the branch drifting behind `main`.

### 1. Company identity — **done in this pass**

`CompanyIdentity` + `CompanyMatchIndex` (`src/DocuEngAIne.Infrastructure/Integrations/CompanyIdentity.cs`),
wired into `IntegrationSyncService.SyncFromPayloadAsync`.

Before creating a company, the sync now matches an existing one by, in order:

| Order | Key | Why it is safe |
|---|---|---|
| 1 | Provider id (typed Halo/Ninja columns + `ExternalIdsJson`) | Authoritative — the provider said so |
| 2 | Normalized primary domain | `https://WWW.Example.com/x` and `example.com` are one host |
| 3 | Exact normalized name | `"ExampleCo, Inc."` and `"Example Co Inc"` collapse alike |

A key that would resolve to two different companies is treated as **ambiguous and skipped** —
duplicating a company is recoverable, merging two real clients is not. Legal suffixes are
deliberately *not* stripped, for the same reason. Every match is recorded on the mapping as
`{"matchedBy":"provider-id|primary-domain|name"}`.

Every provider now also stamps its external id into `Company.ExternalIdsJson` — the column already
existed and was unused, so CIPP, Meraki and UniFi identity is queryable for the first time, with no
schema change.

**Deliberately not done:** no new sync-policy column to gate matching. Adding one means another
hand-written migration on top of an already-stale model snapshot (item 2). Revisit once the
snapshot is reconciled — if matching turns out to need an off switch.

### 2. Reconcile the EF model snapshot — **done** (`Phase2IntegrationsReconcile`, empty `Up()`)

Snapshot now includes Companies / MCP / integration / sync tables and Phase 2 columns; `PendingModelChangesWarning` suppression is removed.

`DocuEngAIneDbContextModelSnapshot.cs` has no `Companies`, `McpServers`, `IntegrationConnections`,
`IntegrationMappings` or `SyncRuns` tables and no `Entities.Company` references at all;
`Company.ParentCompanyId` / `CompanyType` / `Nickname` / `Fax` / `PostalCode` / `Country` and
`McpServer.Kind` are missing too. `DependencyInjection` suppresses `PendingModelChangesWarning` to
keep `database update` working, which hides the drift instead of resolving it.

The next `migrations add` will emit `CreateTable` for tables that already exist in every deployed
database.

Run **`./scripts/reconcile-model-snapshot.sh`** on a machine with the .NET 10 SDK. It regenerates
the snapshot and then *proves* the generated `Up()` is empty — a pure catch-up with no schema
change — failing loudly if it is not. It prints the remaining manual steps: read the snapshot diff,
drop the `PendingModelChangesWarning` suppression in `DependencyInjection.cs`, run the tests, and
commit the empty migration together with the regenerated snapshot.

It cannot run in the Claude Code web container — `builds.dotnet.microsoft.com` is blocked there by
egress policy, so no .NET SDK is available.

This blocks any further schema work, so it comes before anything needing a column.

### 3. Prove one live Compact pull

> Needs only a Key Vault secret and a reachable Compact endpoint — **not** the Azure deploy. This is
> runnable well before the app is hosted, and it is the gate on whether any of the five connectors work.

Provision the Compact secret in Key Vault, point a Halo connection at it, and run a real sync.
Expect to find gaps in `HttpMcpClient` first — it POSTs bare JSON-RPC with no
`Accept: application/json, text/event-stream`, no `initialize` handshake, no `Mcp-Session-Id`, no
`MCP-Protocol-Version`, and no SSE frame parsing, so a spec-conformant server answers 406 or returns
an event-stream body the mappers cannot read. It also proceeds without an `Authorization` header
when the secret name resolves to nothing, turning a config mistake into a vendor 401.

Probe the endpoint before rewriting — Compact may be more permissive than the spec requires. Then
add a test against a stub `HttpMessageHandler`, so the one component touching the outside world
stops being the only untested one.

This is the gate on whether any of the five connectors actually work.

### 4. Make the app reachable in a browser

Two independent gaps, both fine for local development and both blocking the first real demo:

- **SPA auth.** No MSAL dependency; every call in `useApi.ts` is a bare `fetch` with no
  `Authorization` header, while all thirteen endpoint groups sit behind `.RequireAuthorization()`.
  Add `@azure/msal-browser` + `@azure/msal-react`, route calls through one authenticated fetcher
  that throws on `!res.ok`, plumb client id and authority into the SPA build, and register the
  production redirect URI.
- **Migrations on deploy.** `azure-deploy.yml` never applies them. Build a
  `dotnet ef migrations bundle` artifact and run it in `deploy-api` before the zip deploy.

### 5. Enforce the authorization that already exists — **done**

Shipped: `RequireAdmin` now gates `/api/mcp/servers` and `/api/integrations`, as a requirement +
handler accepting an Entra `Admin`/`Owner` app-role claim **or** a `User` row with `Role >= Admin`
(claims-only would have gated these routes on an optional setup step). `/api/me` provisions `Reader`,
and `POST /api/tenant/onboard` grants the onboarding caller `Owner` in the same save as the tenant.

Shipped: every asset / document / runbook / Keeper write (`POST`/`PUT`/`DELETE`) goes through
`ResourceWriteGuard`, which consults `IResourceAuthorizationService.CanWriteAsync` (the boolean
form of `EnforceAsync` — the throwing form would surface as a 500). A Contributor grant on one
document lets that Reader write that document and no other; absence is 403. Tenant-wide
Admin/Owner (and Contributor) write without a grant. Creates gate on the tenant-wide role because
nothing exists yet to name in a grant.

Role management shipped in 5b.

### 5b. Add role management — **done**

Shipped: admin-gated `GET /api/users` and `PUT /api/users/{id}/role`. Granting or revoking
`Owner` requires an existing Owner, with a hatch that lets any admin-policy caller appoint one
when the tenant has zero active Owners.

### 6. Resume the feature chain

Back to `MASRI-NATIVE-PLAN.md` §8, now on a foundation that can hold it:

- Sync schedules + a SyncRun UI (runs are exposed by API, not shown in the SPA)
- Blackpoint and Composio connectors
- Phase 2C: Azure AI Search, Hudu import. Relationship graph shipped (`GET /api/companies/{id}/graph`). Client portal skeleton shipped (`GET /api/portal`, `/portal`; documents / expirations / Keeper metadata; no reveal).

## Azure deploy: intentionally not started

Azure has not been provisioned yet — the project is not at the testing stage. `AZURE_CREDENTIALS` is
therefore unset **by design**. `infra` / `migrate` / `deploy-api` now run only when the repository
variable `DEPLOY_AZURE` is `true`, so `main` stays green until you turn deployment on.

What follows from it, and matters when Azure *is* set up:

- The resource group, SQL server, Key Vault and App Service in `infra/` do not exist yet.
- Nothing downstream of `Azure login` has ever executed. The `migrate` job, the migration bundle, the
  run-scoped SQL firewall rule, the Key Vault read and its `SQL_ADMIN_*` fallback are all **written
  but never run**. The first real deploy exercises all of them at once — budget time for that rather
  than expecting it to be clean.
- `build-and-test` is the check that actually gates code today, and it is green.

## Corrections to the older plan

`MASRI-NATIVE-PLAN.md` §8 item 2 — "CompanyId on document/runbook/Keeper create-edit" — is **already
done** and was already done before this plan was written. All six request records carry
`Guid? CompanyId`, and all six paths validate it through `CompanyEndpoints.EnsureCompanyInTenantAsync`
(400 + "Company not found." for an other-tenant company), exactly as `AssetEndpoints` does. An
earlier review of this repo asserted the opposite; that was wrong, from a grep too narrow to see the
record declarations. What was genuinely missing was endpoint-level test coverage, now added in
`CompanyAttachmentTests.cs`.

Note `null` on update still means "leave unchanged", never "clear", consistently across Asset,
Document, Runbook, KeeperLink and Folder. Detach is explicit: `companyIdClear: true` or the empty
GUID sentinel on those update records. Other-tenant company is still 400.

## AI surface — direction captured 2026-08-28

Three things Joe called for. None started; recorded here so the shape is agreed before code.

### 1. Expose DocuEngAIne's own MCP server to other harnesses — **done in this pass**

`POST /mcp` is a Streamable HTTP MCP server (GET `/mcp` documents it). Auth is a per-tenant API
token (`POST/GET/DELETE /api/tokens`, hash stored, plaintext once), mapped onto `TokenCurrentUser`
via `CurrentUserScope` so `ForTenant` works without a browser JWT. The sync scheduler keeps its
own `BackgroundCurrentUser` / `IBackgroundTenantContext`; `CurrentUser` reads ambient token first,
then Entra JWT, then the bound scheduler tenant.

Read-only tools: `list_companies`, `get_company`, `list_assets`, `get_asset`, `list_documents`,
`list_runbooks`, `list_expirations`, `list_keeper_links` (titles + URLs only). Keeper reveal is not a
tool.

### 2. Promote content into documentation

A path from "something we learned" to "a documented article", rather than expecting techs to write
docs from scratch. Candidate sources: a sync result, a completed runbook run, an asset's change
history, a resolved Halo ticket.

**Ambiguous as specified — confirm before building.** It could mean a one-click "promote this into a
Document", an AI drafting step over the source material, or a review queue of suggestions. The
existing `FlagDefinition` / review-queue machinery and `Document` versioning already cover part of
whichever shape wins.

### 3. Screen-recording capture (future)

A browser extension in the spirit of Hudu's, recording a workflow and turning it into a documented
procedure. Explicitly a later phase. It lands naturally on `Runbook` / `RunbookStep` rather than
`Document`, since the output is an ordered procedure. Needs blob storage, which the plan already
defers to Phase 3 for photos — same dependency, so the two should be planned together.

## Pre-deploy checklist (from the review of the authorization round)

Two things that are harmless today only because nothing has been deployed, and become real the moment
something is:

- **Owner backfill is in place.** Tenants created before onboard granted Owner recover through
  `POST /api/tenant/claim-owner`: if the tenant has zero active Owner/Admin users, the first
  authenticated caller becomes Owner; a second caller (or a tenant that already has an Owner) gets
  409. SQL `UPDATE` / Entra app roles remain available but are no longer the only path.
  `/api/tokens` is on the same admin gate as `/api/integrations` and `/api/mcp/servers`.
- **`ENTRA_CLIENT_ID`, `ENTRA_AUTHORITY` and `ENTRA_API_SCOPE` are baked into the SPA bundle at build
  time.** Missing secrets give a green build that ships a front end telling every visitor sign-in is not
  configured, while the API still requires a bearer token. CI now emits a warning annotation when any is
  empty; it is not a hard failure, because they are legitimately unset until Entra is configured.

## Testing debt worth naming

- No HTTP-level tests. The test project has no `Microsoft.AspNetCore.Mvc.Testing` reference, so the
  per-endpoint other-tenant 404/400 behaviours documented in the README are asserted at the service
  layer, not through the pipeline that enforces them.
