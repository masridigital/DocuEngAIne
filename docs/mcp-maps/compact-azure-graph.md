# Compact → Azure Graph

CSP company list, not Intune inventory. NinjaOne remains device SoR.

## Company list

| Tool | Role | Fields |
|---|---|---|
| `graph_list_delegated_admin_customers` | primary | `id`, `tenantId`, `displayName` |
| `graph_list_partner_customers` | enrich | optional partner fields |

Skip the Home tenant.

## Optional assets

`azure_list_subscriptions` and `azure_list_resource_groups` may map to `Asset`. Do **not** bulk-import Intune devices — Ninja is SoR for devices.
