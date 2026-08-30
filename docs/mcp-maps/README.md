# MCP tool maps (catalog only)

Catalog of Compact and Composio tools DocuEngAIne may map onto entities. This slice is documentation only — no live import, no vendor REST, no product-code change.

## Endpoints

| Harness | Endpoint | Invoke |
|---|---|---|
| **StackJack Compact** | `https://compact.stackjack.io/mcp` (`/mcp` required) | `stackjack_run_readonly_tool` |
| **Composio** | `https://connect.composio.dev/mcp` | second harness; no live mutations in this PR |

Auth is a Key Vault secret name on `McpServer`. No vendor REST secrets in SQL.

## Systems of record

- **HaloPSA + NinjaOne** are SoR for clients and devices.
- **Keeper** is the vault. DocuEngAIne stores `KeeperLink` only — never passwords or TOTP.
- **No live Hudu or IT Glue import.** IT Glue is a one-shot migrate-only path, not an `IntegrationProvider`.

## Maps

| File | Scope |
|---|---|
| [compact-halo.md](compact-halo.md) | HaloPSA via Compact |
| [compact-ninja.md](compact-ninja.md) | NinjaOne via Compact |
| [compact-azure-graph.md](compact-azure-graph.md) | Azure / Graph via Compact |
| [composio.md](composio.md) | Composio allow/skip list |
