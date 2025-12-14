// Azure Key Vault for secrets

@description('Base name for resources')
param baseName string

@description('Environment name')
param environment string

@description('Azure region')
param location string

@description('Resource tags')
param tags object

@description('Admin API key')
@secure()
param adminApiKey string

@description('Storage connection string')
@secure()
param storageConnectionString string

var keyVaultName = 'kv-${baseName}-${environment}'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: false // Set to true for production with strict requirements
  }
}

// Admin API Key secret
resource adminApiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'AdminApiKey'
  properties: {
    value: adminApiKey
  }
}

// Table Storage connection string secret
resource storageConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'TableStorageConnection'
  properties: {
    value: storageConnectionString
  }
}

// Outputs
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
