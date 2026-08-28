# DocuEngAIne — Masri-Native Build Plan

Hudu is a **capability baseline**, not a product to clone. DocuEngAIne is Masri's documentation system: Entra-auth'd, Azure-hosted, Keeper as the vault, HaloPSA + NinjaOne as system of record for clients/devices, and **MCP-pluggable** integrations (StackJack, Composio) instead of a fixed vendor catalog UI.

Observed Masri Hudu instance (`masri.huducloud.com`) on 2026-08-27. Configured integrations: **0**. Available catalog: **32**.

---

## 1. Capability baseline (from Hudu)

### Top-level surfaces
| Capability | Hudu surface | Job for MSP tech |
|---|---|---|
| Personal workspace | Dashboard | Favorites, recents, tasks, expirations, flags, activity, stats |
| Client spaces | Companies | Client as isolation/context boundary |
| Cross-client views | Global | Passwords, assets, expirations, flags across clients |
| Shared knowledge | Central KB | MSP-wide articles (not client-scoped) |
| Personal secrets | My Vault | Per-user password vault (we will **not** replicate — Keeper) |
| Platform config | Admin | Users, groups, layouts, integrations, import/export, API, audit |

### Company (client) space — ExampleCo
**Core:** Home/overview, Passwords, Processes, KB, Photos, IPAM, Racks, Websites, Expirations, External Portal, Related Items, Museum.

**Asset layouts (typed records):** Active Directory / Identity, API Secrets, Applications, Backup, Cellular Backup, Cloud Accounts, Computer Assets, Contracts & SLAs, Databases, Door Access, Email, File Sharing, Firewalls, Insurance, ISP/WAN, LAN, Licenses, Locations, Mobile Devices, NAS, People, Phone/VoIP, Printing, Remote Access, RFC, Security, Cameras, Special Role Devices, SSL Certs, Switches, UPS/PDU, Vendors, VMs, VoIP Phones, Wireless.

### Admin
- **Basic:** General, Security, Design, Portal, Users, Groups, Labels, Lists, Alerts, Flags, Workflows
- **Core:** Password Folders, Process Templates, IPAM settings, Racks, Asset Layouts
- **Apps:** Integrations, External Apps, Hudu Bridge, Hudini AI
- **Account:** Import, Export, API Keys, Activity Logs, License, Email/SMTP

### Integrations that matter to Masri
| Integration | Hudu pull | Masri approach |
|---|---|---|
| **Halo PSA** | Companies, contracts, assets, sites, users; optional KB push to Halo FAQ | **Primary PSA sync** via StackJack MCP / Halo API. Match companies. No local secret storage. |
| **NinjaOne** | Organizations + devices; layout mapping | **Primary RMM sync** via StackJack MCP / Ninja API. Devices → Computer Assets (or mapped layouts). |
| **UniFi** | Sites, alarms, devices, networks, port forwards | Prefer StackJack/Composio or UniFi API connector as MCP tool, not a Hudu-shaped form. |
| **Microsoft 365 / Intune** | Contacts, mailboxes, licenses, devices | Align with existing Graph/Intune work; MCP or Graph-backed sync. |
| **Blackpoint Cyber** | Not in Hudu catalog | First-class Masri connector (MDR context on assets/companies) — differentiation. |
| Cloudflare, Meraki, etc. | Present in catalog | Add **only** when ops needs them, as MCP servers — not a 32-tile clone. |

Halo setup fields observed: Resource Server, Tenant, Client ID/Secret, default layouts for users/sites/assets, skip flags, name auto-update, KB sync to Halo FAQ.

Ninja setup fields observed: Client ID/Secret, regional endpoints, SSO URL, skip devices, layout mapping, name auto-update.

---

## 2. What Phase 1 already covers

| Need | DocuEngAIne today |
|---|---|
| Tenant isolation | `Tenant` + `ForTenant` + Entra `tid` |
| Users / roles | `User` + `UserRole` + object RBAC |
| Typed assets + custom fields | `AssetType`, `FieldDefinition`, `Asset`, `CustomFieldValue` |
| KB + versioning | `Document` / `DocumentVersion` |
| SOPs / checklists | `Runbook` / `RunbookStep` |
| Credentials pattern | **KeeperLink only** (no local secrets) — intentional |
| Audit | `AuditLog` + reveal logging |
| Azure deploy | Bicep + GitHub Actions + App Service + SQL + Key Vault |
| SPA shell | React pages: Dashboard, Assets, Documents, Runbooks, Keeper |

