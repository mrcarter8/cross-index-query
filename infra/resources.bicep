@description('Location for all resources.')
param location string

@description('Stable suffix that keeps globally scoped names unique.')
param resourceToken string

@description('Tags applied to every resource.')
param tags object

@description('Object ID of the principal that will run the sample.')
param principalId string

@description('Type of the principal.')
param principalType string

param embeddingCapacity int
param blurbCapacity int

var searchServiceName = 'srch-xindex-${resourceToken}'
var openAiName = 'aoai-xindex-${resourceToken}'

var embeddingDeploymentName = 'text-embedding-3-small'
var embeddingModelName = 'text-embedding-3-small'
var embeddingDimensions = 1536
var blurbDeploymentName = 'gpt-5.4-batch'

// Built-in role definition IDs.
var searchServiceContributor = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
var searchIndexDataContributor = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var cognitiveServicesOpenAiUser = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

// -----------------------------------------------------------------------------------------------
// Azure AI Search
// -----------------------------------------------------------------------------------------------

// The 2026-03-01-Preview API version is required: it is the first that exposes `knowledgeRetrieval`,
// which the agentic-retrieval strategy needs and which otherwise defaults to `free`.
resource search 'Microsoft.Search/searchServices@2026-03-01-Preview' = {
  name: searchServiceName
  location: location
  tags: tags
  sku: {
    // Serverless bills per operation, which is what lets the evaluation harness report the measured
    // cost of each fusion strategy rather than an estimate. Any other SKU works for the sample, but
    // the cost column becomes meaningless because capacity is billed by the hour.
    name: 'serverless'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    // Serverless allocates compute on demand. Setting `replicaCount` or `partitionCount` here is
    // rejected outright, which is why this block omits both.
    hostingMode: 'Default'
    publicNetworkAccess: 'Enabled'
    semanticSearch: 'standard'
    knowledgeRetrieval: 'standard'
    authOptions: {
      aadOrApiKey: {
        aadAuthFailureMode: 'http401WithBearerChallenge'
      }
    }
  }
}

// -----------------------------------------------------------------------------------------------
// Azure OpenAI
// -----------------------------------------------------------------------------------------------

resource openAi 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: openAiName
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: openAiName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: openAi
  name: embeddingDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: embeddingCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModelName
      version: '1'
    }
  }
}

// Blurb generation runs through the Batch API, which is a separate and far cheaper quota pool than
// GlobalStandard. Generating ten thousand blurbs interactively would be both slow and expensive;
// as a batch it is neither.
resource blurbDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: openAi
  name: blurbDeploymentName
  sku: {
    name: 'GlobalBatch'
    capacity: blurbCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5.4'
      version: '2026-03-05'
    }
  }
  dependsOn: [
    // Deployments on one account must be created serially.
    embeddingDeployment
  ]
}

// -----------------------------------------------------------------------------------------------
// Role assignments
// -----------------------------------------------------------------------------------------------
// The sample authenticates with DefaultAzureCredential throughout and never reads an admin key, so
// these three assignments are what make it run at all.

resource searchServiceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(search.id, principalId, searchServiceContributor)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributor)
    principalId: principalId
    principalType: principalType
  }
}

resource searchDataRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(search.id, principalId, searchIndexDataContributor)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataContributor)
    principalId: principalId
    principalType: principalType
  }
}

resource openAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(openAi.id, principalId, cognitiveServicesOpenAiUser)
  scope: openAi
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUser)
    principalId: principalId
    principalType: principalType
  }
}

// Lets the search service call Azure OpenAI directly, which is what an integrated vectorizer needs.
// The sample embeds client-side so that both indexes provably share one embedding space, but the
// assignment costs nothing and makes the service usable for the integrated path too.
resource searchToOpenAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAi.id, search.id, cognitiveServicesOpenAiUser)
  scope: openAi
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUser)
    principalId: search.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output searchEndpoint string = 'https://${search.name}.search.windows.net'
output searchServiceName string = search.name
output openAiEndpoint string = openAi.properties.endpoint
output embeddingDeployment string = embeddingDeployment.name
output embeddingModel string = embeddingModelName
output embeddingDimensions int = embeddingDimensions
output blurbDeployment string = blurbDeployment.name
