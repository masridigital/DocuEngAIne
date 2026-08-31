# DocuEngAIne — Next Items

Working plan as of 2026-08-31, at `e03ad39` plus the review-fix branch. Previous revision of this
document described the repo at `be7465b` (2026-08-28) and had drifted badly — most of its "next"
items have since shipped. History is in git; this file describes now.

This is a build-stage plan, not a launch checklist. It supplements
[`MASRI-NATIVE-PLAN.md`](MASRI-NATIVE-PLAN.md) §8 rather than replacing it — that document still
owns the product direction.

## The read

Since `be7465b`, roughly fifty PRs landed in two days. The wave was wide and mostly good:

- **Ten providers pull companies through StackJack Compact end to end** — Halo, NinjaOne, CIPP,
  Meraki, UniFi, Action1, Autotask, Blackpoint (CompassOne), DefensX, and Pax8 are dispatched by
  `IntegrationSyncService.SyncAsync`, converge through `CompanyIdentity` / `CompanyMatchIndex`, and
  stamp `ExternalIdsJson`.
- **~14 more mappers exist but nothing calls them** (Halo sites/users/assets, Ninja locations,
  Meraki networks, UniFi sites/devices, CIPP devices/users, Keeper MSP/SCIM, Slide, Datto,
  Huntress, ImmyBot, Liongard, Azure subscriptions/resource groups, Graph partner/delegated-admin,
  DNSFilter, ThreatLocker). They are tested dead code until a sync pass dispatches them — see
  "Decide the mapper backlog" below.
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

### 1. Restore CI, then merge the open PR set in order

Verdicts from the 2026-08-31 review (details in the review report):

1. **#66** (Compact mapper wiring) — fix the per-pass status re-check first (contacts pass runs
   after a failed locations pass), then merge, then retitle.
2. **#61, #59, #53, #52** — merge as-is (#52 needs a retitle; it is not a duplicate).
3. **#65** (LLM providers) — fix the retired Anthropic model id default and the two blocking-wait
   test asserts, then merge. **#67** (document assist) is stacked on #65 and merges after it.
4. **#49** (Azure AI Search scaffolding) — merge with notes; rebase after #67.
5. **#33** (Hudu import) — merge as-is; rebase README/DI after #49.
6. **#45** (Composio harness) — merge as-is, any time.
7. **#50** (client portal) — **hold**: it has no portal identity (any tenant user sees every
   portal-enabled company; the flag isolates nothing). Decide the portal auth story first.

Stale branches safe to delete: `feature/integrations-mcp`, `cursor/unifi-host-pull-8cbd`.

### 2. Decide the mapper backlog: wire or stop building

~14 mappers are dead code. Each is well-tested against fixtures, but no sync pass dispatches them,
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

Back to `MASRI-NATIVE-PLAN.md` §8: SyncRun UI in the SPA, the LLM/document-assist and search PRs
above, then the portal once its identity story is decided.

## Azure deploy: intentionally not started

Unchanged: the project is not at the testing stage, so Azure is not provisioned and
`AZURE_CREDENTIALS` is unset **by design**. `infra` / `migrate` / `deploy-api` run only when the
repository variable `DEPLOY_AZURE` is `true`. Everything downstream of `Azure login` is written
but has never executed — budget time for the first real deploy. When deploys start, migrations
apply via the `migrate` job's EF bundle.

## AI surface

1. **Expose DocuEngAIne's own MCP server** — done. `POST /mcp` (Streamable HTTP), per-tenant API
   tokens with optional expiry, `TokenCurrentUser` via `CurrentUserScope`. Read-only tools:
   `list_companies`, `get_company`, `list_assets`, `list_documents`, `list_runbooks`,
   `list_expirations`, `list_keeper_links` (titles + ids only), and the audited
   `reveal_keeper_link`.
2. **Promote content into documentation** — shipped as one-click promote (runbook run → Document,
   with versioning). An AI drafting step over the source material is the natural next increment —
   PR #65/#67 provide the LLM plumbing.
3. **Screen-recording capture** — unchanged: a later phase, lands on `Runbook`/`RunbookStep`,
   blocked on blob storage (Phase 3), plan together with photos.

## Testing debt worth naming

- HTTP-level tests now exist (`TestHost` + `HttpPipelineTests`), closing the old gap. Coverage is
  route-auth-shaped; per-endpoint other-tenant behaviour is still mostly asserted at the service
  layer.
- No mapper has ever been run against live Compact output that wasn't first pasted into a fixture
  — that is item 3, not more unit tests.
- The 21 unwired mappers carry full test suites that will silently rot if item 2 lands on
  "stop building".
