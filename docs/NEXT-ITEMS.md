# DocuEngAIne — Next Items

Working plan as of 2026-08-31, at `8c03d4e` (all ten reviewed PRs merged) plus the review-fix
branch. Previous revision of this document described the repo at `be7465b` (2026-08-28) and had
drifted badly — most of its "next" items have since shipped. History is in git; this file
describes now.

This is a build-stage plan, not a launch checklist. It supplements
[`MASRI-NATIVE-PLAN.md`](MASRI-NATIVE-PLAN.md) §8 rather than replacing it — that document still
owns the product direction.

## The read

Since `be7465b`, roughly fifty PRs landed in two days. The wave was wide and mostly good:

- **Eleven providers pull companies through StackJack Compact end to end** — Halo, NinjaOne,
  CIPP, Meraki, UniFi, Action1, Autotask, Blackpoint (CompassOne), DefensX, Pax8, and Slide are
  dispatched by `IntegrationSyncService.SyncAsync`, converge through `CompanyIdentity` /
  `CompanyMatchIndex`, and stamp `ExternalIdsJson`. PR #66 additionally wired Halo sites/users,
  Action1 endpoints, CIPP devices, Meraki networks, and UniFi sites/devices into child passes.
- **A dozen mappers still have no caller** (Halo assets, Ninja locations, CIPP users, Keeper
  MSP/SCIM, Datto, Huntress, ImmyBot, Liongard, Azure subscriptions/resource groups, Graph
  partner/delegated-admin, DNSFilter, ThreatLocker). They are tested dead code until a sync pass
  dispatches them — see "Decide the mapper backlog" below.
- **The scheduler is real**: `IntegrationSyncHostedService` polls every minute;
  `SyncCadencePolicy` budgets 20% of the detected StackJack allowance (plan auto-detected from
  `stackjack_session_info`; reported limit beats tier; unknown plan = manual-only; overrides can
  slow but never out-run the plan). The review-fix branch adds failure backoff
  (`LastAttemptAt`), stale-Running reaping, and an overlap guard.
- **SPA auth shipped** (MSAL, authenticated fetcher); **HTTP pipeline tests shipped**
  (`Microsoft.AspNetCore.Mvc.Testing` via `TestHost`); **model snapshot reconciled**
  (`Phase2IntegrationsReconcile`, empty `Up()`), though later hand-edited snapshots are drifting
  again — regenerate with `./scripts/reconcile-model-snapshot.sh` on an SDK machine when one is
  available.
- **Outbound MCP + API tokens shipped**, now with an audited `reveal_keeper_link` tool
  (`list_keeper_links` returns titles and ids only; tokens support optional expiry).
- **One-click promote shipped**: runbook runs / sync results / flags promote into Documents.

## Blockers

### 0. GitHub Actions is not running — nothing since 2026-08-30 20:38 UTC compiles in CI

Runner never starts: jobs "complete" in 2–4 seconds with zero steps and no logs, and open PRs get
zero check runs. That is the billing / spending-limit failure signature, not a workflow bug (the
workflow is unchanged since the last green run). **Only the org owner can fix it** — GitHub org
Settings → Billing → Actions. Until then ~23 merged pushes (~12.5k lines) and every open PR are
unverified by any compiler; there is no .NET SDK in the dev container to compensate.

## Sequence

### 1. Restore CI, then clean up after the batch-merge

