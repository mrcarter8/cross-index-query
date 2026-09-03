targetScope = 'subscription'

@minLength(1)
@maxLength(48)
@description('Name of the environment. Used to derive the resource group and resource names.')
param environmentName string

@minLength(1)
@description('Location for all resources. Must support Azure AI Search serverless and Azure OpenAI.')
@allowed([
  'eastus'
  'eastus2'
  'centralus'
  'northcentralus'
  'southcentralus'
  'westus'
  'westus2'
  'westus3'
  'northeurope'
  'westeurope'
  'swedencentral'
  'uksouth'
  'australiaeast'
  'japaneast'
])
param location string

@description('Object ID of the principal that will run the sample. Defaults to the deploying user.')
param principalId string = ''

@description('Type of the principal. Use "ServicePrincipal" when deploying from CI.')
@allowed([
  'User'
  'ServicePrincipal'
])
param principalType string = 'User'

@description('Capacity, in thousands of tokens per minute, for the embedding deployment.')
param embeddingCapacity int = 250

@description('''
Enqueued-token capacity, in thousands, for the blurb-generation batch deployment.
Generating all 10,000 blurbs enqueues roughly 20 million tokens, so 50,000 is comfortable. Raise it
to finish sooner if your subscription has spare GlobalBatch quota; lower it if the deployment fails
preflight with InsufficientQuota because another deployment already holds the quota.
''')
param blurbCapacity int = 50000

var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = {
  'azd-env-name': environmentName
  sample: 'cross-index-query'
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'resources'
  scope: resourceGroup
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
    principalId: principalId
    principalType: principalType
    embeddingCapacity: embeddingCapacity
    blurbCapacity: blurbCapacity
  }
}

// Consumed by `azd env get-values` and written into appsettings by scripts/write-settings.
output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = resourceGroup.name
output SEARCH_ENDPOINT string = resources.outputs.searchEndpoint
output SEARCH_SERVICE_NAME string = resources.outputs.searchServiceName
output AZURE_OPENAI_ENDPOINT string = resources.outputs.openAiEndpoint
output AZURE_OPENAI_EMBEDDING_DEPLOYMENT string = resources.outputs.embeddingDeployment
output AZURE_OPENAI_EMBEDDING_MODEL string = resources.outputs.embeddingModel
output AZURE_OPENAI_EMBEDDING_DIMENSIONS int = resources.outputs.embeddingDimensions
output AZURE_OPENAI_BLURB_DEPLOYMENT string = resources.outputs.blurbDeployment
