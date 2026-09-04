#!/usr/bin/env pwsh
# Writes appsettings.Development.json from the azd environment.
#
# Run automatically by `azd up` as a postprovision hook. Safe to run by hand at any time; it only
# reads azd environment values and rewrites one gitignored file.

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $repoRoot 'appsettings.Development.json'

function Get-AzdValue {
    param([Parameter(Mandatory)][string]$Name, [switch]$Required)

    $value = $env:AZURE_ENV_NAME ? (azd env get-value $Name 2>$null) : $null
    if ([string]::IsNullOrWhiteSpace($value) -or $value -like 'ERROR*') {
        if ($Required) {
            throw "Could not read '$Name' from the azd environment. Run 'azd up' first, or edit $settingsPath by hand."
        }
        return $null
    }
    return $value.Trim()
}

$searchEndpoint = Get-AzdValue 'SEARCH_ENDPOINT' -Required
$openAiEndpoint = Get-AzdValue 'AZURE_OPENAI_ENDPOINT' -Required
$embeddingDeployment = Get-AzdValue 'AZURE_OPENAI_EMBEDDING_DEPLOYMENT' -Required
$embeddingModel = Get-AzdValue 'AZURE_OPENAI_EMBEDDING_MODEL' -Required
$embeddingDimensions = Get-AzdValue 'AZURE_OPENAI_EMBEDDING_DIMENSIONS' -Required
$blurbDeployment = Get-AzdValue 'AZURE_OPENAI_BLURB_DEPLOYMENT' -Required

$settings = [ordered]@{
    Search    = [ordered]@{
        Endpoint = $searchEndpoint
    }
    Foundry   = [ordered]@{
        Endpoint            = $openAiEndpoint
        EmbeddingDeployment = $embeddingDeployment
        EmbeddingModel      = $embeddingModel
        EmbeddingDimensions = [int]$embeddingDimensions
        BatchDeployment     = $blurbDeployment
    }
}

$settings | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsPath -Encoding utf8NoBOM

Write-Host "Wrote $settingsPath"
Write-Host "  search    $searchEndpoint"
Write-Host "  openai    $openAiEndpoint"
Write-Host ''
Write-Host 'Next: dotnet run --project src/CrossIndexQuery.Cli -- doctor'
