# DealsScannerPro.Backend

Azure backend for DealsScannerPro - C# Function App + Bicep infrastructure.

## Overview

This repository contains:
- **infra/**: Bicep templates for Azure infrastructure
- **src/**: C# .NET 8 Azure Function App (API)

## Related Repositories

| Repo | Description |
|------|-------------|
| [DealsScannerPro](https://github.com/Hverdagskokkens-ugeplan/DealsScannerPro) | Python PDF scanner engine |
| DealsScannerPro.App | Consumer app (coming soon) |

## Architecture

```
┌─────────────────┐     JSON      ┌──────────────────┐
│ DealsScannerPro │ ───────────▶  │ Admin API        │
│ (Python Scanner)│   POST /upload│ (this repo)      │
└─────────────────┘               └────────┬─────────┘
                                           │
                                           ▼
                                  ┌──────────────────┐
                                  │ Azure Table      │
                                  │ Storage          │
                                  └────────┬─────────┘
                                           │
                                           ▼
┌─────────────────┐               ┌──────────────────┐
│ Consumer App    │ ◀──────────── │ Consumer API     │
│ (future)        │  GET /deals   │ (this repo)      │
└─────────────────┘               └──────────────────┘
```

## Azure Resources

| Resource | Name | Description |
|----------|------|-------------|
| Resource Group | rg-dealscanner-prod | Contains all resources |
| Storage Account | stdealscannerprod | Table Storage for deals |
| Function App | func-dealscanner-prod | C# .NET 8 API |
| Key Vault | kv-dealscanner-prod | Secrets storage |
| App Insights | appi-dealscanner-prod | Monitoring |

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli)
- [Azure Functions Core Tools](https://docs.microsoft.com/azure/azure-functions/functions-run-local)

### Deploy Infrastructure

```powershell
# Login to Azure
az login

# Generate API key
$apiKey = [System.Guid]::NewGuid().ToString()

# Deploy
az deployment sub create `
  --location westeurope `
  --template-file infra/main.bicep `
  --parameters infra/parameters/prod.bicepparam `
  --parameters adminApiKey=$apiKey

# Save API key securely!
Write-Host "Admin API Key: $apiKey"
```

### Run Locally

```bash
cd src/DealsScannerPro.Api
func start
```

## API Endpoints

### Admin API (requires API key)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/admin/upload` | Upload scanned deals |

### Consumer API (public)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/deals` | Search deals |
| GET | `/api/deals/{id}` | Get single deal |
| POST | `/api/match-shopping-list` | Match shopping list |
| GET | `/api/stores` | List stores |
| GET | `/api/categories` | List categories |

## License

MIT License
