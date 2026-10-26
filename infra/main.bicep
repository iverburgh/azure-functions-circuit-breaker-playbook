// Deploys the circuit breaker Function: Flex Consumption, identity-based storage for the Durable
// Functions task hub, Application Insights, App Service Authentication with Entra ID, and
// optionally the roles its switches need.
targetScope = 'resourceGroup'

@description('Short name used to derive resource names, for example "cb-prod". Lower case letters, digits and hyphens.')
@minLength(3)
@maxLength(16)
param name string

param location string = resourceGroup().location

@description('Client ID of the Entra ID app registration that represents the breaker API (exposes the app roles).')
param apiClientId string

@description('Optional: name of an App Configuration store in this resource group whose feature flags the breaker may flip.')
param appConfigurationStoreName string = ''

@description('Optional: name of a Service Bus namespace in this resource group whose subscriptions the breaker may enable and disable.')
param serviceBusNamespaceName string = ''

@description('Name of the Durable Functions task hub. Change it to start with empty state, for example after a breaking change to the entity.')
param taskHubName string = 'circuitbreaker'

@description('Upper bound for scale-out. The breaker is light; a low limit keeps cost and storage contention predictable.')
@minValue(1)
param maximumInstanceCount int = 10

@description('Optional: origins allowed to call the API from a browser, for example the URL where the management console is hosted.')
param allowedOrigins array = []

var suffix = uniqueString(resourceGroup().id, name)
var storageAccountName = take(toLower(replace('st${name}${suffix}', '-', '')), 24)
var deploymentContainerName = 'deployments'

// Built-in role definition IDs (public, identical in every tenant).
var roles = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  appConfigurationDataOwner: '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b'
  serviceBusDataOwner: '090c5cfd-751d-490a-894a-3ce6f1109419'
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: false
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  // 'st' + name (3+) + uniqueString (13) is always at least 18 characters.
  #disable-next-line BCP334
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentContainerName
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-${name}'
  location: location
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: 'func-${name}-${suffix}'
  location: location
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      cors: {
        allowedOrigins: allowedOrigins
        supportCredentials: false
      }
      appSettings: concat(
        [
          // Identity-based storage: no account keys anywhere.
          { name: 'AzureWebJobsStorage__accountName', value: storage.name }
          { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
          { name: 'TaskHubName', value: taskHubName }
          { name: 'Security__Mode', value: 'EasyAuth' }
        ],
        empty(appConfigurationStoreName)
          ? []
          : [{ name: 'Switches__AppConfigurationStores__main', value: 'https://${appConfigurationStoreName}.azconfig.io' }],
        empty(serviceBusNamespaceName)
          ? []
          : [{ name: 'Switches__ServiceBusNamespace', value: '${serviceBusNamespaceName}.servicebus.windows.net' }]
      )
    }
  }
  dependsOn: [deploymentContainer]
}

// App Service Authentication validates every token and passes the caller on in X-MS-CLIENT-PRINCIPAL.
// The Function trusts that header only because this is enabled; never deploy it without.
resource authentication 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: functionApp
  name: 'authsettingsV2'
  properties: {
    platform: { enabled: true }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: apiClientId
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            'api://${apiClientId}'
            apiClientId
          ]
        }
      }
    }
  }
}

var storageRoles = [roles.storageBlobDataOwner, roles.storageQueueDataContributor, roles.storageTableDataContributor]

resource storageRoleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for role in storageRoles: {
    name: guid(storage.id, functionApp.id, role)
    scope: storage
    properties: {
      principalId: functionApp.identity.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    }
  }
]

resource appConfiguration 'Microsoft.AppConfiguration/configurationStores@2024-05-01' existing = if (!empty(appConfigurationStoreName)) {
  name: empty(appConfigurationStoreName) ? 'unused' : appConfigurationStoreName
}

resource appConfigurationRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appConfigurationStoreName)) {
  name: guid(resourceGroup().id, appConfigurationStoreName, functionApp.id, roles.appConfigurationDataOwner)
  scope: appConfiguration
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.appConfigurationDataOwner)
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = if (!empty(serviceBusNamespaceName)) {
  name: empty(serviceBusNamespaceName) ? 'unused' : serviceBusNamespaceName
}

resource serviceBusRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(serviceBusNamespaceName)) {
  name: guid(resourceGroup().id, serviceBusNamespaceName, functionApp.id, roles.serviceBusDataOwner)
  scope: serviceBus
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataOwner)
  }
}

output functionAppName string = functionApp.name
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}/'
output principalId string = functionApp.identity.principalId
output applicationInsightsName string = insights.name
