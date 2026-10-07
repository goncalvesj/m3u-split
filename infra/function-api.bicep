targetScope = 'resourceGroup'

@minLength(2)
@maxLength(32)
param functionAppName string

param appServicePlanName string

@description('Use the region of the existing App Service plan.')
param location string

@minLength(3)
@maxLength(24)
@description('Use the storage account that the owner approved.')
param storageAccountName string

resource appServicePlan 'Microsoft.Web/serverfarms@2025-03-01' existing = {
  name: appServicePlanName
}

resource storage 'Microsoft.Storage/storageAccounts@2025-08-01' existing = {
  name: storageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-08-01' existing = {
  parent: storage
  name: 'default'
}

var hostContainerNames = ['azure-webjobs-hosts', 'azure-webjobs-secrets']
var dataContainerNames = ['online-imports', 'online-profiles', 'online-feeds']
var dataQueueNames = ['online-imports', 'online-imports-poison', 'online-profile-refreshes', 'online-profile-refreshes-poison']

resource hostContainers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-08-01' = [
  for containerName in hostContainerNames: {
    parent: blobService
    name: containerName
    properties: {
      publicAccess: 'None'
    }
  }
]

resource dataContainers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-08-01' = [
  for containerName in dataContainerNames: {
    parent: blobService
    name: containerName
    properties: {
      publicAccess: 'None'
    }
  }
]

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2025-08-01' existing = {
  parent: storage
  name: 'default'
}

resource dataQueues 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-08-01' = [
  for queueName in dataQueueNames: {
    parent: queueService
    name: queueName
    properties: {}
  }
]

resource functionApi 'Microsoft.Web/sites@2025-03-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    reserved: true
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      http20Enabled: true
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      use32BitWorkerProcess: false
      appSettings: [
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storage.name
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'OnlinePlaylistStorage__blobServiceUri'
          value: storage.properties.primaryEndpoints.blob
        }
        {
          name: 'OnlinePlaylistStorage__queueServiceUri'
          value: storage.properties.primaryEndpoints.queue
        }
        {
          name: 'OnlinePlaylistStorage__credential'
          value: 'managedidentity'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
      ]
    }
  }
  dependsOn: [
    hostContainers
    dataContainers
    dataQueues
  ]
}

resource scmPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: functionApi
  name: 'scm'
  properties: {
    allow: false
  }
}

resource ftpPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: functionApi
  name: 'ftp'
  properties: {
    allow: false
  }
}

var hostBlobRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var dataBlobRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var dataQueueRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')

resource hostBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (containerName, index) in hostContainerNames: {
    scope: hostContainers[index]
    name: guid(hostContainers[index].id, functionApi.id, hostBlobRole)
    properties: {
      roleDefinitionId: hostBlobRole
      principalId: functionApi.identity.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource dataBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (containerName, index) in dataContainerNames: {
    scope: dataContainers[index]
    name: guid(dataContainers[index].id, functionApi.id, dataBlobRole)
    properties: {
      roleDefinitionId: dataBlobRole
      principalId: functionApi.identity.principalId
      principalType: 'ServicePrincipal'
    }
  }
]

resource dataQueueAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for (queueName, index) in dataQueueNames: {
    scope: dataQueues[index]
    name: guid(dataQueues[index].id, functionApi.id, dataQueueRole)
    properties: {
      roleDefinitionId: dataQueueRole
      principalId: functionApi.identity.principalId
      principalType: 'ServicePrincipal'
    }
  }
]
