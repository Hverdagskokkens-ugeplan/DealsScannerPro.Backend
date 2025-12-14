// Azure Function App (.NET 8 Isolated Worker)

@description('Base name for resources')
param baseName string

@description('Environment name')
param environment string

@description('Azure region')
param location string

@description('Resource tags')
param tags object

@description('Application Insights connection string')
param appInsightsConnectionString string

@description('Key Vault name for secrets')
param keyVaultName string

@description('Storage account name')
param storageAccountName string

var functionAppName = 'func-${baseName}-${environment}'
var hostingPlanName = 'asp-${baseName}-${environment}'
var functionStorageAccountName = replace('stfunc${baseName}${environment}', '-', '')

// Storage account for Function App (runtime storage)
resource functionStorageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: functionStorageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
  }
}

// Consumption Plan
resource hostingPlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: hostingPlanName
  location: location
  tags: tags
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {
    reserved: false // false for Windows
  }
}

// Function App
resource functionApp 'Microsoft.Web/sites@2023-01-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v8.0'
      use32BitWorkerProcess: false
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${functionStorageAccount.name};EndpointSuffix=${az.environment().suffixes.storage};AccountKey=${functionStorageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
          value: 'DefaultEndpointsProtocol=https;AccountName=${functionStorageAccount.name};EndpointSuffix=${az.environment().suffixes.storage};AccountKey=${functionStorageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'WEBSITE_CONTENTSHARE'
          value: toLower(functionAppName)
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'KeyVaultName'
          value: keyVaultName
        }
        {
          name: 'StorageAccountName'
          value: storageAccountName
        }
        // Key Vault references for secrets
        {
          name: 'AdminApiKey'
          value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=AdminApiKey)'
        }
        {
          name: 'TableStorageConnection'
          value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=TableStorageConnection)'
        }
      ]
    }
  }
}

// Outputs
output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'
output principalId string = functionApp.identity.principalId
