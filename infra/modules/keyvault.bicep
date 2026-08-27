@description('Globally unique Key Vault name')
param vaultName string

@description('Azure region')
param location string = resourceGroup().location

@secure()
@description('SQL connection string stored as a secret')
param sqlConnectionString string

@description('Enable purge protection')
param enablePurgeProtection bool = true

resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' = {
  name: vaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    softDeleteRetentionInDays: 90
    enablePurgeProtection: enablePurgeProtection
    enableRbacAuthorization: true
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource sqlConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'ConnectionStrings--DocuEngAIne'
  properties: {
    value: sqlConnectionString
    contentType: 'connection-string'
  }
}

output name string = keyVault.name
output sqlConnectionStringSecretUri string = sqlConnectionSecret.properties.secretUriWithVersion
