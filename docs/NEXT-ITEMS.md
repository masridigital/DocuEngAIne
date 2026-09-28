# DocuEngAIne — Next Items

Working plan as of 2026-09-28. The 2026-08-31 revision covered the batch-merge clean-up (PR #68,
merged); this revision adds the port of the Shuvouits/Docuengine prototype. History is in git;
this file describes now.

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

## Docuengine port

[Shuvouits/Docuengine](https://github.com/Shuvouits/Docuengine) (Laravel 12 + React 18) was a
parallel prototype of this product. It is being ported feature by feature — natively, as one PR
per slice through the normal build → test → review loop — not copied: its tenant scoping has no
global safety net, it returns invitation / reset tokens in API responses, and its IP allowlist
middleware was never attached to a route. Those are not carried over.

Shipped:

- **Audit v2** — before/after diffs, actor name + target label, categories, request context,
  per-record activity feed, CSV export (formula-escaped), retention purge. Also fixed a
  cross-tenant document-version read found while porting.
- **Museum** — soft delete + archive registry + restore / permanent delete for assets, documents,
  runbooks and Keeper links. Company archive is deliberately excluded until sync, portal and
  import semantics for an archived company are decided.
- **Deactivation that means something** — a suspended user is refused everywhere, including with
  an Entra Admin app role; Owner invariants guard suspend / reactivate.
- **Access reviews** — snapshot, decide (applied immediately), complete, CSV evidence.
- **Tenant IP allowlist** — enforced on `/api/*` and `/mcp`, fails closed, with Docuengine's four
  anti-lockout rules and a host-level break-glass (`Security:DisableIpAllowlist`).
- **Security-group company scoping** — groups confine members to granted companies at View / Edit
  / Manage, via named EF query filters on every company-owned entity plus write guards; tenant-wide
  records are opt-in per group and read-only for confined users; Admins and Owners bypass. Closed
  two pre-existing gaps on the way: company and folder writes had no role check at all.
- **Asset layouts** — typed fields (eleven types, sections, help text) with values checked and
  stored in one normalized form per type; draft → validated publish → an immutable version per
  schema change while published; shared option lists (fixed values, retire-not-delete, never left
  empty under a published layout); layouts limited to chosen companies (enabling one needs Manage).
  Schema changes cannot strand data: type / list changes convert every stored value or are refused.
  The SPA gained layout and option-list admin pages and typed asset field view / edit / create.
  Closed a pre-existing gap: asset create accepted another tenant's layout id.
- **Tenant configuration** — a registered feature catalog (client portal, AI assistant, MCP server,
  access reviews; all default on) gating whole route families with `403 feature_disabled`, the
  configuration routes themselves never gated; tenant names for companies / assets / documents /
  runbooks; a header name and a contrast-checked accent color. Audited, Admin-only, with a Settings
  page in the SPA. Not carried over: Docuengine's free-form custom CSS (an exfiltration vector) and
  its regional settings (locale, timezone, date formats) — worth doing properly across every date
  the app shows, not half-applied. Logos wait on blob storage.
- **Tenant lifecycle** — Active / Suspended / Archived, changed only by platform operators named in
  host configuration (never an app role a customer's admins could self-assign). A closed tenant is
  refused on every API route with its reason, its API tokens stop working and its sync stops; data
  is kept and reactivation restores it. Audited in both the operator's and the tenant's trail.
  Docuengine's separate "inactive" state is folded into Suspended; hard tenant deletion is not
  ported (decide retention and export first).

Next, in order:

1. **Company archive** — the Museum slice deferred above.
2. **Regional settings** — tenant timezone and date / time formats, applied to every date the SPA
   shows and to expiration day boundaries.

Open decision from the scoping slice: links, flag assignments and flag definitions are still
writable by Readers (company access is checked, the tenant role is not). Decide whether flagging is
a Reader action before tightening it.

Deliberately not carried over from Docuengine's layouts: its tenant-wide "unique value" check (a
cross-company existence oracle for restricted users), and its file and relationship field types,
which were never finished there — files wait on blob storage, relationships already exist as
related-item links.

Not ported: MFA, password, session and invitation-token mechanics (Entra owns authentication);
role-carrying invitations are optional later work on top of Entra.

## Sequence

### 1. Decisions left over from the batch-merge

CI is running again, and the code-level issues the 2026-08-31 batch-merge carried onto `main` were
fixed in PR #68. Two things remain decisions, not fixes:

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
