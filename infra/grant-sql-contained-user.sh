#!/usr/bin/env bash
# Post-step after `az deployment group create` of infra/main.bicep.
#
# Bicep provisions the SQL server, database, and App Service system-assigned
# identity. It cannot CREATE USER ... FROM EXTERNAL PROVIDER. Run this once
# after the first deploy (and again if the App Service is recreated).
#
# The signed-in Azure CLI identity is made the SQL Entra admin if none exists,
# then a contained database user is created for the App Service identity and
# granted db_datareader + db_datawriter. Set SQL_GRANT_DDLADMIN=true if the
# app (not the migrate job) will apply EF migrations.
#
# Usage:
#   az login
#   AZURE_RESOURCE_GROUP=rg-docuengaine-prod \
#   SQL_SERVER=docuengaine-prod-sql \
#   SQL_DATABASE=DocuEngAIne \
#   APP_NAME=docuengaine-prod-app \
#   ./infra/grant-sql-contained-user.sh
#
# Defaults match infra/main.bicep with environment=prod.

set -euo pipefail

AZURE_RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-rg-docuengaine-prod}"
SQL_SERVER="${SQL_SERVER:-docuengaine-prod-sql}"
SQL_DATABASE="${SQL_DATABASE:-DocuEngAIne}"
APP_NAME="${APP_NAME:-docuengaine-prod-app}"
SQL_GRANT_DDLADMIN="${SQL_GRANT_DDLADMIN:-false}"

if ! command -v az >/dev/null 2>&1; then
  echo "ERROR: Azure CLI (az) is required." >&2
  exit 1
fi

echo "Granting SQL contained user for App Service identity: $APP_NAME"
echo "  resource group: $AZURE_RESOURCE_GROUP"
echo "  server:         $SQL_SERVER"
echo "  database:       $SQL_DATABASE"

# CREATE USER FROM EXTERNAL PROVIDER requires an Entra admin on the server.
# SQL auth admin cannot create Entra users. If none is set, use the caller.
EXISTING_ADMIN=$(az sql server ad-admin list \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --server "$SQL_SERVER" \
  --query "[0].sid" -o tsv 2>/dev/null || true)

if [ -z "${EXISTING_ADMIN:-}" ]; then
  echo "No SQL Entra admin found; setting the signed-in identity as admin."
  ACCOUNT_TYPE=$(az account show --query user.type -o tsv)
  if [ "$ACCOUNT_TYPE" = "servicePrincipal" ]; then
    SP_APP_ID=$(az account show --query user.name -o tsv)
    ADMIN_OBJECT_ID=$(az ad sp show --id "$SP_APP_ID" --query id -o tsv)
    ADMIN_DISPLAY=$(az ad sp show --id "$SP_APP_ID" --query displayName -o tsv)
  else
    ADMIN_OBJECT_ID=$(az ad signed-in-user show --query id -o tsv)
    ADMIN_DISPLAY=$(az ad signed-in-user show --query userPrincipalName -o tsv)
  fi
  az sql server ad-admin create \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --server "$SQL_SERVER" \
    --display-name "$ADMIN_DISPLAY" \
    --object-id "$ADMIN_OBJECT_ID" \
    --output none
  echo "Set SQL Entra admin to $ADMIN_DISPLAY ($ADMIN_OBJECT_ID)."
fi

SQL_QUERIES="
  IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = '$APP_NAME')
    CREATE USER [$APP_NAME] FROM EXTERNAL PROVIDER;

  IF NOT EXISTS (
    SELECT 1 FROM sys.database_role_members drm
    JOIN sys.database_principals r ON drm.role_principal_id = r.principal_id
    JOIN sys.database_principals m ON drm.member_principal_id = m.principal_id
    WHERE r.name = 'db_datareader' AND m.name = '$APP_NAME'
  )
    ALTER ROLE db_datareader ADD MEMBER [$APP_NAME];

  IF NOT EXISTS (
    SELECT 1 FROM sys.database_role_members drm
    JOIN sys.database_principals r ON drm.role_principal_id = r.principal_id
    JOIN sys.database_principals m ON drm.member_principal_id = m.principal_id
    WHERE r.name = 'db_datawriter' AND m.name = '$APP_NAME'
  )
    ALTER ROLE db_datawriter ADD MEMBER [$APP_NAME];
"

if [ "$SQL_GRANT_DDLADMIN" = "true" ]; then
  SQL_QUERIES="$SQL_QUERIES
  IF NOT EXISTS (
    SELECT 1 FROM sys.database_role_members drm
    JOIN sys.database_principals r ON drm.role_principal_id = r.principal_id
    JOIN sys.database_principals m ON drm.member_principal_id = m.principal_id
    WHERE r.name = 'db_ddladmin' AND m.name = '$APP_NAME'
  )
    ALTER ROLE db_ddladmin ADD MEMBER [$APP_NAME];
"
fi

if ! az extension show --name rdbms-connect >/dev/null 2>&1; then
  echo "Installing Azure CLI extension 'rdbms-connect' (az sql db query)."
  az extension add --name rdbms-connect --yes
fi

az sql db query \
  --server "$SQL_SERVER" \
  --database "$SQL_DATABASE" \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --auth-mode ActiveDirectoryDefault \
  --queries "$SQL_QUERIES"

echo "Contained user [$APP_NAME] granted on $SQL_DATABASE."
