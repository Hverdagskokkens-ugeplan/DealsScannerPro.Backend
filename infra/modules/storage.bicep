// Storage Account with Table Storage for deals data

@description('Base name for resources')
param baseName string

@description('Environment name')
param environment string

@description('Azure region')
param location string

@description('Resource tags')
param tags object

// Storage account name (must be lowercase, no hyphens, 3-24 chars)
var storageAccountName = replace('st${baseName}${environment}', '-', '')

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
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
    accessTier: 'Hot'
  }
}

// Table Service
resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

// Tilbud Table - stores all deals
resource tilbudTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-01-01' = {
  parent: tableService
  name: 'Tilbud'
}

// Butikker Table - stores store metadata
resource butikkerTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-01-01' = {
  parent: tableService
  name: 'Butikker'
}

// AktivePerioder Table - tracks active deal periods per store
resource aktivePerioder 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-01-01' = {
  parent: tableService
  name: 'AktivePerioder'
}

// Outputs
output storageAccountName string = storageAccount.name
output connectionString string = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${az.environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
output tableEndpoint string = storageAccount.properties.primaryEndpoints.table