Gaps vs Hudu baseline: company/client entity distinct from Entra tenant, relationship graph, IPAM/racks/websites/expirations, portal, import/export, **integrations + MCP**, search/RAG, flags/workflows/alerts, photos.

---

## 3. Product principles (non-negotiable)

1. **Not a Hudu skin.** Same jobs, Masri UX and data model.
2. **Keeper is the vault.** Never store passwords/TOTP in DocuEngAIne.
3. **PSA/RMM are systems of record** for clients and devices; DocuEngAIne stores documentation context and links.
4. **MCP-first integrations.** Register StackJack MCP, Composio MCP, and future servers. Prefer tool invocation over hardcoding every vendor.
5. **Strict multi-tenant siloing** at query layer (already enforced).
6. **Entra ID** for identity; Azure SQL + Key Vault for data/secrets.
7. **Ship vertical slices** that techs use in Halo/Ninja workflows first.

---

## 4. Domain additions (Phase 2+)

### 4.1 Company (client space)
Hudu "Company" ≠ Entra tenant. Masri MSP has **one Entra tenant**, many clients.

Add:
- `Company` (tenant-scoped): Name, Slug, HaloClientId?, NinjaOrgId?, ExternalIds JSON, Address, Notes, IsActive, PortalEnabled
- Map assets/documents/runbooks/KeeperLinks → optional `CompanyId`
- Company overview API + SPA page (ops notes, key links, emergency contacts as structured fields or documents)

### 4.2 Integrations + MCP (priority slice)
Entities:
- `McpServer` — name, baseUrl/transport, auth ref (Key Vault secret name), enabled, tenant-scoped
- `IntegrationConnection` — provider (`halo`|`ninjaone`|`unifi`|`blackpoint`|`mcp`|…), status, config JSON (non-secret), secret ref, lastSyncAt, lastError
- `IntegrationMapping` — externalId ↔ Company/Asset/Document
- `SyncRun` — job log (started, finished, counts, errors)

Services:
- `IMcpClient` — list tools, call tool (StackJack `stackjack_run_readonly_tool` / Composio execute)
- `IHaloSyncService` / `INinjaSyncService` — orchestrate via MCP or direct API behind the same interface
- Webhook/inbound later for near-real-time

API (sketch):
- `GET/POST /api/mcp/servers`
- `POST /api/mcp/servers/{id}/tools/{toolName}` (admin, audited)
- `GET/POST /api/integrations`
- `POST /api/integrations/{id}/test`
- `POST /api/integrations/{id}/sync`
- `GET /api/integrations/{id}/mappings`

SPA: **Integrations** page — add MCP server, connect Halo/Ninja (secrets to Key Vault), sync status, mapping UI.

### 4.3 Relationships
- `ResourceLink` (fromType, fromId, toType, toId, label) — replace Hudu "Related Items" without copying UX
- Graph query endpoint for asset ↔ doc ↔ runbook ↔ Keeper ↔ company

### 4.4 Operational metadata (selective)
Ship when needed, not as a Hudu checklist:
- **Expirations** on assets/docs (warranty, SSL, contract end) + dashboard widget
- **Flags / review states** on companies, assets, documents, runbooks, Keeper links (this slice: definitions + assignments + review queue)
- **Websites / SSL** as asset layout + expiration, not a separate product line day one
- **IPAM / Racks** — Phase 3 unless a tenant demands it; UniFi MCP can own live network truth
- **Photos** — blob storage linked to company/asset (Phase 3)
- **Client portal** — Entra external ID or magic-link read-only (Phase 3; already in README next steps)

### 4.5 Search / AI
Keep README direction: Azure AI Search + OpenAI RAG over documents, assets, runbooks. Prefer this over Typesense clone. Hudini-equivalent = Masri AI over **our** index + MCP tools (ask StackJack for live Halo ticket context).

---

## 5. Phased delivery

