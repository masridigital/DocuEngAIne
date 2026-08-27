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
  }
}

var sqlConnectionString = 'Server=tcp:${sql.outputs.serverFqdn},1433;Initial Catalog=${sql.outputs.databaseName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvaultDeploy'
  params: {
    vaultName: vaultName
    location: location
    sqlConnectionString: sqlConnectionString
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
    sqlConnectionStringKeyVaultSecretUri: keyvault.outputs.sqlConnectionStringSecretUri
    allowedOrigins: allowedOrigins
  }
}

output appUrl string = app.outputs.url
output sqlServerFqdn string = sql.outputs.serverFqdn
output keyVaultName string = vaultName
