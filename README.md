# DocuEngAIne

An MSP documentation platform — a modern, secure, AI-ready alternative to IT Glue / Hudu.

Built for Microsoft-centric environments: **Azure App Service**, **Azure SQL**, **Entra ID** authentication, **Azure Key Vault** secrets, and a **React** SPA frontend. No containers, no Docker.

## Stack

- **.NET 10 / ASP.NET Core** Web API
- **React 19 + TypeScript + Vite** SPA, built into the API's `wwwroot/`
- **Azure App Service (Linux, code/zip deploy)** — no Docker per Masri infra preference
- **Azure SQL Database**
- **Azure Key Vault** for secrets and connection strings
- **Microsoft Entra ID** (JWT Bearer) for auth and tenant identity
- **EF Core 10** with SQL Server provider
- **Azure Bicep** for infrastructure
- **GitHub Actions** for CI/CD

## Project Layout

```text
src/
  DocuEngAIne.Core          # Domain entities, interfaces, enums
  DocuEngAIne.Infrastructure # EF Core, identity, Key Vault, DI wiring
  DocuEngAIne.Api           # Minimal APIs, health checks, Swagger, wwwroot SPA
  DocuEngAIne.Web           # React SPA (Vite)
infra/                      # Bicep modules
tests/                      # xUnit + EF InMemory tests
```

## Domain Model

