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
- **Company** — client space (distinct from Entra tenant). Optional Halo/Ninja IDs and portal URLs (Open in Halo / Open in Ninja). URLs only; no secrets. Every provider's external id is also recorded in `ExternalIdsJson`, which is how sync converges Halo/Ninja/CIPP/Meraki/UniFi/Action1 onto one company instead of one per connection.
- **McpServer / IntegrationConnection / IntegrationMapping / SyncRun** — MCP registry (StackJack Compact or Composio) and PSA/RMM sync. Secrets live in Key Vault names only.
- **User** — mapped to Entra object ID, email, and tenant-wide role.
- **Asset / AssetType / FieldDefinition / CustomFieldValue** — flexible assets with custom fields.
- **Document** — KB articles with full-text search and **versioning**. Optional `FolderId`.
- **DocumentFolder** — nested KB folders (`ParentId`). Optional `CompanyId` (null = central KB; set = company KB). Tenant-scoped.
- **Runbook / RunbookStep / RunbookRun** — ordered SOPs and checklists with start/complete/cancel run history. Tenant-wide books are templates; optional `CompanyId` is the per-client instance. Not a second process product. No local secrets.
- **KeeperLink** — links to credentials in **Keeper**; no secrets are stored locally.
- **ResourceRoleAssignment** — object-level RBAC overriding tenant-wide roles.
- **AuditLog** — action trail (tenant-scoped where applicable).
- **FlagDefinition / FlagAssignment** — named color labels on companies, assets, documents, runbooks, and Keeper links. Drive the review queue. No local secrets.
- **ResourceLink** — directed related-item links between Company, Asset, Document, Runbook, and KeeperLink. Optional label. Not a graph visualization. `AssetDocumentLink` remains the asset↔document convenience. No local secrets.

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

What gets provisioned:

- App Service Plan (Linux, B2)
- App Service with system-assigned managed identity
- Azure SQL server + database
- Azure Key Vault with RBAC and SQL connection string secret
- Role assignment granting the app **Key Vault Secrets User**

The app reads `ConnectionStrings:DocuEngAIne` from Key Vault via a Key Vault reference in App Service settings.

## CI/CD

`.github/workflows/azure-deploy.yml`:

1. Builds and lints the React SPA into the API's `wwwroot/` (with the `VITE_ENTRA_*` values above).
2. Builds, tests, and publishes the .NET API, and builds a self-contained EF migrations bundle.
3. Deploys Bicep infrastructure.
4. **Applies migrations** with the bundle, then deploys the published zip to Azure App Service.

`infra`, `migrate`, and `deploy-api` run only when the repository variable `DEPLOY_AZURE` is `true`.

The `migrate` job runs between `infra` and `deploy-api`, and `deploy-api` depends on it — a failed migration blocks the deploy rather than shipping code against a schema that does not exist. `sql.bicep` only allows Azure services, which does not cover GitHub-hosted runners, so the job opens a run-scoped SQL firewall rule for the runner IP and removes it afterwards.

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

> The EF model snapshot still lags the hand-written Phase 2 migrations, and `DependencyInjection` suppresses `PendingModelChangesWarning` to compensate. Run `./scripts/reconcile-model-snapshot.sh` (needs the .NET 10 SDK) before adding any new migration — it regenerates the snapshot and fails unless the result is a pure no-op. See [`docs/NEXT-ITEMS.md`](docs/NEXT-ITEMS.md).

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
| POST | `/api/tenant/onboard` | Onboard tenant from Entra `tid` |

### Companies

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/companies` | List companies (`q` search) |
| GET | `/api/companies/{id}` | Company detail + related counts/lists (includes `relatedLinks`) |
| GET | `/api/companies/{id}/summary` | Related counts/lists only |
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
| POST | `/api/integrations` | Create connection (Halo, NinjaOne, CIPP, Meraki, UniFi, Action1, Blackpoint, Composio, CustomMcp) plus sync-policy bools |
| PUT | `/api/integrations/{id}` | Update connection and sync-policy bools |
| DELETE | `/api/integrations/{id}` | Delete connection |
| POST | `/api/integrations/{id}/test` | Test MCP/config |
| POST | `/api/integrations/{id}/sync` | Halo, NinjaOne, CIPP, Meraki, UniFi, or Action1 live pull via Compact (`halo_list_clients` / `ninja_list_organizations` / `cipp_list_tenants` / `meraki_get_organizations` / `unifi_sm_list_hosts` / `action1_list_organizations`), or payload upsert. NinjaOne additionally pulls `ninja_list_devices` into Computer Assets unless `SkipAssets`. Other-tenant → 404 |
| GET | `/api/integrations/{id}/runs` | Recent sync runs |
| GET | `/api/integrations/{id}/mappings` | External→local mappings |

The MCP client speaks Streamable HTTP: it runs the `initialize` handshake, echoes `Mcp-Session-Id` and `MCP-Protocol-Version`, sends `Accept: application/json, text/event-stream`, and unwraps `text/event-stream` replies. A configured `AuthSecretName` that cannot be resolved throws rather than sending an unauthenticated request.

**Admin only.** `/api/mcp/servers` and `/api/integrations` require the `RequireAdmin` policy: an Entra `Admin`/`Owner` app role, or a `User` row with `Role >= Admin`. `POST /api/tenant/onboard` grants the onboarding caller `Owner`; every later sign-in provisions `Reader`.

MCP kinds: **StackJack Compact** (`https://compact.stackjack.io/mcp` — `/mcp` required) is the only StackJack endpoint (Halo, NinjaOne, CIPP, Meraki, UniFi, Action1). **Composio** (`https://connect.composio.dev/mcp`) is the 1000+ app Connect MCP. Auth is `McpServer.AuthSecretName` (Key Vault name only).