All ten open PRs were merged on 2026-08-31 before CI was restored, so none of them ever compiled
in CI, and the known issues they carried are now on `main`. The review-fix branch (PR #68)
addresses the code-level ones:

- **#66's per-pass status re-check** — a child pass ran after an earlier failed pass and could
  stamp `LastSyncAt` on a Failed run.
- **#65's retired Anthropic model default** (`claude-sonnet-4-20250514`) and its two blocking
  `GetAwaiter().GetResult()` test asserts (xUnit1031 — CI enforces 0 warnings).
- **#50's dead portal expirations widget** — `[FromQuery] bool showExpired` with no default is a
  required parameter in minimal APIs, and the SPA never sends it (same latent bug on
  `/api/expirations`).

Two things remain decisions, not fixes:

- **#50 shipped without a portal identity.** `/api/portal` is plain `RequireAuthorization()` —
  any tenant user sees every portal-enabled company, and a client onboarded as a Reader "to try
  the portal" can call every other authenticated GET in the tenant. Fine while no external user
  can authenticate; decide the portal auth story (Entra External ID / magic links / portal
  tokens) before onboarding any client.
- **#49's in-memory search index** is a singleton fed by document writes — unbounded, per-process,
  lost on restart, and unpublished docs are searchable by any tenant user. Acceptable as
  scaffolding; revisit when Azure AI Search is provisioned.

Stale branches safe to delete: `feature/integrations-mcp`, `cursor/unifi-host-pull-8cbd`.

### 2. Decide the mapper backlog: wire or stop building

A dozen mappers are dead code. Each is well-tested against fixtures, but no sync pass dispatches them,
and each unwired provider that later gets wired without an `IntegrationProvider` enum value would
fall through to the `"custom"` provider key and collide in `ExternalIdsJson`. Either schedule the
site/user/device passes that consume them (the Ninja device pass is the template) or stop merging
new ones until the consumers exist.

### 3. Prove one live Compact pull

Still the gate on whether any connector works, and still not done — every pull has only ever run
against fixtures. Needs a Key Vault secret and a reachable Compact endpoint, **not** the Azure
deploy. `HttpMcpClient` is in much better shape than at `be7465b` (initialize handshake,
`Mcp-Session-Id`, SSE parsing with id correlation, stub-handler tests), so the remaining risk is
credential plumbing and vendor quirks, not protocol basics.

### 4. Resume the feature chain

Back to `MASRI-NATIVE-PLAN.md` §8: SyncRun history is now in the API and SPA (#52); LLM chat +
document assist (#65) and search scaffolding (#49) shipped; the portal skeleton (#50) waits on its
identity story; Hudu one-shot import (#33) and the Composio harness (#45) shipped.

## Azure deploy: intentionally not started

Unchanged: the project is not at the testing stage, so Azure is not provisioned and
`AZURE_CREDENTIALS` is unset **by design**. `infra` / `migrate` / `deploy-api` run only when the
repository variable `DEPLOY_AZURE` is `true`. Everything downstream of `Azure login` is written
but has never executed — budget time for the first real deploy. When deploys start, migrations
apply via the `migrate` job's EF bundle.

## AI surface

1. **Expose DocuEngAIne's own MCP server** — done. `POST /mcp` (Streamable HTTP), per-tenant API
   tokens with optional expiry, `TokenCurrentUser` via `CurrentUserScope`. Read-only tools:
   `list_companies`, `get_company`, `list_assets`, `get_asset`, `list_documents`,
   `list_runbooks`, `list_expirations`, `list_keeper_links` (titles + ids only), and the audited
   `reveal_keeper_link`.
2. **Promote content into documentation** — shipped as one-click promote (runbook run → Document,
   with versioning), and document assist (`/api/documents/{id}/assist`, preview by default) now
   provides the AI summarize/rewrite step on top of it.
3. **Screen-recording capture** — unchanged: a later phase, lands on `Runbook`/`RunbookStep`,
   blocked on blob storage (Phase 3), plan together with photos.

## Testing debt worth naming

- HTTP-level tests now exist (`TestHost` + `HttpPipelineTests`), closing the old gap. Coverage is
  route-auth-shaped; per-endpoint other-tenant behaviour is still mostly asserted at the service
  layer.
- No mapper has ever been run against live Compact output that wasn't first pasted into a fixture
  — that is item 3, not more unit tests.
- The unwired mappers carry full test suites that will silently rot if item 2 lands on
  "stop building".
- #67's document-assist apply path (`Apply=true`: version snapshot + write) has no test — preview,
  403, and cross-tenant cases are covered.