- **Tenant** — isolation boundary; seeded from the Entra `tid` claim on first login.
- **Company** — client space (distinct from Entra tenant). Optional Halo/Ninja IDs and portal URLs (Open in Halo / Open in Ninja). URLs only; no secrets. Every provider's external id is also recorded in `ExternalIdsJson`, which is how sync converges Halo/Ninja/CIPP/Meraki/UniFi/Action1/Autotask/Blackpoint/DefensX/Pax8/Slide onto one company instead of one per connection. One-shot IT Glue / Hudu imports stamp `ExternalIdsJson` keys `itglue` and `hudu` the same way; neither is a live `IntegrationProvider`.
- **McpServer / IntegrationConnection / IntegrationMapping / SyncRun** — MCP registry (StackJack Compact or Composio) and PSA/RMM sync. Secrets live in Key Vault names only.
- **User** — mapped to Entra object ID, email, and tenant-wide role. Admins can suspend / reactivate users (`POST /api/users/{id}/deactivate|activate`); a suspended user is refused on every authenticated route — even with an Entra Admin/Owner app role — and their stored role and grants confer nothing. You cannot suspend yourself or the last active Owner, and only an Owner can suspend or reactivate an Owner.
- **Asset / AssetType / FieldDefinition / CustomFieldValue** — flexible assets with custom fields. Optional Halo asset / Ninja device portal URLs (Open in Halo / Open in Ninja) and `ExternalIdsJson` (same shape as Company). URLs only; no secrets. Sync does not stamp these yet.
- **Asset layouts** (`AssetType` + `AssetTypeVersion` / `AssetTypeCompanyActivation`, `OptionList` / `OptionListItem`) — a layout's fields are typed (Text, Markdown, Number, Date, DateTime, Url, Email, Phone, Checkbox, Select, MultiSelect), grouped into optional sections with help text. Values are checked and stored in one form per type (invariant numbers, `yyyy-MM-dd` dates, round-trip UTC date-times, `true`/`false`, http/https links only, option values — a multi-select as a JSON array); required fields are enforced on create and cannot be cleared. A layout built in the app starts as a **draft** and is offered for new assets once **published**, which validates it (at least one field, unique names, expiration only on date fields, every choice field on an active list with an active option). While published, every schema change is re-checked and recorded as an immutable **version** (schema snapshot + summary + author). A layout can be limited to chosen companies (enabling one needs Manage on it). No change may strand data: a field type or list change converts every stored value — Museum included — to the new form or is refused, a field with values cannot be removed, a layout in use cannot be deleted, and an asset cannot switch layouts while it holds values. Option lists are shared across layouts; an option's stored value is fixed at creation so labels can be renamed, options are retired instead of deleted, and neither a list nor its last active option can be switched off while a published layout needs it. Layouts created by sync and one-shot imports are published from the start.
- **Document** — KB articles with **versioning** and Azure AI Search scaffolding (`ISearchService`; title / body / companyId / tenantId). Optional `FolderId`.
- **DocumentFolder** — nested KB folders (`ParentId`). Optional `CompanyId` (null = central KB; set = company KB). Tenant-scoped.
- **Runbook / RunbookStep / RunbookRun** — ordered SOPs and checklists with start/complete/cancel run history. A completed run can be promoted into a Document (one-click, not AI). Tenant-wide books are templates; optional `CompanyId` is the per-client instance. Not a second process product. No local secrets.
- **KeeperLink** — links to credentials in **Keeper**; no secrets are stored locally.
- **AccessReview / AccessReviewItem** — periodic user-access certification (SOC 2 / cyber-insurance evidence). Admin-gated `/api/access-reviews`: create a draft, start it (snapshots every active user's role and per-resource grants), decide each user — Retain, Revoke (suspends immediately) or ChangeRole (applies immediately) through the same last-Owner / Owner-requires-Owner guards as the Users page, and never on your own access except Retain — then complete, which is refused while anything is pending. `GET /{id}/export` is the CSV evidence.
- **ArchiveEntry** — the Museum. DELETE on an asset / document / runbook / Keeper link archives it (soft delete via `DeletedAt` + a global EF query filter, so it vanishes from every list, rollup, portal and MCP read) with an optional `?reason=`. `/api/archive` lists entries and restores them (same per-resource write gate as delete; a slug taken while archived is suffixed `-restored`). `DELETE /api/archive/{id}` is Admin-only and permanent: it removes the row plus its child rows and the flags / links / grants that name it, and keeps the entry as a tombstone. Sync skips archived devices instead of re-creating them.
- **SecurityGroup / SecurityGroupMember / SecurityGroupCompanyGrant** — company scoping. A member of any group that grants companies is confined to those companies, at the highest level any of their groups grants: **View** (read), **Edit** (create and change records), **Manage** (also archive / restore / delete them, and change the company's status, portal flag or parent). A group with no company grants restricts nobody; Admins and Owners (Entra app role or stored role) are never restricted. Records with no company (central KB, runbook templates, tenant-wide Keeper links) are visible to a confined user only if one of their groups has `IncludeTenantWide`, and are never writable by them. Enforced by named EF query filters on every company-owned entity (so lists, rollups, the portal and the Museum are scoped without opting in), a per-request scope resolved after authorization, write guards on every create / update / archive, and a post-filter on search hits. `/mcp` API tokens are tenant-wide by design.
- **Tenant lifecycle** (`Tenant.Status`: Active / Suspended / Archived, with reason, time and who changed it; replaces `IsActive`) — changed only from the platform console by the operators listed in `Platform:Operators` (Entra tenant id + object id pairs on the host: an app role cannot carry this, since each customer's own administrators control role assignments in their directory). A suspended or archived tenant is refused on every `/api` route with `403 {"error":"tenant_suspended"|"tenant_archived","reason":…}` (after the IP allowlist, before authorization; cached 30 s per instance), its `/mcp` tokens authenticate nobody, and its sync is never scheduled; its data is kept and reactivating restores it. Suspending needs a reason, an archived tenant can only be reactivated, and an operator cannot close their own tenant. Each change is audited in the operator's log and, with the reason, in the affected tenant's.
- **Tenant configuration** (`TenantFeatureSetting`; `Tenant.DisplayName` / `AccentColor` / `TerminologyJson`) — optional features from a registered catalog: `client_portal` (`/api/portal`), `ai_assistant` (LLM chat and document summarize / rewrite; the provider config stays readable), `mcp_server` (`/mcp` for API-token agents) and `access_reviews`. All default on, a stored choice wins, and an unknown key can be neither stored nor gated on. While one is off, every route it covers answers `403 {"error":"feature_disabled","feature":…}` for that tenant only; the configuration routes are never gated, so it can always be turned back on. Sign-in, users, roles, security groups, audit and the IP allowlist are core and never features. Terminology renames companies / assets / documents / runbooks (singular and plural; only overrides are stored). Branding is a header name and an accent color that must pass WCAG AA against the app's dark background; no custom CSS (it can read page content out through selectors) and no logo until blob storage exists. Admin-only writes, each audited with its before/after diff.
- **IpAllowlistEntry** — the tenant IP allowlist (`Tenant.IpAllowlistEnabled` + one normalized CIDR per row, IPv4 or IPv6). While it is on, every tenant request — `/api/*` (middleware, before authorization) and `/mcp` (after token auth) — from an address outside the active entries gets `403 {"error":"ip_not_allowed"}`; an unknown client address fails closed. It cannot be turned on without an active entry covering the admin's own address, and while it is on no edit, deactivation or removal may leave that address uncovered. `Security:DisableIpAllowlist=true` is the host-level break-glass.
- **ResourceRoleAssignment** — object-level RBAC overriding tenant-wide roles.
- **AuditLog** — action trail (tenant-scoped where applicable) with structured before/after `ChangesJson`, denormalized actor name + target label, category, and request context. Admin surface at `/api/audit-events` (filters + paging), per-record activity feed (`/activity/{type}/{id}`), CSV export (formula-injection-escaped, itself audited), and a daily retention purge (`Audit:RetentionDays`, default 365; 0 disables).
- **ApiToken** — per-tenant outbound MCP credential. SHA-256 hash stored; plaintext shown once at create. Restrict tenant FK.
- **FlagDefinition / FlagAssignment** — named color labels on companies, assets, documents, runbooks, and Keeper links. Drive the review queue. No local secrets.
- **ResourceLink** — directed related-item links between Company, Asset, Document, Runbook, and KeeperLink. Optional label. `GET /api/companies/{id}/graph` returns nodes + edges for that company (`ForTenant`). Not a Hudu visualization. `AssetDocumentLink` remains the asset↔document convenience. No local secrets.
- **LLM** — tenant-scoped chat completion (`ILlmClient`) and document assist (`POST /api/documents/{id}/assist`). Providers: Ollama (self-hosted default), Together AI, Anthropic. Config / Key Vault only; chat bodies are not persisted. Assist defaults to preview. See [`docs/LLM.md`](docs/LLM.md).

All tenant-scoped queries use `ForTenant(currentUser)`; `SaveChangesAsync` stamps `TenantId` and audit timestamps automatically.

## Local Development

1. Install .NET 10 SDK, Node 22+, and the local EF tool:
   ```bash
   dotnet tool restore
   ```

2. Configure user secrets with your Entra app registration:
   ```bash
   dotnet user-secrets init --project src/DocuEngAIne.Api
   dotnet user-secrets set --project src/DocuEngAIne.Api "EntraId:Authority" "https://login.microsoftonline.com/{tenant-id}/v2.0"
   dotnet user-secrets set --project src/DocuEngAIne.Api "EntraId:Audience" "api://{application-client-id}"
   ```

3. Run migrations against a local SQL instance:
   ```bash
   dotnet ef database update --project src/DocuEngAIne.Api --startup-project src/DocuEngAIne.Api
   ```

4. Build the SPA and run the API:
   ```bash
   cd src/DocuEngAIne.Web
   npm install
   npm run build
   cd ../..
   dotnet run --project src/DocuEngAIne.Api
   ```

5. For hot-reload frontend dev, run both:
   ```bash
   dotnet run --project src/DocuEngAIne.Api          # https://localhost:7285
   cd src/DocuEngAIne.Web && npm run dev             # http://localhost:5173, proxies /api
   ```

Swagger UI is available at `/swagger` in Development.

## Entra ID Setup

1. Register an app in **Microsoft Entra admin center**.
2. Add a platform: **Single-page application** with redirect `http://localhost:5173` for local dev and `https://{app-host}/` for production.
3. Expose an API scope (e.g., `api://{client-id}/access`).
4. Set `EntraId:Authority` to `https://login.microsoftonline.com/{tenant-id}/v2.0` and `EntraId:Audience` to `api://{client-id}`.
5. (Optional) Add app roles `Owner`, `Admin`, `Contributor`, `Reader` and assign users.

The SPA signs in with MSAL (redirect flow) and attaches a bearer token to every API call, so it needs the same registration at build time. Copy `src/DocuEngAIne.Web/.env.example` to `.env` for local dev:

| Vite variable | Value |
|---|---|
| `VITE_ENTRA_CLIENT_ID` | Application (client) ID |
| `VITE_ENTRA_AUTHORITY` | `https://login.microsoftonline.com/{tenant-id}/v2.0` |
| `VITE_ENTRA_API_SCOPE` | `api://{client-id}/access` |

These are inlined into the bundle at build time — the CI job supplies them from repository secrets. With none set the SPA renders a notice naming the missing variables rather than failing blank.

## Azure Infrastructure

Deploy with Bicep:

```bash
az group create --name rg-docuengaine-prod --location eastus
az deployment group create \
  --resource-group rg-docuengaine-prod \
  --template-file infra/main.bicep \
  --parameters environment=prod \
               sqlAdminLogin=... \
               sqlAdminPassword=... \
               entraAuthority=... \
               entraAudience=...
```

Optional Entra admin on the SQL server (so `CREATE USER FROM EXTERNAL PROVIDER` works without the grant script creating one):

```bash
               entraAdminLogin='James' \
               entraAdminObjectId='{object-id}'
```

What gets provisioned:

- App Service Plan (Linux, B2)
- App Service with system-assigned managed identity
- Azure SQL server + database (SQL admin kept for the migrate job and break-glass)
- Optional SQL Entra admin when `entraAdminLogin` / `entraAdminObjectId` are passed
- Azure Key Vault with RBAC and the SQL-admin connection string (used by the migrate job, not the app)
- Role assignment granting the app **Key Vault Secrets User**

Production App Service settings:

- `ConnectionStrings:DocuEngAIne` = `Authentication=Active Directory Default` (DefaultAzureCredential / managed identity). No SQL password.
- `Azure__Sql__UseManagedIdentity=true`, which strips any leftover `User ID` / `Password` from that string.

Local development is unchanged: `appsettings.Development.json` uses LocalDB, and a user-secrets SQL connection string still wins because `Azure:Sql:UseManagedIdentity` defaults to `false`.

After the first deploy, grant the App Service identity a contained database user (Bicep cannot run `CREATE USER ... FROM EXTERNAL PROVIDER`):

```bash
az login
AZURE_RESOURCE_GROUP=rg-docuengaine-prod \
SQL_SERVER=docuengaine-prod-sql \
SQL_DATABASE=DocuEngAIne \
APP_NAME=docuengaine-prod-app \
./infra/grant-sql-contained-user.sh
```

The script makes the signed-in identity the SQL Entra admin if none exists, then grants `db_datareader` + `db_datawriter`. Set `SQL_GRANT_DDLADMIN=true` only if the app itself will apply EF migrations (the CI migrate job uses SQL admin and does not need this).

## CI/CD

`.github/workflows/azure-deploy.yml`:

1. Builds and lints the React SPA into the API's `wwwroot/` (with the `VITE_ENTRA_*` values above).
2. Builds, tests, and publishes the .NET API, and builds a self-contained EF migrations bundle.
3. Deploys Bicep infrastructure.
4. **Applies migrations** with the bundle, then deploys the published zip to Azure App Service.

`infra`, `migrate`, and `deploy-api` run only when the repository variable `DEPLOY_AZURE` is `true`.

The `migrate` job runs between `infra` and `deploy-api`, and `deploy-api` depends on it — a failed migration blocks the deploy rather than shipping code against a schema that does not exist. `sql.bicep` only allows Azure services, which does not cover GitHub-hosted runners, so the job opens a run-scoped SQL firewall rule for the runner IP and removes it afterwards. The migrate job still uses the SQL-admin connection string (Key Vault or `SQL_ADMIN_*` secrets). The App Service does not: it connects with managed identity after `infra/grant-sql-contained-user.sh` has been run once.

Required GitHub secrets:

- `AZURE_CREDENTIALS`
- `AZURE_SUBSCRIPTION_ID`
- `SQL_ADMIN_LOGIN`
- `SQL_ADMIN_PASSWORD`
- `ENTRA_AUTHORITY` (also used for the SPA build)
- `ENTRA_AUDIENCE`
- `ENTRA_CLIENT_ID`
- `ENTRA_API_SCOPE`

## Database Migrations

Create a new migration:

```bash
dotnet ef migrations add MigrationName --project src/DocuEngAIne.Api --startup-project src/DocuEngAIne.Api --output-dir Data/Migrations
```

Production migrations are applied by the `migrate` job in `.github/workflows/azure-deploy.yml`, using an EF migrations bundle built during CI.

> The EF model snapshot is caught up (`Phase2IntegrationsReconcile`, empty `Up()`). `PendingModelChangesWarning` is no longer suppressed. `Phase2ApiTokens` (`20260830190000`) is additive on top of that snapshot.

## Health Checks

- `GET /api/health/live` — anonymous liveness
- `GET /api/health/ready` — checks SQL readiness

## API Endpoints

### Identity / Tenant

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/me` | Current user profile (auto-provisions on first login) |
| GET | `/api/tenant/me` | Current tenant |
| GET | `/api/tenant/settings` | Tenant settings |
| POST | `/api/tenant/onboard` | Onboard tenant from Entra `tid` (grants the caller Owner; existing tenant → 409) |
| POST | `/api/tenant/claim-owner` | Recover Owner when the tenant has zero active Owner/Admin users. First authenticated caller wins; a second caller (or any tenant that already has an Owner) → 409. |
| GET | `/api/tenant/configuration` | Any tenant user: `features` (key, name, enabled, default), `terminology` (singular / plural and their defaults), `branding` (display name, accent color). A tenant not yet onboarded reads every default. |
| PUT | `/api/tenant/features/{key}` | Admin. `{ enabled }`. Unknown key → 404. |
| PUT | `/api/tenant/terminology` | Admin. `{ terms: { company: { singular, plural } \| null, … } }` — null or both blank restores the default; 1–40 characters each, no control characters; unknown key → 400. |
| PUT | `/api/tenant/branding` | Admin. `{ displayName, accentColor }` replaces both (null clears). `#RRGGBB` with contrast ≥ 4.5:1 against `#0f172a`, else 400. |

### Platform console (operators in `Platform:Operators` only)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/platform/tenants` | Every tenant: status, reason, active users, companies |
| POST | `/api/platform/tenants/{id}/suspend` | `{ reason }` (required, shown to its users). Archived → 409; your own tenant → 409. |
| POST | `/api/platform/tenants/{id}/archive` | `{ reason? }`. Your own tenant → 409. |
| POST | `/api/platform/tenants/{id}/reactivate` | Back to Active; the reason is cleared |

### API tokens (admin)

Per-tenant credentials for the outbound MCP server. Not a browser JWT. Hash stored; plaintext returned once on create.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/tokens` | List tokens (`id`, `name`, `prefix`, timestamps). Never hash or plaintext. `ForTenant`. |
| POST | `/api/tokens` | Create `{ name }`. Returns `{ token }` once. Tenant is the caller's, never a body field. |
| DELETE | `/api/tokens/{id}` | Revoke. Other-tenant id → 404. Idempotent if already revoked. |

**Admin only** (`RequireAdmin`).

### Security groups (admin)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/security-groups` | Groups with member and company counts |
| POST | `/api/security-groups` | `{ name, description?, includeTenantWide? }`. Duplicate name → 409. |
| GET / PUT / DELETE | `/api/security-groups/{id}` | Detail (members, company grants) / rename, describe, toggle tenant-wide / delete (members and grants go with it) |
| PUT / DELETE | `/api/security-groups/{id}/members/{userId}` | Add (idempotent) / remove a member |
| PUT / DELETE | `/api/security-groups/{id}/companies/{companyId}` | `{ level: View \| Edit \| Manage }` grant or change / remove |
| GET | `/api/security-groups/effective/{userId}` | What a stored user can reach |
| GET | `/api/me/company-access` | The caller's own scope (any signed-in user) |

**Admin only** (except `/api/me/company-access`). Every change is audited (category `access`).

### IP access (admin)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/tenant/ip-access` | Policy, entries, the caller's address as the allowlist sees it, and whether it is covered |
| PUT | `/api/tenant/ip-access/policy` | `{ enabled }`. Turning on needs an active entry covering the caller (400 otherwise). |
| POST | `/api/tenant/ip-access/entries` | `{ cidr, label?, isActive? }`. `cidr` is an address or range; host bits are masked (`10.0.0.5/24` → `10.0.0.0/24`). Duplicate → 409. |
| PUT | `/api/tenant/ip-access/entries/{id}` | `{ cidr?, label?, isActive? }`. While on, refused if the caller would no longer be covered. |
| DELETE | `/api/tenant/ip-access/entries/{id}` | While on, refused if the caller would no longer be covered or no active entry would remain. |

**Admin only.** Every change is audited (category `security`).

### Companies

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/companies` | List companies (`q` search) |
| GET | `/api/companies/{id}` | Company detail + related counts/lists (includes `relatedLinks`) |
| GET | `/api/companies/{id}/summary` | Related counts/lists only |
| GET | `/api/companies/{id}/graph` | Relationship graph: `nodes` + `edges` from `ResourceLink` for that company (documents, assets, runbooks, Keeper links). Other-tenant / unknown → 404. `ForTenant`. |
| POST | `/api/companies` | Create company |
| PUT | `/api/companies/{id}` | Update company |
| DELETE | `/api/companies/{id}` | Delete company |

### MCP / Integrations

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/mcp/servers` | List MCP servers (includes `kind` + endpoint) |
| GET | `/api/mcp/servers/{id}` | MCP server detail |
| POST | `/api/mcp/servers` | Register Compact or Composio (`kind`; default URL if endpoint omitted) |
| PUT | `/api/mcp/servers/{id}` | Update MCP server |
| DELETE | `/api/mcp/servers/{id}` | Delete MCP server |
| GET | `/api/integrations` | List connections (includes sync-policy bools) |
| GET | `/api/integrations/{id}` | Connection detail (includes sync-policy bools) |
| POST | `/api/integrations` | Create connection (Halo, NinjaOne, CIPP, Meraki, UniFi, Action1, Autotask, Blackpoint, DefensX, Pax8, Slide, Composio, CustomMcp) plus sync-policy bools |
| PUT | `/api/integrations/{id}` | Update connection and sync-policy bools |
| DELETE | `/api/integrations/{id}` | Delete connection |
| POST | `/api/integrations/{id}/test` | Test MCP/config |
| POST | `/api/integrations/{id}/sync` | Halo, NinjaOne, CIPP, Meraki, UniFi, Action1, Autotask, Blackpoint, DefensX, Pax8, or Slide live pull via Compact (`halo_list_clients` / `ninja_list_organizations` / `cipp_list_tenants` / `meraki_get_organizations` / `unifi_sm_list_hosts` / `action1_list_organizations` / `at_list_companies` / `compassone_list_tenants` / `dfx_list_customers` / `pax8_list_companies` / `slide_list_clients`), or payload upsert. NinjaOne additionally pulls `ninja_list_devices` into Computer Assets unless `SkipAssets`. Other-tenant → 404. IT Glue and Hudu are **not** live providers — use `POST /api/migrations/itglue` and `POST /api/migrations/hudu`. |
| GET | `/api/integrations/{id}/runs` | Recent sync runs |
| GET | `/api/integrations/{id}/mappings` | External→local mappings |

The MCP client speaks Streamable HTTP: it runs the `initialize` handshake, echoes `Mcp-Session-Id` and `MCP-Protocol-Version`, sends `Accept: application/json, text/event-stream`, and unwraps `text/event-stream` replies. A configured `AuthSecretName` that cannot be resolved throws rather than sending an unauthenticated request.

### LLM

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/llm/config` | Current provider + model (no secrets). Authenticated. |
| POST | `/api/llm/chat` | Stateless chat `{ messages, model? }` → `{ content, model, provider }`. Audit-logs that a chat happened; does not store the prompt. Authenticated. |

See [`docs/LLM.md`](docs/LLM.md) for `LLM__Provider`, `LLM__Ollama__BaseUrl`, `TogetherApiKey`, and `AnthropicApiKey`.

**Admin only.** `/api/mcp/servers`, `/api/integrations`, `/api/migrations/itglue`, `/api/migrations/hudu`, and `/api/tokens` require the `RequireAdmin` policy: an Entra `Admin`/`Owner` app role, or a `User` row with `Role >= Admin`. `POST /api/tenant/onboard` grants the onboarding caller `Owner`; every later sign-in provisions `Reader`. Tenants created before that grant recover through `POST /api/tenant/claim-owner` (not admin-gated: the tenant has no Admin to satisfy the policy).

### Outbound MCP (`/mcp`)

DocuEngAIne also *publishes* a read-only Streamable HTTP MCP server so other harnesses can read a tenant's companies, assets, documents, runbooks, expirations, and Keeper links.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/mcp` | Protocol card (tools, auth). Anonymous documentation. |
| POST | `/mcp` | JSON-RPC 2.0 (`initialize`, `tools/list`, `tools/call`, `ping`, `notifications/initialized`). |

Auth is `Authorization: Bearer <api-token>` (or `X-Api-Token`). MCP is **not** a browser JWT. The token hashes to an `ApiToken` row and is mapped onto `ICurrentUser` (`TokenCurrentUser` / `CurrentUserScope`) so every query is `ForTenant`. A missing, revoked, or unknown token is 401.

Read-only tools: `list_companies`, `get_company`, `list_assets`, `get_asset`, `list_documents`, `list_runbooks`, `list_expirations`, `list_keeper_links` (titles and record URLs only). **Keeper reveal is not a tool.** `POST /api/keeper/{id}/reveal` remains the only reveal path and still audit-logs `KeeperLink.Reveal`.

`Accept: application/json, text/event-stream` returns JSON. An event-stream-only Accept wraps the same JSON-RPC result as one `message` SSE event.

MCP kinds: **StackJack Compact** (`https://compact.stackjack.io/mcp` — `/mcp` required) is the only StackJack endpoint (Halo, NinjaOne, CIPP, Meraki, UniFi, Action1, Autotask, Blackpoint, DefensX, Pax8, Slide). **Composio** (`https://connect.composio.dev/mcp`) is the second harness — the 1000+ app Connect MCP, allowlisted to `github`, `cloudflare`, `outlook`, and `notion`. Ads and social toolkits (`googleads`, `facebook`, `instagram`, `linkedin`, `reddit`) are skipped and never invoked. Auth is `McpServer.AuthSecretName` (Key Vault name only).

Sync policy (typed columns, not ConfigJson): `SkipInactive` default true, `SkipContacts` false, `SkipLocations` false, `SkipAssets` false (Ninja skip-devices), `AutoUpdateAssetNames` false, `UpdateCompanyDetails` false (refuse overwrite).

Company matching (`CompanyIdentity` / `CompanyMatchIndex`): before creating a company, sync matches an existing one by provider id (typed Halo/Ninja columns plus `ExternalIdsJson`), then normalized primary domain, then exact normalized name. A key that resolves to two different companies is ambiguous and is **not** matched — a duplicate is recoverable, a wrong merge is not. Legal suffixes are not stripped for the same reason. The mapping records `{"matchedBy":"provider-id|primary-domain|name"}`.

### One-time migrations

IT Glue and Hudu are **not** live company-sync systems of record. There is no `IntegrationProvider` for either and no recurring Compact pull. The endpoints exist only to migrate into DocuEngAIne. Passwords are never stored (Keeper remains the vault). Files live under `Integrations/Migration/` and are named `ItGlue*` and `Hudu*` so the two importers sit beside each other.

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/migrations/itglue` | Admin one-shot import. Body: `{ mcpServerId }` (Compact `itg_list_organizations`) **or** a JSON:API `payload` / raw `{ data: [{ id, type: organizations, attributes: { name } }] }` fixture. Organizations → `Company` via `CompanyIdentity` (`ExternalIdsJson` key `itglue`). Documents / flexible assets → `Document` / `Asset` with secret traits stripped. Idempotent on IT Glue ids. Other-tenant `mcpServerId` → 404. |
| POST | `/api/migrations/hudu` | **One-shot admin mapper.** Body: tenant Compact `mcpServerId` plus Compact-shaped `companies` / `articles` JSON (`hudu_list_companies` / `hudu_list_articles` catalog schema, sanitized fixtures). Companies converge on `ExternalIdsJson` key `hudu`. Articles become Documents in a company folder named from Hudu. Password entities are skipped (Keeper is the vault). Other-tenant or non-Compact server → 404. Not an `IntegrationProvider` and not on SyncAsync. Tests never call Compact Hudu tools. |

### Assets

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/assets/types` | Layouts with their fields, publish state, current version and (for a layout limited to chosen companies) `enabledCompanyIds` |
| POST | `/api/assets/types` | Contributor, full company access. `{ name, description?, icon?, availableToAllCompanies?, fields?: [{ name, type, isRequired?, isExpiration?, section?, helpText?, optionListId? }] }` → a **draft**. Duplicate name → 409. |
| GET / PUT / DELETE | `/api/assets/types/{id}` | Detail (`layout`, enabled `companies`, `assetCount`, publish `problems`) / rename, describe, change availability (a new version while published) / delete — 409 while any asset (Museum included) uses it |
| POST | `/api/assets/types/{id}/publish` · `/unpublish` | Publish validates and records version 1+ (409 `{ error, problems }` when invalid); unpublish stops offering it for new assets, existing assets keep it |
| POST | `/api/assets/types/{id}/fields` | Add a field (a new version while published) |
| PUT / DELETE | `/api/assets/fields/{id}` | Change a field — `name`, `fieldType`, `isRequired`, `isExpiration`, `sortOrder`, `section`, `helpText`, `optionListId` / `optionListClear`; a type or list change converts stored values or is refused with 409 / remove a field — 409 while any asset holds a value for it, or if it is a published layout's last field |
| GET | `/api/assets/types/{id}/versions` · `/versions/{number}` | Version history / one version's schema snapshot |
| PUT / DELETE | `/api/assets/types/{id}/companies/{companyId}` | Enable / disable a layout limited to chosen companies for one company (Manage on it) |
| GET / POST | `/api/option-lists` | Lists with options and which `Layout › Field`s use them / create `{ name, description?, items?: [{ label, value? }] }` (values derived from labels, unique per list) |
| GET / PUT / DELETE | `/api/option-lists/{id}` | Detail / rename, describe, `isActive` (409 while a published layout's choice field needs it) / delete — 409 while any field uses it |
| POST / PUT | `/api/option-lists/{id}/items` · `/items/{itemId}` | Add an option / relabel, reorder, retire or restore one (the value never changes; retiring the last active option a published layout needs → 409) |
| GET | `/api/assets` | List assets (includes `haloAssetUrl` / `ninjaDeviceUrl` when set) |
| GET | `/api/assets/{id}` | Asset detail (URLs + `externalIdsJson`) and `fields`: every layout field with its stored value, plus any value whose field has left the layout (`onLayout: false`) |
| POST | `/api/assets` | Create asset (`expiresAt`, `haloAssetUrl`, `ninjaDeviceUrl`, `externalIdsJson` optional). `fields: { [fieldId]: value }` checked per type; required fields must be given. The layout must be this tenant's, published and available where the asset lives. |
| PUT | `/api/assets/{id}/fields` | `{ values: { [fieldId]: value } }` — sets or clears (null / empty) only the submitted fields. 400 `{ error, fields: { [fieldId]: message } }` when any is invalid. |
| PUT | `/api/assets/{id}` | Update asset (`expiresAt`, portal URLs, `externalIdsJson` optional). `companyId` null = leave unchanged; detach with `companyIdClear: true` or empty GUID. Empty URL clears. Other-tenant company → 400. Changing `assetTypeId` needs a published layout available there and no stored field values (409); moving company needs the layout to be available in the new one. |
| DELETE | `/api/assets/{id}` | Delete asset |

### Expirations

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/expirations` | Tenant-scoped rollup (`companyId`, `showExpired` default false, `q`). Date fields with `FieldDefinition.IsExpiration` plus `Asset.ExpiresAt`. Sort by date asc. Other-tenant `companyId` returns empty (no 500). |

### Flags

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/flags` | List flag definitions (name + hex color) |
| POST | `/api/flags` | Create definition (`name`, `color`, optional `isActive`) |
| PUT | `/api/flags/{id}` | Update definition |
| DELETE | `/api/flags/{id}` | Delete definition (cascades assignments) |
| POST | `/api/flags/{id}/assign` | Assign to `{ entityType, entityId }` (`Company` \| `Asset` \| `Document` \| `Runbook` \| `KeeperLink`). Other-tenant entity → 400. |
| DELETE | `/api/flags/{id}/assign/{entityType}/{entityId}` | Remove assignment |
| GET | `/api/flags/review` | Flagged records for the review queue (`entityType` filter). Joins names via existing tables, `ForTenant`. |

### Related items

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/links?type=&id=` | Links from or to that entity (`Company` \| `Asset` \| `Document` \| `Runbook` \| `KeeperLink`). `ForTenant`. |
| POST | `/api/links` | Create `{ fromType, fromId, toType, toId, label? }`. Both ends must exist `ForTenant` or 400. Unique per tenant pair. |
| DELETE | `/api/links/{id}` | Delete link |

Company GET includes `counts.relatedLinks` plus a short `relatedLinks` list (other-end type + name). `GET /api/companies/{id}/graph` returns the company-centered ResourceLink graph (`nodes`: id/type/name, `edges`: from/to/label). Other-tenant company is 404. Not Hudu tabs.

### Search

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/search?q=` | Tenant-scoped document search via `ISearchService` (title + body). In-memory stub until Azure AI Search is provisioned; config placeholders are `Azure:Search:IndexName`, `Endpoint`, `ApiKeySecretName` (Key Vault secret name, never the key). Empty `q` → empty. Other-tenant hits never leak. |

### Documents

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/documents` | List published documents (`search`, `folderId`). Other-tenant `folderId` → empty. |
| GET | `/api/documents/{id}` | Document detail |
| POST | `/api/documents` | Create document (optional `folderId`; other-tenant folder → 400) |
| PUT | `/api/documents/{id}` | Update document (creates a version; optional `folderId`). `companyId` null = leave unchanged; detach with `companyIdClear: true` or empty GUID. Other-tenant company → 400. |
| DELETE | `/api/documents/{id}` | Delete document |
| GET | `/api/documents/{id}/versions` | List versions |
| GET | `/api/documents/{id}/versions/{versionId}` | Version detail |
| POST | `/api/documents/{id}/restore` | Restore a version |
| POST | `/api/documents/{id}/assist` | Summarize or rewrite via `ILlmClient`. Preview by default; `apply: true` snapshots a `DocumentVersion`. |

### Folders

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/folders` | List folders (`companyId`, `parentId`). `ForTenant`. Other-tenant company → empty. |
| GET | `/api/folders/{id}` | Folder detail |
| POST | `/api/folders` | Create folder (`name`, optional `parentId` / `companyId`) |
| PUT | `/api/folders/{id}` | Update folder. `companyId` null = leave unchanged; detach with `companyIdClear: true` or empty GUID. Other-tenant company → 400. |
| DELETE | `/api/folders/{id}` | Delete folder (reparents children, unfiles articles) |

### Runbooks

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/runbooks` | Search runbooks (includes `runCount`) |
| GET | `/api/runbooks/{id}` | Runbook detail with steps and `runCount` |
| POST | `/api/runbooks` | Create runbook |
| PUT | `/api/runbooks/{id}` | Update runbook and steps. `companyId` null = leave unchanged; detach with `companyIdClear: true` or empty GUID. Other-tenant company → 400. |
| DELETE | `/api/runbooks/{id}` | Delete runbook |
| GET | `/api/runbooks/runs` | Tenant-scoped process completion rollup (`status`, `companyId`). Joins runbook title and company name. Other-tenant / unknown company → empty. |
| GET | `/api/runbooks/{id}/runs` | List runs (`ForTenant`; other-tenant runbook → 404) |
| POST | `/api/runbooks/{id}/runs` | Start a run (optional `companyId`, must `ForTenant`) |
| POST | `/api/runbooks/{id}/runs/{runId}/complete` | Mark a running run completed |
| POST | `/api/runbooks/{id}/runs/{runId}/cancel` | Cancel a running run |
| POST | `/api/runbooks/{id}/runs/{runId}/promote` | Create a Document from a completed run (`ForTenant`; other-tenant → 404). Idempotent: second call returns the existing document id. |

### Keeper Links

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/keeper` | List Keeper links |
| GET | `/api/keeper/{id}` | Keeper link detail |
| POST | `/api/keeper` | Create Keeper link |
| PUT | `/api/keeper/{id}` | Update Keeper link. `companyId` null = leave unchanged; detach with `companyIdClear: true` or empty GUID. Other-tenant company → 400. |
| DELETE | `/api/keeper/{id}` | Delete Keeper link |
| POST | `/api/keeper/{id}/reveal` | Audit-log and return Keeper URL |

### Client portal (read-only)

Company-scoped client view. Companies must have `PortalEnabled`. Every query is `ForTenant`. Other-tenant or portal-disabled companies are 404. **No password vault. Keeper reveal is not a portal path.**

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/portal` | Surface card (`readOnly`, `passwordVault: false`, `keeper.reveal: false`) |
| GET | `/api/portal/companies` | Portal-enabled companies in the caller's tenant |
| GET | `/api/portal/companies/{id}` | Company overview + counts (docs / expirations / Keeper links) |
| GET | `/api/portal/companies/{id}/documents` | Published company documents. Unpublished / other-company / other-tenant → absent or 404 |
| GET | `/api/portal/companies/{id}/documents/{docId}` | One published company document |
| GET | `/api/portal/companies/{id}/expirations` | Company expirations (`showExpired`, `q`). Same rollup as `/api/expirations` |
| GET | `/api/portal/companies/{id}/keeper-links` | Keeper **metadata only** (title, `hasRecordUrl`). No URL, UID, username hint, notes, or reveal audit |

SPA stub: `/portal` and `/portal/:companyId`. Enable a company with `portalEnabled` on create/update.

## Security Notes

- Tenant isolation is enforced at the API/query layer.
- Tenant-wide roles are enforced on the admin surface: `/api/mcp/servers`, `/api/integrations`, `/api/migrations/itglue`, `/api/migrations/hudu`, and `/api/tokens` require Admin/Owner.
- `ResourceRoleAssignment` is enforced on asset, document, runbook and Keeper write routes (`POST`/`PUT`/`DELETE`) via `IResourceAuthorizationService`. A Contributor grant on one resource lets a Reader write that resource; without a grant they get 403. Tenant-wide Admin/Owner write without a grant. Creates still require a tenant-wide Contributor-or-above role.
- **No passwords or secrets are stored in DocuEngAIne.** Keeper is the vault; we only store a title, optional username hint, and a link to the Keeper record. Every HTTP reveal is audit-logged. The outbound MCP surface and the client portal do not expose reveal. The portal returns Keeper titles only.
- Outbound MCP tokens are stored as SHA-256 hashes. The plaintext is shown once at create.
- Production Azure SQL uses **Active Directory Default** (DefaultAzureCredential / App Service managed identity). SQL auth remains the local-dev fallback via connection string / user-secrets. After deploy, run `infra/grant-sql-contained-user.sh` to create the contained database user for the App Service identity.
- Company, folder and flag-definition writes now require Contributor or above like every other record (they previously had no role check), plus the company access the change needs. Links and flag assignments check company access on the record they annotate; they still have no tenant-role check.
- The optional tenant IP allowlist matches the client address ASP.NET Core reports. Behind the App Service front end that is the last `X-Forwarded-For` entry, which is why the app sets `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`; putting another proxy in front (Front Door, a WAF) needs `ForwardLimit` / known proxies configured before the allowlist is trusted.
- HTTPS only, TLS 1.2+, FTPS disabled, health checks exposed.

## Phase 1 Status ✅

- [x] React SPA scaffold embedded in API
- [x] Current-user profile + auto-provisioning
- [x] Tenant onboarding and settings endpoints
- [x] Object-level RBAC model and service
- [x] KeeperLink entity and endpoints (no local secrets)
- [x] Document versioning and restore
- [x] Runbook/SOP entity with ordered steps
- [x] Azure Bicep + GitHub Actions updated for SPA build
- [x] xUnit tests for tenant isolation, RBAC, versioning, Keeper audit

## Next Steps

See the Masri-native plan: [`docs/MASRI-NATIVE-PLAN.md`](docs/MASRI-NATIVE-PLAN.md), and the current working order in [`docs/NEXT-ITEMS.md`](docs/NEXT-ITEMS.md).

### Phase 2A (now) ✅
- [x] Company (client space) distinct from Entra tenant
- [x] MCP server registry + IntegrationConnection (Key Vault secrets)
- [x] First-class MCP kinds: StackJack Compact (`https://compact.stackjack.io/mcp`) and Composio (`https://connect.composio.dev/mcp`)
- [x] HaloPSA + NinjaOne + CIPP + Meraki + UniFi + Action1 + Autotask + Blackpoint + DefensX + Pax8 + Slide company pull via Compact (`halo_list_clients` / `ninja_list_organizations` / `cipp_list_tenants` / `meraki_get_organizations` / `unifi_sm_list_hosts` / `action1_list_organizations` / `at_list_companies` / `compassone_list_tenants` / `dfx_list_customers` / `pax8_list_companies` / `slide_list_clients` → `SyncFromPayload`)
- [x] SPA: Companies + Integrations (Compact vs Composio; Halo/Ninja/CIPP/Meraki/UniFi/Action1/Autotask/Blackpoint/DefensX/Pax8/Slide point at Compact)
- [x] Company overview related lists (assets/docs/runbooks/Keeper)
- [x] GET MCP server and integration by id; SQL cascade fix
- [x] Sync-policy toggles on IntegrationConnection (SkipInactive default on; UpdateCompanyDetails default off)
- [x] Optional `Company.HaloPortalUrl` / `Company.NinjaPortalUrl` (Open in Halo / Open in Ninja)
- [x] Optional `Asset.HaloAssetUrl` / `Asset.NinjaDeviceUrl` / `Asset.ExternalIdsJson` (Open in Halo / Open in Ninja on Assets)
- [x] Cross-provider company convergence (`ExternalIdsJson` for every provider; match on provider id → domain → exact name; ambiguous keys refuse to merge)
- [x] Outbound read-only MCP server (`/mcp`) + per-tenant API tokens (`/api/tokens`, optional `ExpiresInDays`). `list_keeper_links` returns titles and ids only; `reveal_keeper_link` disclosed one URL at a time and writes the same `KeeperLink.Reveal` audit row as the HTTP reveal endpoint.

> Hand-written migrations `20260827214500_Phase2Integrations`, `20260827220000_Phase2IntegrationsCascadeFix` (Tenant FKs on Mapping/SyncRun are Restrict), `20260827223000_Phase2SyncPolicy`, `20260828010000_Phase2Expirations` (`FieldDefinition.IsExpiration`, `Asset.ExpiresAt`), `20260828020000_Phase2Flags` (`FlagDefinitions`, `FlagAssignments`; Tenant FK on assignments is Restrict), `20260828030000_Phase2RunbookRuns` (`RunbookRuns`; Tenant and Company FKs are Restrict; Runbook FK Cascades), `20260828040000_Phase2ResourceLinks` (`ResourceLinks`; unique `(TenantId, FromType, FromId, ToType, ToId)`; Tenant FK Cascades), `20260828043000_Phase2PsaDeepLinks` (`Companies.HaloPortalUrl`, `Companies.NinjaPortalUrl`), `20260828044000_Phase2ParentCompany` (`Companies.ParentCompanyId`, `CompanyType`, `Nickname`, `Fax`, `Country`, `PostalCode`), `20260828045000_Phase2DocumentFolders` (`DocumentFolders`; `Documents.FolderId` Restrict; Parent Restrict; Company Restrict), `20260828050000_Phase2McpServerKind` (`McpServers.Kind`: StackJackCompact=0, Composio=1), `20260828060000_Phase2StackJackPlan` (`IntegrationConnections.StackJackPlan`, `MonthlyCallLimit`, `PlanDetectedAt`, `SyncIntervalMinutesOverride`), empty `20260830181353_Phase2IntegrationsReconcile` (snapshot catch-up), `20260830190000_Phase2ApiTokens` (`ApiTokens`; unique `TokenHash`; Tenant FK Restrict), `20260830210000_Phase2AssetDeepLinks` (`Assets.ExternalIdsJson`, `Assets.HaloAssetUrl`, `Assets.NinjaDeviceUrl`), `20260831093000_SyncBackoffTokenExpiryAuditActor` (`IntegrationConnections.LastAttemptAt` — failed syncs back off for their full interval; `ApiTokens.ExpiresAt`; `AuditLogs.ActorObjectId`), `20260928130000_AuditV2` (`AuditLogs` changes diff / actor name / target label / category / request context; bounded `EntityType`; target activity index), `20260928140000_Museum` (`DeletedAt` on Assets / Documents / Runbooks / KeeperLinks; `ArchiveEntries`; Documents and Runbooks unique slug indexes filtered to live rows), `20260928150000_AccessReviews` (`AccessReviews`, `AccessReviewItems`; items cascade with their review, unique per subject), `20260928160000_TenantIpAllowlist` (`Tenants.IpAllowlistEnabled`, off for existing tenants; `IpAllowlistEntries`, unique `(TenantId, Cidr)`, Tenant FK Restrict), `20260928170000_SecurityGroups` (`SecurityGroups`, `SecurityGroupMembers`, `SecurityGroupCompanyGrants`; members cascade with group and user, grants with group and company; `ArchiveEntries.CompanyId`, backfilled), `20260928180000_AssetLayouts` (`AssetTypes.IsPublished` / `AvailableToAllCompanies` — existing layouts stay published and available everywhere — `PublishedAt`, `CurrentVersion`; `FieldDefinitions.Section` / `HelpText` / `OptionListId` (Restrict); `OptionLists`, `OptionListItems` (unique value per list); `AssetTypeCompanyActivations` (Company and Tenant FKs Restrict — company delete removes them); `AssetTypeVersions`), `20260928190000_TenantConfiguration` (`Tenants.DisplayName`, `AccentColor`, `TerminologyJson`, all null; `TenantFeatureSettings`, unique `(TenantId, Key)`, Tenant FK Restrict — no rows, so every feature keeps its default), and `20260928200000_TenantStatusLifecycle` (`Tenants.Status` / `StatusReason` / `StatusChangedAt` / `StatusChangedByObjectId` replace `IsActive`; a tenant that was inactive becomes Suspended with a reason saying so).


### Later
- [x] Company relationship graph (`GET /api/companies/{id}/graph` nodes+edges from ResourceLink; Companies page Relationships section)
- [x] LLM providers (Ollama default, Together, Anthropic) — `ILlmClient`, `/api/llm/chat`, `/api/llm/config`
- [x] Document assist (`POST /api/documents/{id}/assist` summarize/rewrite via `ILlmClient`; preview by default)
- [x] Azure AI Search scaffolding (`ISearchService`, in-memory stub, `GET /api/search?q=`; live Azure Search + OpenAI RAG later)
- [x] UniFi / Blackpoint as MCP connectors (both pull companies via Compact — see Phase 2A above)
- [x] Expirations rollup (`GET /api/expirations`, `/expirations`)
- [x] Flags (`GET/POST /api/flags`, assign, `/flags` review queue)
- [x] Runbook runs (`POST/GET /api/runbooks/{id}/runs`, complete/cancel/promote, `runCount`)
- [x] Process completion rollup (`GET /api/runbooks/runs?status=&companyId=`, `/runs`)
- [x] Related items (`ResourceLink`, `GET/POST/DELETE /api/links`, company `relatedLinks`, `GET /api/companies/{id}/graph`).
- [x] Document folders (`CRUD /api/folders`, `folderId` on documents, `/documents` folder list). Other-tenant folder attach → 400.
- [x] Client portal skeleton (`GET /api/portal`, `/portal`) — documents, expirations, Keeper metadata; no reveal; `ForTenant`; `PortalEnabled`
- [x] Switch SQL auth to managed identity (production AD Default; local SQL auth / user-secrets unchanged; contained user via `infra/grant-sql-contained-user.sh`)
- [x] One-time IT Glue migrate-only import (`POST /api/migrations/itglue`; Compact or JSON:API fixture; passwords never stored)
- [x] One-time Hudu mapper (`POST /api/migrations/hudu`, Compact-shaped `hudu_list_companies` / `hudu_list_articles` JSON from catalog schema and sanitized fixtures; passwords skipped — Keeper only; tests never call Compact)
