# Compact → HaloPSA

Envelope on list tools: `{ record_count, clients | users | sites | assets }`.

## Shipped

| Tool | Entity |
|---|---|
| `halo_list_clients` | `Company` |

## Next (not this PR)

| Tool | Target | Notes |
|---|---|---|
| `halo_list_users` | contacts | **People** asset layout, not Entra `User` |
| `halo_list_sites` | locations | No `pageNo`. Cap 200. |
| `halo_list_assets` | `Asset` | |

## Do not persist

Ticket list/get tools exist on Compact. There is no Ticket entity. Do not write tickets into SQL.
