using '../main.bicep'

param environment = 'prod'
param location = 'westeurope'
param baseName = 'dealscanner'

// IMPORTANT: Set this securely when deploying
// Use: az deployment sub create --parameters adminApiKey='your-secret-key'
// Or use Azure Key Vault / GitHub Secrets in CI/CD
param adminApiKey = '' // Must be provided at deployment time
