#!/usr/bin/env bash
#
# Step 2 of docs/NEXT-ITEMS.md — reconcile the EF Core model snapshot.
#
# Why this exists
# ---------------
# Every Phase 2 migration was hand-written, and DocuEngAIneDbContextModelSnapshot.cs was never
# regenerated to match. It currently contains NO Companies, McpServers, IntegrationConnections,
# IntegrationMappings or SyncRuns table, and no Entities.Company reference at all. Company's
# parent/type/nickname/fax/postal/country columns and McpServer.Kind are missing too.
#
# Consequence: the next `dotnet ef migrations add` emits CreateTable for tables that already exist
# in every deployed database. DependencyInjection.cs currently suppresses PendingModelChangesWarning
# to keep `database update` working, which hides the drift rather than resolving it.
#
# This script regenerates the snapshot and PROVES the result is a no-op before you keep it.
#
# Run it on a machine with the .NET 10 SDK. It cannot run in the Claude Code web container --
# builds.dotnet.microsoft.com is blocked there by egress policy.
#
# Usage:  ./scripts/reconcile-model-snapshot.sh
#
set -euo pipefail

cd "$(dirname "$0")/.."

MIGRATION_NAME="Phase2IntegrationsReconcile"
MIG_DIR="src/DocuEngAIne.Api/Data/Migrations"

echo "==> Restoring local tools (dotnet-ef 10.0.7, per dotnet-tools.json)"
dotnet tool restore

echo "==> Building so the migration is generated against current code"
dotnet build --configuration Release

echo "==> Adding migration $MIGRATION_NAME"
dotnet ef migrations add "$MIGRATION_NAME" \
  --project src/DocuEngAIne.Api \
  --startup-project src/DocuEngAIne.Api \
  --output-dir Data/Migrations

GENERATED="$(ls -1 "$MIG_DIR"/*_"$MIGRATION_NAME".cs 2>/dev/null | head -1 || true)"
if [[ -z "$GENERATED" ]]; then
  echo "!! Migration file not found under $MIG_DIR -- stopping." >&2
  exit 1
fi

echo
echo "==> Generated: $GENERATED"
echo "==> Checking that Up() is EMPTY (this is the whole point)"
echo

# An empty Up()/Down() means the snapshot now matches the model and the hand-written
# migrations together -- i.e. pure catch-up with no schema change.
if grep -qE '^\s+(migrationBuilder\.[A-Za-z]+)' "$GENERATED"; then
  echo "!! Up()/Down() is NOT empty. The snapshot did not simply catch up:"
  echo
  grep -nE '^\s+migrationBuilder\.[A-Za-z]+' "$GENERATED" | head -40
  echo
  echo "   Do NOT commit this as-is. Each operation above is either:"
  echo "     (a) a real model/migration divergence to fix in the entity or the hand-written"
  echo "         migration, or"
  echo "     (b) something the hand-written migrations already did, meaning the snapshot is"
  echo "         still behind and needs another look."
  echo
  echo "   Investigate, fix, then re-run this script. The snapshot change itself"
  echo "   ($MIG_DIR/DocuEngAIneDbContextModelSnapshot.cs) is still worth reading either way."
  exit 2
fi

echo "   Up() is empty -- the snapshot is now a pure catch-up. Good."
echo
echo "==> Verifying EF now reports no pending model changes"
# has-pending-model-changes exits NON-ZERO when changes are pending -- it is built for CI gating.
# So a zero exit is the good case; do not invert this.
if dotnet ef migrations has-pending-model-changes \
     --project src/DocuEngAIne.Api --startup-project src/DocuEngAIne.Api; then
  echo "   No pending model changes."
else
  echo
  echo "!! EF still reports pending model changes after regenerating the snapshot." >&2
  echo "   The snapshot and the model disagree about something the empty Up() did not capture." >&2
  echo "   Do not commit until this is understood." >&2
  exit 3
fi

cat <<'NEXT'

==> Done. Remaining manual steps:

 1. Read the diff to DocuEngAIneDbContextModelSnapshot.cs. It should now include Companies,
    McpServers, IntegrationConnections, IntegrationMappings and SyncRuns, the Company parent /
    type / nickname / fax / postal / country columns, McpServer.Kind, and a real FK from
    DocumentFolder.CompanyId to Companies.

 2. Drop the drift suppression now that it is no longer needed, in
    src/DocuEngAIne.Infrastructure/DependencyInjection.cs -- remove the ConfigureWarnings block
    that ignores RelationalEventId.PendingModelChangesWarning (and its comment), so future drift
    fails loudly instead of silently.

 3. dotnet test   (expect the full suite green)

 4. Commit the empty migration together with the regenerated snapshot. The empty Up() is the
    point: it records that the snapshot caught up without changing any schema.

NEXT