Sync policy (typed columns, not ConfigJson): `SkipInactive` default true, `SkipContacts` false, `SkipLocations` false, `SkipAssets` false (Ninja skip-devices), `AutoUpdateAssetNames` false, `UpdateCompanyDetails` false (refuse overwrite).

Company matching (`CompanyIdentity` / `CompanyMatchIndex`): before creating a company, sync matches an existing one by provider id (typed Halo/Ninja columns plus `ExternalIdsJson`), then normalized primary domain, then exact normalized name. A key that resolves to two different companies is ambiguous and is **not** matched — a duplicate is recoverable, a wrong merge is not. Legal suffixes are not stripped for the same reason. The mapping records `{"matchedBy":"provider-id|primary-domain|name"}`.

### Assets

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/assets/types` | List asset types (fields include `isExpiration`) |
| POST | `/api/assets/types` | Create asset type (fields accept `isExpiration`) |
| PUT | `/api/assets/fields/{id}` | Update field definition (`isExpiration`, type, name) |
| GET | `/api/assets` | List assets |
| GET | `/api/assets/{id}` | Asset detail |
| POST | `/api/assets` | Create asset (`expiresAt` optional) |
| PUT | `/api/assets/{id}` | Update asset (`expiresAt` optional) |
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

Company GET includes `counts.relatedLinks` plus a short `relatedLinks` list (other-end type + name). Not Hudu tabs; not a graph viz.

### Documents

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/documents` | List published documents (`search`, `folderId`). Other-tenant `folderId` → empty. |
| GET | `/api/documents/{id}` | Document detail |
| POST | `/api/documents` | Create document (optional `folderId`; other-tenant folder → 400) |
| PUT | `/api/documents/{id}` | Update document (creates a version; optional `folderId`) |
| DELETE | `/api/documents/{id}` | Delete document |
| GET | `/api/documents/{id}/versions` | List versions |
| GET | `/api/documents/{id}/versions/{versionId}` | Version detail |
| POST | `/api/documents/{id}/restore` | Restore a version |

### Folders

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/folders` | List folders (`companyId`, `parentId`). `ForTenant`. Other-tenant company → empty. |
| GET | `/api/folders/{id}` | Folder detail |
| POST | `/api/folders` | Create folder (`name`, optional `parentId` / `companyId`) |
| PUT | `/api/folders/{id}` | Update folder |
| DELETE | `/api/folders/{id}` | Delete folder (reparents children, unfiles articles) |

### Runbooks

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/runbooks` | Search runbooks (includes `runCount`) |
| GET | `/api/runbooks/{id}` | Runbook detail with steps and `runCount` |
| POST | `/api/runbooks` | Create runbook |
| PUT | `/api/runbooks/{id}` | Update runbook and steps |
| DELETE | `/api/runbooks/{id}` | Delete runbook |
| GET | `/api/runbooks/runs` | Tenant-scoped process completion rollup (`status`, `companyId`). Joins runbook title and company name. Other-tenant / unknown company → empty. |
| GET | `/api/runbooks/{id}/runs` | List runs (`ForTenant`; other-tenant runbook → 404) |
| POST | `/api/runbooks/{id}/runs` | Start a run (optional `companyId`, must `ForTenant`) |
| POST | `/api/runbooks/{id}/runs/{runId}/complete` | Mark a running run completed |
| POST | `/api/runbooks/{id}/runs/{runId}/cancel` | Cancel a running run |

