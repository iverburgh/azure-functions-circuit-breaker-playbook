// Lets the breaker read alert rules and their alerts in one resource group. Resource Graph only
// returns what the identity may read: a rule without this role looks exactly like a rule that does not exist.
targetScope = 'resourceGroup'

param principalId string

@description('Scopes the assignment name, so two breakers can read the same resource group.')
param functionAppId string

var monitoringReader = '43d0d8ad-25c7-4714-9337-8ba259a9fe05'

resource monitoringReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, functionAppId, monitoringReader)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringReader)
  }
}