### Phase 2A — Integrations foundation (NOW)
1. `Company` entity + CRUD + attach existing resources
2. `McpServer` + `IntegrationConnection` + Key Vault secret refs
3. StackJack MCP registration path (URL + auth)
4. Halo sync v1: companies (+ optional sites/users) → `Company` / People layout
5. Ninja sync v1: orgs + devices → `Company` + Computer Assets
6. SPA Integrations + Companies pages
7. Tests: tenant isolation on new entities; sync mapping uniqueness
8. README Phase 2 status

### Phase 2B — Stack depth
1. Bidirectional or push: selected KB → Halo FAQ (only if ops wants it)
2. UniFi + Blackpoint as MCP connectors
3. Composio MCP as second harness
4. Sync schedules + SyncRun UI
5. Asset external IDs + "open in Halo/Ninja" deep links (company portal URLs shipped)

### Phase 2C — Doc quality & ops
1. Relationships graph
2. Flags (this slice) after expirations
3. Azure AI Search
4. Import from Hudu export (one-time migration tooling)
5. Client portal

### Explicitly defer / skip
- In-app password vault / TOTP (Keeper)
- Hudu Bridge, gamification, leaderboard, Museum novelty
- Cloning all 32 integration tiles
- Pixel-matching Hudu nav or Magic Dash widgets

---

## 6. Migration from Hudu (when ready)

1. Export Hudu (admin export)
2. Map Companies → `Company`
3. Map Asset Layouts → `AssetType` + field defs
4. Map Articles → `Document` (+ versions if present)
5. Map Processes → `Runbook` / steps
6. Map Passwords → **Keeper import + KeeperLink records only**
7. Re-link Halo/Ninja IDs via fresh sync match, not CSV guesswork where possible

---

## 7. Success criteria

- Tech opens a **Company**, sees Halo + Ninja identity, devices, docs, runbooks, Keeper links — without a second password vault.
- Admin adds **StackJack MCP** and runs Halo/Ninja sync without a code change per vendor.
- No secrets in SQL; Key Vault + Keeper only.
- Multi-tenant tests green for all new entities.
- Clearer Halo↔docs loop than Hudu (MCP tools callable from agents / future Assist).

---


## 7b. Gap-fill notes (Hudu crawl 2026-08-27)

- **Global**: cross-company Passwords, Networks, Racks, Websites, Expirations; Flag Review; Process Completion; Documentation Quality. Skip Leaderboard/Gold Standards as non-core.
- **Central KB**: account-wide articles with folders; Public/share flag. Distinct from company KB (`/kba?company_id=`).
- **Expirations**: aggregate across API keys, contracts, licenses, SaaS renewals, UPS battery, SSL, domains — Phase 2C widget.
- **Processes**: template → per-company instance with run tracking (`RunbookRun` on existing Runbook). Global Process Completion is `GET /api/runbooks/runs` (this slice).
- **UniFi MCP**: sites, alarms, devices, network configs, port forwards.
- **Security**: 2FA, SAML/SSO, IP allow-list, idle timeout — Entra covers auth; optional IP allow-list later.
- **Portal**: white-label client portal (KB, limited reveal via Keeper links only, websites, files). Phase 2C.
- Hudu company KB deep links `/c/{id}/kba` return 500; use query param pattern if importing links.

## 8. Immediate next engineering tasks

