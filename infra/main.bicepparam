using 'main.bicep'

param name = 'cb-demo'
param apiClientId = '<client-id-of-the-breaker-api-app-registration>'

// Uncomment to let the breaker flip feature flags or Service Bus subscriptions:
// param appConfigurationStoreName = 'my-app-configuration'
// param serviceBusNamespaceName = 'my-service-bus'

// Uncomment when the management console is hosted on its own origin:
// param allowedOrigins = ['https://circuits.example.com']
