# Compact → HaloPSA

Envelope on list tools: `{ record_count, clients | users | sites | assets }`.

## Shipped

| Tool | Entity | Notes |
|---|---|---|
| `halo_list_clients` | `Company` | |
| `halo_list_sites` | `Asset` (Locations layout) | `SkipLocations`. No `pageNo`. Cap 200. |
| `halo_list_users` | `Asset` (People layout) | `SkipContacts`. People, not Entra `User`. |
| `halo_list_assets` | `Asset` (Computer) | `SkipAssets`. `inactive` assets are skipped with `SkipInactive`. Name is `inventory_number`, falling back to `name`. |

## Do not persist

Ticket list/get tools exist on Compact. There is no Ticket entity. Do not write tickets into SQL.
