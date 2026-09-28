# Compact → NinjaOne

Cursor on list tools: `after` = last id from the previous page.

## Shipped

| Tool | Entity | Notes |
|---|---|---|
| `ninja_list_organizations` | `Company` | |
| `ninja_list_devices` | `Asset` (Computer) | Skipped with `SkipAssets`. Do **not** call `ninja_list_devices_detailed` in sync. |
| `ninja_list_locations` | `Asset` (Locations layout) | Skipped with `SkipLocations`. Attached through the organization's company mapping; a location whose organization is unmapped is skipped. The list has no city or inactive flag. |
