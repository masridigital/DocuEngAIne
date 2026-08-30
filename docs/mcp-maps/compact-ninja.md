# Compact → NinjaOne

Cursor on list tools: `after` = last id from the previous page.

## Shipped

| Tool | Entity |
|---|---|
| `ninja_list_organizations` | `Company` |

`NinjaDeviceMapper` already exists on main (`ninja_list_devices` → `Asset`). Wire it when device sync is enabled; list is the sync source.

## Needed

| Tool | Target | Notes |
|---|---|---|
| `ninja_list_devices` | `Asset` | Mapper shipped. Do **not** call `ninja_list_devices_detailed` in sync. |
| `ninja_list_locations` | — | No Location entity. Do not persist. |
