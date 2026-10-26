using Azure.Core;
using Azure.Identity;
using CircuitBreaker.Function.Http;
using CircuitBreaker.Function.Observability;
using CircuitBreaker.Function.Switches;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using Azure.Monitor.OpenTelemetry.Exporter;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMetrics();
builder.Services.AddSingleton<CircuitMetrics>();
builder.Services.AddSingleton<RequestAuthorizer>();
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddSingleton<ICircuitSwitchProvider, CircuitSwitchProvider>();
builder.Services.Configure<SwitchOptions>(builder.Configuration.GetSection("Switches"));
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection("Api"));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Security"));

var security = builder.Configuration.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();
if (security.Mode == SecurityMode.Development && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Security:Mode 'Development' grants every caller every role and is refused outside the Development environment.");
}

// Logs already flow to Application Insights through the Functions host; export only the metrics.
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
        .AddMeter(CircuitMetrics.MeterName)
        .AddAzureMonitorMetricExporter(exporter => exporter.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]));
}

builder.Build().Run();