Phase 2A is on `feature/integrations-mcp` (PR #1): Company, MCP registry, IntegrationConnection, payload sync, SPA Companies/Integrations, SQL cascade-safe FKs, company related summary.

Next:
1. Halo company pull via Compact `halo_list_clients` shipped this slice. Still: Key Vault credentials for live Compact/Composio, then Ninja/CIPP/Meraki/UniFi pulls.
2. CompanyId on document/runbook/Keeper create-edit (assets already accept it)
3. Phase 2B: UniFi + Blackpoint MCP; sync schedules + SyncRun UI. Halo/Ninja company deep links shipped (`HaloPortalUrl` / `NinjaPortalUrl`).
4. Phase 2C: relationship graph, Azure AI Search, client portal, Hudu export import (passwords → Keeper only). Expirations + flags + runbook runs + process completion rollup shipped.
5. `dotnet ef migrations add Phase2IntegrationsReconcile` if the model snapshot still needs regen after the hand-written Phase 2 migrations

## 9. Hudu ↔ DocuEngAIne verification matrix

Hudu jobs we observed. Masri ships the **job**, not the Hudu screen. Status is capability shipped, not pixel parity.

| Hudu job | Masri approach | Status | Verify notes |
|---|---|---|---|
| Top nav — Dashboard | Masri hub (assets/docs/runbooks/Keeper). Recents/expirations later. Not Magic Dash. | Phase1 | Open `/`. No Hudu widgets. |
| Top nav — Companies | `Company` client space ≠ Entra tenant. `/companies`. | Phase2A | List + detail. Halo/Ninja IDs shown. |
| Top nav — Global views | Tenant-wide Assets/Docs/Runbooks/Keeper lists. | Phase1 | Cross-company filter later. |
| Top nav — Central KB | Tenant-wide Documents with folders. Public/share later. | Phase2 | `/documents` folder list + articles. Not Hudu chrome. |
| Top nav — My Vault | Do not replicate. Keeper is the vault. | skip | No password/TOTP tables. |
| Top nav — Admin | Entra + RBAC + audit. Azure for SMTP/license. | Phase1 | Users/roles/audit exist. Layouts UI later. |
| Company home | Overview + related counts/lists (assets, docs, runbooks, Keeper). | Phase2A | `/companies/:id`. Not Hudu tab chrome. |
| Company passwords | `KeeperLink` filtered by `CompanyId`. Reveal audits. | Phase2A | Links only. Open in Keeper. |
| Company processes | Extend **Runbook** (not a second product): `RunbookRun` with Running/Completed/Cancelled, optional `CompanyId` (`ForTenant`), `runCount` + start-run. Tenant-wide books = templates (Hudu admin Process Templates had 0); per-company books = checklists (ExampleCo had 2 ad-hoc). | Phase2C | `/runbooks` shows runCount + Start run. Company detail lists runCount. Other-tenant runbook start = 404. |
| Global process completion | Tenant rollup of recent `RunbookRun`s (`GET /api/runbooks/runs?status=&companyId=`). Joins runbook title + company name via existing tables. Unknown/other-tenant company returns empty. Not Hudu chrome. | Phase2C | `/runs`. Table: Runbook, Company, Status, Started, Finished. Other-tenant runs do not leak. |
| Company KB | Documents filtered by `CompanyId`. Folders may be company-scoped. | Phase2A | Same Document model as central KB. |
| Company photos | Blob storage later. | later | |
| Company IPAM | UniFi/MCP owns live network. No IPAM product line day one. | later | |
| Company racks | Defer unless a tenant demands it. | later | |
| Company websites | Asset layout + expiration later. | later | |
| Company expirations | Date fields (`FieldDefinition.IsExpiration` on Date/DateTime) plus optional `Asset.ExpiresAt`, rolled up. `GET /api/expirations?companyId=` is ForTenant; unknown/other-tenant company returns empty (Hudu `/c/{id}/expirations` 500s — do not copy). Hide expired unless `showExpired=true`. | Phase2C | `/expirations` or `companyId` filter. Not Hudu chrome. |
| Company portal | Entra external ID / magic-link read-only. | later | |
| Company related items | `ResourceLink` (FromType/FromId ↔ ToType/ToId, optional Label). Types: Company, Asset, Document, Runbook, KeeperLink. Unique per tenant pair. Both ends `ForTenant` or 400. `GET/POST/DELETE /api/links`. Company detail includes `relatedLinks` count + short list. Not a graph viz; not Hudu tabs. `AssetDocumentLink` stays the asset↔doc convenience. Graph query later. | Phase2C | `/companies/:id` Related section. Other-tenant entity = 400. |
| Company museum | Novelty. Skip. | skip | |
| Asset layouts (all types) | One `AssetType` + `FieldDefinition` model. Map from Halo/Ninja. Do not clone 32 layouts. | Phase1 | Create types as ops needs them. |
| Admin (users, groups, layouts, API, import/export, SMTP, license) | Entra + Azure. Import only for Hudu migration. | later | Audit is Phase1. |
| Integrations — Halo | Compact MCP only (`halo_list_clients` → `Company`). `McpServer.Kind=StackJackCompact`. Secret **name** only. Halo is SoR. | Phase2A | Test/sync endpoints. GET by id. Other-tenant sync → 404. |
| Integrations — NinjaOne | Same path → `Company` + Computer Assets. Secret **name** only. | Phase2A | Same sync service by provider. |
| Integrations — sync scope toggles | Typed bools on `IntegrationConnection` (not a 32-tile form). Skip inactive/contacts/locations/assets; auto-update names; refuse company overwrite by default. | Phase2A | GET/POST/PUT `/api/integrations`. Honored in `SyncFromPayload`. |
| Integrations — UniFi | MCP connector, not a Hudu form. | later | |
| Integrations — M365 / Intune | Graph or MCP when ops needs it. | later | |
| Integrations — Blackpoint | First-class Masri MCP later. | later | |
| Integrations — 32-tile catalog | MCP registry; add on demand. | skip | |
| Hudu Bridge / Hudini / leaderboard | Skip. Masri AI = our RAG + MCP later. | skip | |
| Central KB folders / public share | `DocumentFolder` + `Document.FolderId`. Public/share later. | Phase2 | `/api/folders` CRUD, `GET /api/documents?folderId=`. Other-tenant folder → 400. Distinct from company-filtered docs. |
| Global expirations | Same rollup without `companyId`. Search `q` + show-expired toggle. Types are field names (Expiration Date, End Date, License Expiration, Renewal Date, Next Battery Replacement, SSL Certificate, Domain, …) or `Expiration` for the asset shortcut. | Phase2C | `/expirations`. Table: Name, Company, Type, Date, days. |
| Flags / Flag Review | Named color labels (`FlagDefinition`: Name + hex Color + IsActive) applied to Company, Asset, Document, Runbook, KeeperLink (`FlagAssignment`). Admin CRUD. `GET /api/flags/review?entityType=` joins names via existing tables, ForTenant. Not Hudu label chrome. Alerts/workflows are separate. | Phase2C | `/flags`. Other-tenant entity assign = 400. |
| Client portal | Phase 2C. Keeper reveal only, never local secrets. | later | |
| Passwords (global) | Tenant-wide KeeperLink list. No vault. | skip | Phase1 links exist. |

## 9b. Live verify 2026-08-27 (`masri.huducloud.com`)

Read-only. Active companies: **1** (ExampleCo). Archived: **0**. Configured integrations: **0** of **32**.

Hudu company list is `/c` (not `/companies`). Create form fields: Name*, Parent, Logo, Nickname, Company ID Number, Type, Address 1/2, City, Country, State, Postal, Phone, Fax, Website.

**Already on Company (correction):** Phone, Website, CompanyNumber, Address, City, State, Notes, HoursOfOperation. Missing vs that form: Type, Parent, Logo, Nickname, Fax, Country, Postal.

| Hudu job | Result vs DocuEngAIne |
|---|---|
| Company CRUD + tenant silo | COVERED |
| Halo/Ninja org matching + test/sync/runs | COVERED |
| MCP servers | COVERED (Masri extra) |
| Company home related lists | COVERED this slice (assets/docs/runbooks/Keeper by CompanyId). Not Hudu tab chrome. |
| Company type / archive tab / A–Z filter | NOT YET (IsActive exists; no archive UX). Flags are the Flags / Flag Review row. |
| Parent company, logo | NOT YET |
| Halo/Ninja skip/overwrite toggles | COVERED this slice (`IntegrationConnection` sync-policy bools; defaults refuse company overwrite). Layout mapping still later. |
| UniFi local user/password | INTENTIONAL: Key Vault secret name only |
| 32-vendor catalog, in-app secrets, portal | INTENTIONAL SKIP |
| Asset-layout mapping per remote object | NOT YET (use AssetType as ops needs it) |

Hudu bugs in this tenant (do not copy): `/c/{id}/expirations` 500 (global expirations works, 7 rows); `/c/{id}/kba` 500 (use `/kba?company_id=`); Filters button on company list did not open.

Expirations in Hudu are date fields on asset layouts, rolled up globally. Process templates (admin, 0) vs per-company processes (ExampleCo has 2, ad-hoc). Flags = named colored labels. Alerts/workflows = none configured.

