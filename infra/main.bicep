// DealsScannerPro - Azure Infrastructure
// Main Bicep template that orchestrates all modules

targetScope = 'subscription'

@description('Environment name')
@allowed(['prod', 'dev'])
param environment string = 'prod'

@description('Azure region for resources')
param location string = 'westeurope'

@description('Base name for resources')
param baseName string = 'dealscanner'

@description('Admin API key for upload endpoint')
@secure()
param adminApiKey string

// Resource naming
var resourceGroupName = 'rg-${baseName}-${environment}'
var tags = {
  Environment: environment
  Project: 'DealsScannerPro'
  ManagedBy: 'Bicep'
}

// Resource Group
resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// Application Insights (deployed first, needed by Function App)
module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    baseName: baseName
    environment: environment
    location: location
    tags: tags
  }
}

// Storage Account with Tables
module storage 'modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    baseName: baseName
    environment: environment
    location: location
    tags: tags
  }
}

// Key Vault
module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  scope: rg
  params: {
    baseName: baseName
    environment: environment
    location: location
    tags: tags
    adminApiKey: adminApiKey
    storageConnectionString: storage.outputs.connectionString
  }
}

// Function App
module functionApp 'modules/function.bicep' = {
  name: 'functionapp'
  scope: rg
  params: {
    baseName: baseName
    environment: environment
    location: location
    tags: tags
    appInsightsConnectionString: monitoring.outputs.connectionString
    keyVaultName: keyVault.outputs.keyVaultName
    storageAccountName: storage.outputs.storageAccountName
  }
}

// Grant Function App access to Key Vault
module keyVaultAccess 'modules/keyvault-access.bicep' = {
  name: 'keyvault-access'
  scope: rg
  params: {
    keyVaultName: keyVault.outputs.keyVaultName
    principalId: functionApp.outputs.principalId
  }
}

// Scanner Function App (Python, blob trigger)
module scannerFunction 'modules/scanner-function.bicep' = {
  name: 'scanner-functionapp'
  scope: rg
  params: {
    baseName: baseName
    environment: environment
    location: location
    tags: tags
    storageAccountName: storage.outputs.storageAccountName
    appInsightsConnectionString: monitoring.outputs.connectionString
    keyVaultName: keyVault.outputs.keyVaultName
  }
}

// Grant Scanner Function App access to Key Vault
module scannerKeyVaultAccess 'modules/keyvault-access.bicep' = {
  name: 'scanner-keyvault-access'
  scope: rg
  params: {
    keyVaultName: keyVault.outputs.keyVaultName
    principalId: scannerFunction.outputs.functionAppPrincipalId
  }
}

// Outputs
output resourceGroupName string = rg.name
output functionAppName string = functionApp.outputs.functionAppName
output functionAppUrl string = functionApp.outputs.functionAppUrl
output scannerFunctionAppName string = scannerFunction.outputs.functionAppName
output storageAccountName string = storage.outputs.storageAccountName
output keyVaultName string = keyVault.outputs.keyVaultName
output appInsightsName string = monitoring.outputs.appInsightsName
