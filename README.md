# DocuEngAIne

An MSP documentation platform — a modern, secure, AI-ready alternative to IT Glue / Hudu.

Built for Microsoft-centric environments: **Azure App Service**, **Azure SQL**, **Entra ID** authentication, and **Azure Key Vault** secrets. No containers, no Docker.

## Stack

- **.NET 10 / ASP.NET Core** Web API
- **Azure App Service (Linux, code deploy)** — no Docker per Masri infra preference
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
  DocuEngAIne.Api           # Minimal APIs, health checks, Swagger
infra/                      # Bicep modules
tests/                      # xUnit + EF InMemory tests
```

## Domain Model

- **Tenant** — isolation boundary; seeded from the Entra `tid` claim on first login.
- **User** — mapped to Entra object ID, email, and role.
- **Asset / AssetType / FieldDefinition / CustomFieldValue** — flexible assets with custom fields.
- **Document** — KB articles with full-text search.
- **EncryptedSecret** — credentials encrypted with ASP.NET Data Protection.
- **AuditLog** — action trail (tenant-scoped where applicable).

All tenant-scoped queries use `ForTenant(currentUser)`; `SaveChangesAsync` stamps `TenantId` and audit timestamps automatically.

## Local Development

1. Install .NET 10 SDK and the local EF tool:
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

4. Run:
   ```bash
   dotnet run --project src/DocuEngAIne.Api
   ```

Swagger UI is available at `/swagger` in Development.

## Entra ID Setup

1. Register an app in **Microsoft Entra admin center**.
2. Add a platform: **Web** with redirect `https://{app-host}/signin-oidc` (or use a SPA/native client for token acquisition).
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

1. Builds, tests, and publishes the API.
2. Deploys Bicep infrastructure.
3. Deploys the published zip to Azure App Service.

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

## API Endpoints (v0 skeleton)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/tenant/me` | Current tenant |
| POST | `/api/tenant/onboard` | Onboard tenant from Entra `tid` |
| GET | `/api/assets/types` | List asset types |
| POST | `/api/assets/types` | Create asset type |
| GET | `/api/assets` | List assets |
| GET | `/api/assets/{id}` | Asset detail |
| POST | `/api/assets` | Create asset |
| PUT | `/api/assets/{id}` | Update asset |
| DELETE | `/api/assets/{id}` | Delete asset |
| GET | `/api/documents` | Search documents |
| GET | `/api/documents/{id}` | Document detail |
| POST | `/api/documents` | Create document |
| PUT | `/api/documents/{id}` | Update document |
| DELETE | `/api/documents/{id}` | Delete document |

## Security Notes

- Tenant isolation is enforced at the API/query layer, not by EF global filters (dynamic per-request filters have model-caching gotchas).
- Credentials are encrypted at rest with ASP.NET Data Protection; in production back the key ring by Key Vault or Blob Storage.
- SQL auth is used in the skeleton for portability. Plan to switch to **Active Directory Managed Identity** for production and create the contained database user for the App Service identity.
- HTTPS only, TLS 1.2+, FTPS disabled, health checks exposed.

## Next Steps

- [ ] Switch SQL auth to managed identity
- [ ] Add password/secret endpoints with reveal-audit logging
- [ ] Add AI assistant integration (Azure OpenAI) for KB search and asset Q&A
- [ ] Add rich relationship graph (assets ↔ documents ↔ secrets)
- [ ] Add integration connectors (Microsoft 365, Intune, Entra, HaloPSA)