### Keeper Links

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/keeper` | List Keeper links |
| GET | `/api/keeper/{id}` | Keeper link detail |
| POST | `/api/keeper` | Create Keeper link |
| PUT | `/api/keeper/{id}` | Update Keeper link |
| DELETE | `/api/keeper/{id}` | Delete Keeper link |
| POST | `/api/keeper/{id}/reveal` | Audit-log and return Keeper URL |

## Security Notes

- Tenant isolation is enforced at the API/query layer.
- Tenant-wide roles are enforced on the admin surface: `/api/mcp/servers` and `/api/integrations` require Admin/Owner.
- `ResourceRoleAssignment` **models** object-level RBAC per asset/document/runbook/Keeper link, but no endpoint consults it yet, so it is not an enforced control today. See `docs/NEXT-ITEMS.md` §5.
- **No passwords or secrets are stored in DocuEngAIne.** Keeper is the vault; we only store a title, optional username hint, and a link to the Keeper record. Every reveal is audit-logged.
- SQL auth is used in the skeleton for portability. Plan to switch to **Active Directory Managed Identity** for production and create the contained database user for the App Service identity.
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
- [x] HaloPSA + NinjaOne + CIPP + Meraki + UniFi + Action1 company pull via Compact (`halo_list_clients` / `ninja_list_organizations` / `cipp_list_tenants` / `meraki_get_organizations` / `unifi_sm_list_hosts` / `action1_list_organizations` → `SyncFromPayload`)
- [x] SPA: Companies + Integrations (Compact vs Composio; Halo/Ninja/CIPP/Meraki/UniFi/Action1 point at Compact)
- [x] Company overview related lists (assets/docs/runbooks/Keeper)
- [x] GET MCP server and integration by id; SQL cascade fix
- [x] Sync-policy toggles on IntegrationConnection (SkipInactive default on; UpdateCompanyDetails default off)
- [x] Optional `Company.HaloPortalUrl` / `Company.NinjaPortalUrl` (Open in Halo / Open in Ninja)
- [x] Cross-provider company convergence (`ExternalIdsJson` for every provider; match on provider id → domain → exact name; ambiguous keys refuse to merge)

> Hand-written migrations `20260827214500_Phase2Integrations`, `20260827220000_Phase2IntegrationsCascadeFix` (Tenant FKs on Mapping/SyncRun are Restrict), `20260827223000_Phase2SyncPolicy`, `20260828010000_Phase2Expirations` (`FieldDefinition.IsExpiration`, `Asset.ExpiresAt`), `20260828020000_Phase2Flags` (`FlagDefinitions`, `FlagAssignments`; Tenant FK on assignments is Restrict), `20260828030000_Phase2RunbookRuns` (`RunbookRuns`; Tenant and Company FKs are Restrict; Runbook FK Cascades), `20260828040000_Phase2ResourceLinks` (`ResourceLinks`; unique `(TenantId, FromType, FromId, ToType, ToId)`; Tenant FK Cascades), `20260828043000_Phase2PsaDeepLinks` (`Companies.HaloPortalUrl`, `Companies.NinjaPortalUrl`), `20260828044000_Phase2ParentCompany` (`Companies.ParentCompanyId`, `CompanyType`, `Nickname`, `Fax`, `Country`, `PostalCode`), `20260828045000_Phase2DocumentFolders` (`DocumentFolders`; `Documents.FolderId` Restrict; Parent Restrict; Company Restrict), `20260828050000_Phase2McpServerKind` (`McpServers.Kind`: StackJackCompact=0, Composio=1), and `20260828060000_Phase2StackJackPlan` (`IntegrationConnections.StackJackPlan`, `MonthlyCallLimit`, `PlanDetectedAt`, `SyncIntervalMinutesOverride`). Run `dotnet ef migrations add Phase2IntegrationsReconcile --project src/DocuEngAIne.Api` if the model snapshot still needs regen.


### Later
- [ ] Asset relationship graph
- [ ] Azure AI Search + Azure OpenAI RAG
- [ ] UniFi / Blackpoint as MCP connectors
- [x] Expirations rollup (`GET /api/expirations`, `/expirations`)
- [x] Flags (`GET/POST /api/flags`, assign, `/flags` review queue)
- [x] Runbook runs (`POST/GET /api/runbooks/{id}/runs`, complete/cancel, `runCount`)
- [x] Process completion rollup (`GET /api/runbooks/runs?status=&companyId=`, `/runs`)
- [x] Related items (`ResourceLink`, `GET/POST/DELETE /api/links`, company `relatedLinks`). Graph visualization later.
- [x] Document folders (`CRUD /api/folders`, `folderId` on documents, `/documents` folder list). Other-tenant folder attach → 400.
- [ ] Client portal
- [ ] Switch SQL auth to managed identity
- [ ] One-time Hudu export migration (passwords → Keeper only)
