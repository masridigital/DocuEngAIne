@description('SQL server name')
param serverName string

@description('Database name')
param databaseName string

@description('Azure region')
param location string = resourceGroup().location

@description('SQL admin login')
param adminLogin string

@secure()
@description('SQL admin password')
param adminPassword string

@description('Database SKU name')
param skuName string = 'Basic'

@description('Database SKU tier')
param skuTier string = 'Basic'

@description('Optional Entra ID admin display name or UPN. Required for CREATE USER FROM EXTERNAL PROVIDER. Leave empty to set later via infra/grant-sql-contained-user.sh.')
param entraAdminLogin string = ''

@description('Optional Entra ID admin object ID (user, group, or service principal).')
param entraAdminObjectId string = ''

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: serverName
  location: location
  properties: {
    administratorLogin: adminLogin
    administratorLoginPassword: adminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    version: '12.0'
  }
}

resource firewall 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Entra admin enables Azure AD authentication alongside SQL auth. The App Service
// identity is not the server admin; grant it a contained user with
// infra/grant-sql-contained-user.sh after the first deploy.
resource entraAdmin 'Microsoft.Sql/servers/administrators@2023-08-01-preview' = if (!empty(entraAdminLogin) && !empty(entraAdminObjectId)) {
  parent: sqlServer
  name: 'ActiveDirectory'
  properties: {
    administratorType: 'ActiveDirectory'
    login: entraAdminLogin
    sid: entraAdminObjectId
    tenantId: subscription().tenantId
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: skuName == 'Basic' ? 2147483648 : 268435456000
  }
}

output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = database.name
