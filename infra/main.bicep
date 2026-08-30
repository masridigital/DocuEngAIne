@description('Environment name, used in resource naming')
param environment string = 'prod'

@description('Azure region')
param location string = resourceGroup().location

@description('SQL server admin login')
param sqlAdminLogin string

@secure()
@description('SQL server admin password')
param sqlAdminPassword string

@description('Entra ID authority for JWT validation')
param entraAuthority string

@description('Entra ID API audience (application client ID)')
param entraAudience string

@description('Optional allowed CORS origins')
param allowedOrigins array = []

@description('Optional Entra ID admin display name or UPN for the SQL server. Leave empty and run infra/grant-sql-contained-user.sh after deploy.')
param entraAdminLogin string = ''

@description('Optional Entra ID admin object ID for the SQL server.')
param entraAdminObjectId string = ''

var baseName = 'docuengaine-${environment}'
var vaultName = take('de${uniqueString(resourceGroup().id)}${environment}', 24)

module sql 'modules/sql.bicep' = {
  name: 'sqlDeploy'
  params: {
    serverName: '${baseName}-sql'
    databaseName: 'DocuEngAIne'
    location: location
    adminLogin: sqlAdminLogin
    adminPassword: sqlAdminPassword
    entraAdminLogin: entraAdminLogin
    entraAdminObjectId: entraAdminObjectId
  }
}

// SQL-auth string stays in Key Vault for the migrate job (efbundle) and break-glass.
// The App Service does not use it; production uses Authentication=Active Directory Default.
var sqlAdminConnectionString = 'Server=tcp:${sql.outputs.serverFqdn},1433;Initial Catalog=${sql.outputs.databaseName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
var sqlManagedIdentityConnectionString = 'Server=tcp:${sql.outputs.serverFqdn},1433;Initial Catalog=${sql.outputs.databaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvaultDeploy'
  params: {
    vaultName: vaultName
    location: location
    sqlConnectionString: sqlAdminConnectionString
  }
}

module app 'modules/app.bicep' = {
  name: 'appDeploy'
  params: {
    appName: '${baseName}-app'
    planName: '${baseName}-plan'
    location: location
    entraAuthority: entraAuthority
    entraAudience: entraAudience
    keyVaultName: vaultName
    sqlConnectionString: sqlManagedIdentityConnectionString
    allowedOrigins: allowedOrigins
  }
}

output appUrl string = app.outputs.url
output appName string = '${baseName}-app'
output appPrincipalId string = app.outputs.principalId
output sqlServerName string = '${baseName}-sql'
output sqlServerFqdn string = sql.outputs.serverFqdn
output sqlDatabaseName string = sql.outputs.databaseName
output keyVaultName string = vaultName
