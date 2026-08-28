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

### 2. Reconcile the EF model snapshot

`DocuEngAIneDbContextModelSnapshot.cs` has no `Companies`, `McpServers`, `IntegrationConnections`,
`IntegrationMappings` or `SyncRuns` tables and no `Entities.Company` references at all;
`Company.ParentCompanyId` / `CompanyType` / `Nickname` / `Fax` / `PostalCode` / `Country` and
`McpServer.Kind` are missing too. `DependencyInjection` suppresses `PendingModelChangesWarning` to
keep `database update` working, which hides the drift instead of resolving it.

The next `migrations add` will emit `CreateTable` for tables that already exist in every deployed
database. Run `dotnet ef migrations add Phase2IntegrationsReconcile`, confirm the generated `Up()`
is **empty**, then drop the warning suppression so future drift fails loudly.

This blocks any further schema work, so it comes before anything needing a column.

### 3. Prove one live Compact pull

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

### 5. Enforce the authorization that already exists

`IResourceAuthorizationService` is registered, tested and documented, but no endpoint calls it; the
`RequireAdmin` policy is never applied; `/api/me` auto-provisions everyone as `Contributor`. Apply
`RequireAdmin` to `/api/mcp/servers` and `/api/integrations`, call `EnforceAsync` on
asset/document/runbook/Keeper writes, and default new users to `Reader`.

Needed before a second real tenant, not before the next demo.

### 6. Resume the feature chain

Back to `MASRI-NATIVE-PLAN.md` §8, now on a foundation that can hold it:

- `CompanyId` on document / runbook / Keeper create and edit (assets already accept it)
- NinjaOne devices → Computer Assets (plan lists this in 2A; only organizations ship)
- Sync schedules + a SyncRun UI (runs are exposed by API, not shown in the SPA)
- Blackpoint and Composio connectors
- Phase 2C: relationship graph, Azure AI Search, client portal, Hudu import

## Testing debt worth naming

- No HTTP-level tests. The test project has no `Microsoft.AspNetCore.Mvc.Testing` reference, so the
  per-endpoint other-tenant 404/400 behaviours documented in the README are asserted at the service
  layer, not through the pipeline that enforces them.
- CI runs `npm run build` (which typechecks) but never `npm run lint`; oxlint is configured and unused.
