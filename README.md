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
- **Company** — client space (distinct from Entra tenant). Optional Halo/Ninja IDs.
- **McpServer / IntegrationConnection / IntegrationMapping / SyncRun** — MCP registry and PSA/RMM sync. Secrets live in Key Vault names only.
- **User** — mapped to Entra object ID, email, and tenant-wide role.
- **Asset / AssetType / FieldDefinition / CustomFieldValue** — flexible assets with custom fields.
- **Document** — KB articles with full-text search and **versioning**.
- **Runbook / RunbookStep** — ordered SOPs and checklists.
- **KeeperLink** — links to credentials in **Keeper**; no secrets are stored locally.
- **ResourceRoleAssignment** — object-level RBAC overriding tenant-wide roles.
- **AuditLog** — action trail (tenant-scoped where applicable).

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

1. Builds the React SPA into the API's `wwwroot/`.
2. Builds, tests, and publishes the .NET API.
3. Deploys Bicep infrastructure.
4. Deploys the published zip to Azure App Service.

Required GitHub secrets:

- `AZURE_CREDENTIALS`
- `AZURE_SUBSCRIPTION_ID`
- `SQL_ADMIN_LOGIN`
- `SQL_ADMIN_PASSWORD`
- `ENTRA_AUTHORITY`
- `ENTRA_AUDIENCE`

## Database Migrations

Create a new migration:

```bash
dotnet ef migrations add MigrationName --project src/DocuEngAIne.Api --startup-project src/DocuEngAIne.Api --output-dir Data/Migrations
```

Apply in production via a CI step or from an Azure Pipelines/SQL deployment task after the infra job completes.

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
| GET | `/api/companies/{id}` | Company detail + related counts/lists |
| GET | `/api/companies/{id}/summary` | Related counts/lists only |
| POST | `/api/companies` | Create company |
| PUT | `/api/companies/{id}` | Update company |
| DELETE | `/api/companies/{id}` | Delete company |

### MCP / Integrations

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/mcp/servers` | List MCP servers |
| GET | `/api/mcp/servers/{id}` | MCP server detail |
| POST | `/api/mcp/servers` | Register MCP server |
| PUT | `/api/mcp/servers/{id}` | Update MCP server |
| DELETE | `/api/mcp/servers/{id}` | Delete MCP server |
| GET | `/api/integrations` | List integration connections |
| GET | `/api/integrations/{id}` | Integration connection detail |
| POST | `/api/integrations` | Create connection (Halo, NinjaOne, UniFi, Blackpoint, CustomMcp) |
| PUT | `/api/integrations/{id}` | Update connection |
| DELETE | `/api/integrations/{id}` | Delete connection |
| POST | `/api/integrations/{id}/test` | Test MCP/config |
| POST | `/api/integrations/{id}/sync` | Sync (payload upsert or gated live pull) |
| GET | `/api/integrations/{id}/runs` | Recent sync runs |
| GET | `/api/integrations/{id}/mappings` | External→local mappings |

### Assets

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/assets/types` | List asset types |
| POST | `/api/assets/types` | Create asset type |
| GET | `/api/assets` | List assets |
| GET | `/api/assets/{id}` | Asset detail |
| POST | `/api/assets` | Create asset |
| PUT | `/api/assets/{id}` | Update asset |
| DELETE | `/api/assets/{id}` | Delete asset |

### Documents

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/documents` | Search documents |
| GET | `/api/documents/{id}` | Document detail |
| POST | `/api/documents` | Create document |
| PUT | `/api/documents/{id}` | Update document (creates a version) |
| DELETE | `/api/documents/{id}` | Delete document |
| GET | `/api/documents/{id}/versions` | List versions |
| GET | `/api/documents/{id}/versions/{versionId}` | Version detail |
| POST | `/api/documents/{id}/restore` | Restore a version |

### Runbooks

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/runbooks` | Search runbooks |
| GET | `/api/runbooks/{id}` | Runbook detail with steps |
| POST | `/api/runbooks` | Create runbook |
| PUT | `/api/runbooks/{id}` | Update runbook and steps |
| DELETE | `/api/runbooks/{id}` | Delete runbook |

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
- Object-level RBAC via `ResourceRoleAssignment` can override a user's tenant-wide role per asset/document/runbook/Keeper link.
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

See the Masri-native plan: [`docs/MASRI-NATIVE-PLAN.md`](docs/MASRI-NATIVE-PLAN.md).

### Phase 2A (now) ✅
- [x] Company (client space) distinct from Entra tenant
- [x] MCP server registry + IntegrationConnection (Key Vault secrets)
- [x] HaloPSA + NinjaOne sync via payload/MCP path (`SyncFromPayload` + test/sync endpoints)
- [x] SPA: Companies + Integrations
- [x] Company overview related lists (assets/docs/runbooks/Keeper)
- [x] GET MCP server and integration by id; SQL cascade fix

> Hand-written migrations `20260827214500_Phase2Integrations` and `20260827220000_Phase2IntegrationsCascadeFix` (Tenant FKs on Mapping/SyncRun are Restrict). Run `dotnet ef migrations add Phase2IntegrationsReconcile --project src/DocuEngAIne.Api` if the model snapshot still needs regen.

### Later
- [ ] Asset relationship graph
- [ ] Azure AI Search + Azure OpenAI RAG
- [ ] UniFi / Blackpoint as MCP connectors
- [ ] Expirations + flags
- [ ] Client portal
- [ ] Switch SQL auth to managed identity
- [ ] One-time Hudu export migration (passwords → Keeper only)
