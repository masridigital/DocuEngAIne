@description('App Service name')
param appName string

@description('App Service Plan name')
param planName string

@description('Azure region')
param location string = resourceGroup().location

@description('Entra ID authority')
param entraAuthority string

@description('Entra ID audience')
param entraAudience string

@description('Key Vault name that holds secrets')
param keyVaultName string

@description('SQL connection string for the app. Production is Authentication=Active Directory Default (no password).')
param sqlConnectionString string

@description('Allowed CORS origins')
param allowedOrigins array = []

@description('App Service Plan SKU')
param planSku object = {
  name: 'B2'
  tier: 'Basic'
  size: 'B2'
  family: 'B'
  capacity: 1
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' existing = {
  name: keyVaultName
}

resource appPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: planSku
  properties: {
    reserved: true
    perSiteScaling: false
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appPlan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      webSocketsEnabled: false
      linuxFxVersion: 'DOTNETCORE|10.0'
      healthCheckPath: '/api/health/live'
      cors: {
        allowedOrigins: allowedOrigins
        supportCredentials: false
      }
      connectionStrings: [
        {
          name: 'DocuEngAIne'
          connectionString: sqlConnectionString
          type: 'SQLAzure'
        }
      ]
      appSettings: [
        {
          name: 'EntraId__Authority'
          value: entraAuthority
        }
        {
          name: 'EntraId__Audience'
          value: entraAudience
        }
        {
          name: 'Azure__KeyVault__VaultUri'
          value: keyVault.properties.vaultUri
        }
        {
          name: 'Azure__Sql__UseManagedIdentity'
          value: 'true'
        }
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
      ]
    }
  }
}

resource keyVaultSecretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webApp.id, keyVault.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output url string = 'https://${webApp.properties.defaultHostName}'
output principalId string = webApp.identity.principalId
