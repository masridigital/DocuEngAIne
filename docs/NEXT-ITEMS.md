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

### 5. Enforce the authorization that already exists — **partly done**

Shipped: `RequireAdmin` now gates `/api/mcp/servers` and `/api/integrations`, as a requirement +
handler accepting an Entra `Admin`/`Owner` app-role claim **or** a `User` row with `Role >= Admin`
(claims-only would have gated these routes on an optional setup step). `/api/me` provisions `Reader`,
and `POST /api/tenant/onboard` grants the onboarding caller `Owner` in the same save as the tenant.

Still open, and the more important half: **`IResourceAuthorizationService` is still called by no
endpoint.** The README's claim that object-level `ResourceRoleAssignment` overrides a tenant-wide role
remains false — a grant of Contributor on one document is consulted nowhere. Thread `EnforceAsync`
through the asset/document/runbook/Keeper write paths, or drop the claim from the README.

Also still open: role management (see 5b) — nothing can change a `User.Role` after onboarding.

### 5b. Add role management — surfaced by the authorization work

With admin routes gated and auto-provisioning defaulted to `Reader`, **nothing in the codebase can
change a `User.Role`**. The first user in a tenant is bootstrapped to `Owner`; everyone after is a
`Reader` forever, and if that first person leaves, the tenant has no admin and no in-app recovery.

An admin-gated `PUT /api/users/{id}/role` (plus a users list) is the smallest thing that makes the
gate usable. Treat it as part of shipping authorization, not a follow-up.

Note this is only safe to ship *because* nothing has been deployed yet — there are no existing tenants
carrying the old `Contributor` default to be locked out.

### 6. Resume the feature chain

Back to `MASRI-NATIVE-PLAN.md` §8, now on a foundation that can hold it:

- Sync schedules + a SyncRun UI (runs are exposed by API, not shown in the SPA)
- Blackpoint and Composio connectors
- Phase 2C: relationship graph, Azure AI Search, client portal, Hudu import

## Azure deploy: intentionally not started

Azure has not been provisioned yet — the project is not at the testing stage. `AZURE_CREDENTIALS` is
therefore unset **by design**, so on every `main` push the `infra` job stops at `Azure login`,
`deploy-api` is skipped, and the workflow run is marked failed. That is expected, not a defect.

What follows from it, and matters when Azure *is* set up:

- The resource group, SQL server, Key Vault and App Service in `infra/` do not exist yet.
- Nothing downstream of `Azure login` has ever executed. The `migrate` job, the migration bundle, the
  run-scoped SQL firewall rule, the Key Vault read and its `SQL_ADMIN_*` fallback are all **written
  but never run**. The first real deploy exercises all of them at once — budget time for that rather
  than expecting it to be clean.
- `build-and-test` is the check that actually gates code today, and it is green.

One side effect worth deciding on: because the deploy jobs always fail, **every** run on `main` shows
red, so a genuine failure would not stand out. If that becomes a problem before Azure is ready, gate
the `infra` / `migrate` / `deploy-api` jobs on a repository variable (or move them to
`workflow_dispatch`) so `main` reads green until you deliberately turn deployment on.

## Corrections to the older plan

`MASRI-NATIVE-PLAN.md` §8 item 2 — "CompanyId on document/runbook/Keeper create-edit" — is **already
done** and was already done before this plan was written. All six request records carry
`Guid? CompanyId`, and all six paths validate it through `CompanyEndpoints.EnsureCompanyInTenantAsync`
(400 + "Company not found." for an other-tenant company), exactly as `AssetEndpoints` does. An
earlier review of this repo asserted the opposite; that was wrong, from a grep too narrow to see the
record declarations. What was genuinely missing was endpoint-level test coverage, now added in
`CompanyAttachmentTests.cs`.

Note `null` on update means "leave unchanged", never "clear", consistently across Asset, Document,
Runbook, KeeperLink and Folder. There is therefore **no way to detach** a resource from a company
through the API. That is a real gap, but a deliberate, consistent one — changing it needs a sentinel
value or a separate route, and should be decided rather than slipped in.

## AI surface — direction captured 2026-08-28

Three things Joe called for. None started; recorded here so the shape is agreed before code.

### 1. Expose DocuEngAIne's own MCP server to other harnesses

Today we are an MCP *client* (we call StackJack Compact). The ask is the other direction: publish our
documentation as MCP tools so Claude, Cursor and other harnesses can read a client's assets, docs,
runbooks, expirations and Keeper links directly.

The hard part is not the protocol, it is the trust boundary. Every existing query is scoped by
`ForTenant(currentUser)`, and `ICurrentUser` is derived from an Entra JWT on an HTTP request. An MCP
client is not a browser session, so this needs its own auth path — most likely per-tenant API tokens
or Entra client credentials — mapped onto a `ICurrentUser` a background/non-HTTP scope can supply.
That same gap blocks the sync scheduler (below), so the two should be solved together, once.

Read-only first. Keeper reveal must stay out of the tool surface, or be audit-logged exactly as the
HTTP path is.

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

- **Any tenant that already has `User` rows has no Owner.** The Owner grant lives in
  `POST /api/tenant/onboard`, which returns `Conflict` for a tenant that already exists, and the only
  role-writing route sits behind the same admin policy it would need to escape. A tenant created before
  this round — including a local development database — is therefore permanently 403 on
  `/api/integrations`, `/api/mcp/servers`, `/api/users` and `/api/resource-access`. Recovery is a direct
  `UPDATE Users SET Role = 4` or defining the Entra app roles. Drop the local database rather than
  fighting it; **write a backfill before the first real deployment carries tenants across.**
- **`ENTRA_CLIENT_ID`, `ENTRA_AUTHORITY` and `ENTRA_API_SCOPE` are baked into the SPA bundle at build
  time.** Missing secrets give a green build that ships a front end telling every visitor sign-in is not
  configured, while the API still requires a bearer token. CI now emits a warning annotation when any is
  empty; it is not a hard failure, because they are legitimately unset until Entra is configured.

## Testing debt worth naming

- No HTTP-level tests. The test project has no `Microsoft.AspNetCore.Mvc.Testing` reference, so the
  per-endpoint other-tenant 404/400 behaviours documented in the README are asserted at the service
  layer, not through the pipeline that enforces them.
- CI runs `npm run build` (which typechecks) but never `npm run lint`; oxlint is configured and unused.
