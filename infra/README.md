# DealsScannerPro Infrastructure

Azure infrastructure defined with Bicep.

## Resources

| Resource | Name | Description |
|----------|------|-------------|
| Resource Group | rg-dealscanner-prod | Contains all resources |
| Storage Account | stdealscannerprod | Table Storage for deals |
| Function App | func-dealscanner-prod | C# .NET 8 API |
| Key Vault | kv-dealscanner-prod | Secrets storage |
| App Insights | appi-dealscanner-prod | Monitoring |
| Log Analytics | log-dealscanner-prod | Logs |

## Table Storage Design

### Tilbud Table
- **PartitionKey**: `{butik}_{gyldig_fra}_{gyldig_til}`
- **RowKey**: `{uuid}`
- Contains all deal data

### Butikker Table
- **PartitionKey**: `butikker`
- **RowKey**: `{butik_id}`
- Store metadata (name, colors, logo)

### AktivePerioder Table
- **PartitionKey**: `{butik}`
- **RowKey**: `{gyldig_fra}_{gyldig_til}`
- Quick lookup of active periods

## Prerequisites

1. Azure CLI installed
2. Bicep CLI installed (comes with Azure CLI)
3. Azure subscription with Contributor access

## Deployment

### First time setup

```bash
# Login to Azure
az login

# Set subscription (if you have multiple)
az account set --subscription "Your Subscription Name"
```

### Deploy infrastructure

```bash
# Generate a secure API key
$apiKey = [System.Guid]::NewGuid().ToString()

# Deploy to Azure
az deployment sub create `
  --location westeurope `
  --template-file infra/main.bicep `
  --parameters infra/parameters/prod.bicepparam `
  --parameters adminApiKey=$apiKey

# Save the API key securely!
Write-Host "Admin API Key: $apiKey"
```

### Validate before deploying

```bash
az deployment sub validate `
  --location westeurope `
  --template-file infra/main.bicep `
  --parameters infra/parameters/prod.bicepparam `
  --parameters adminApiKey='test-key'
```

## Outputs

After deployment, you'll get:
- `resourceGroupName`: The resource group name
- `functionAppName`: Function App name
- `functionAppUrl`: Function App URL
- `storageAccountName`: Storage account name
- `keyVaultName`: Key Vault name

## Estimated Monthly Cost

| Resource | Estimated Cost |
|----------|---------------|
| Storage Account | ~1-5 DKK |
| Function App (Consumption) | Free (1M requests) |
| Key Vault | ~2 DKK |
| App Insights | Free (5GB/month) |
| **Total** | **~5-10 DKK/month** |
