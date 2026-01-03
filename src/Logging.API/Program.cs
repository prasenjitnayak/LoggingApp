using Azure.Monitor.OpenTelemetry.AspNetCore;
using Logging.API.Middleware;
using Logging.API.Service;
using Microsoft.ApplicationInsights.Extensibility;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Context;
using Serilog.Enrichers.Span;

//initializes the ASP.NET Core app by setting up configuration, dependency injection, logging, and environment //information in a single unified builder.
var builder = WebApplication.CreateBuilder(args);

// Enables controller-based Web APIs (MVC)
builder.Services.AddControllers();

// Enables endpoint metadata discovery for Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();

// Enables Swagger/OpenAPI document generation and UI
builder.Services.AddSwaggerGen();

// Registers SampleService with transient lifetime (new instance per request)
builder.Services.AddTransient<SampleService>();

// -------------------- Mode switch --------------------
// Set in Azure App Settings / env vars:
// Observability__UseAzureNative=true
var useAzureNative = builder.Configuration.GetValue<bool>("Observability:UseAzureNative");

// -------------------- Serilog --------------------
// Local: Console + Seq
// Azure: Console (platform capture) OR add AppInsights sink if you want Serilog logs in AppInsights too
var seqUrl = builder.Configuration["Seq:ServerUrl"] ?? "http://seq";

//Creates a Serilog logger configuration builder used to define log levels, enrichers, and sinks.
var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Information()  //Sets the minimum log level to Information, ignoring Debug and Verbose logs.
    .Enrich.FromLogContext() // Enriches logs with contextual properties (e.g., RequestId, UserId, custom scoped values).
    .Enrich.WithSpan() // adds TraceId/SpanId into logs (via Activity.Current)
    .WriteTo.Console(); //Sends logs to the console output, for: Docker containers, Kubernetes, Azure App Service logs, Local development

if (!useAzureNative)
{
    // Local docker-compose
    loggerConfig = loggerConfig.WriteTo.Seq(seqUrl);
}

// Optional (Azure): If you want Serilog logs in Application Insights as well,
// add Serilog.Sinks.ApplicationInsights and uncomment:
// Check whether Azure-native observability is enabled (typically via appsettings or App Service settings)
if (useAzureNative)
{
    // Add Application Insights as a Serilog sink and extend the existing logger configuration
    loggerConfig = loggerConfig.WriteTo.ApplicationInsights(

        // Build a temporary service provider to resolve Application Insights telemetry configuration
        builder.Services.BuildServiceProvider()

                     // Retrieve the TelemetryConfiguration required to send telemetry to Azure Monitor
                     .GetRequiredService<TelemetryConfiguration>(),

        // Convert Serilog log events into Application Insights "Trace" telemetry
        TelemetryConverter.Traces);
}

// Creates the global Serilog logger from the configured settings and instructs the ASP.NET Core host
// to use Serilog as the primary logging provider for the application.
Log.Logger = loggerConfig.CreateLogger(); // Create the Serilog logger instance based on the configured logger settings
builder.Host.UseSerilog();  // Replace the default ASP.NET Core logging system with Serilog

// -------------------- OpenTelemetry (single pipeline) --------------------
// Registers OpenTelemetry and configures the service resource metadata so all traces, metrics,
// and logs are associated with the "logging-api" service and its version.
var otel = builder.Services.AddOpenTelemetry() // Register OpenTelemetry services with the dependency injection container
    .ConfigureResource(r => r.AddService( // Configure resource attributes that describe this service in telemetry backends
        serviceName: "logging-api", // Logical name of the service used in tracing systems (Jaeger, Azure Monitor, etc.)
        serviceVersion: "1.0.0"));  // Version of the service, useful for deployment and release tracking

// Configures OpenTelemetry tracing by enabling request, dependency, and custom spans,
// enriching HTTP telemetry, always sampling traces, and exporting traces either to
// a local OTLP collector or Azure-native backends based on environment.
// ---- Tracing
otel.WithTracing(t =>
{
    // Configure the sampler to capture 100% of traces (no sampling drop)
    t.SetSampler(new AlwaysOnSampler());

    // Enable automatic tracing for incoming ASP.NET Core HTTP requests
    t.AddAspNetCoreInstrumentation(o =>
    {
        // Capture unhandled exceptions as span events
        o.RecordException = true;

        // Enrich request spans with HTTP request content length
        o.EnrichWithHttpRequest = (activity, request) =>
            activity.SetTag("http.request_content_length", request.ContentLength);

        // Enrich response spans with HTTP response content length
        o.EnrichWithHttpResponse = (activity, response) =>
            activity.SetTag("http.response_content_length", response.ContentLength);
    });

    // Enable tracing for outgoing HTTP calls made via HttpClient
    t.AddHttpClientInstrumentation(o => o.RecordException = true);

    // Register custom ActivitySource names for manual spans in application code
    // (ActivitySource names must exactly match those used in ActivitySource creation)
    t.AddSource("SampleController");
    t.AddSource("SampleService");

    // Configure exporter only when not running in Azure-native mode
    if (!useAzureNative)
    {
        // Resolve the OTLP endpoint for local environments (e.g., OpenTelemetry Collector / Jaeger)
        var otlpEndpoint = builder.Configuration["Otel:OtlpEndpoint"] ?? "http://otel-collector:4317";

        // Export traces using the OTLP protocol to the configured collector endpoint
        t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    }
});

// Configures OpenTelemetry metrics to collect HTTP request, dependency, and runtime metrics,
// and exposes them via a Prometheus-compatible /metrics endpoint for scraping by
// Prometheus or Azure Managed Prometheus.
// ---- Metrics
otel.WithMetrics(m =>
{
    // Collect metrics for incoming ASP.NET Core HTTP requests
    m.AddAspNetCoreInstrumentation()

     // Collect metrics for outgoing HTTP requests made via HttpClient
     .AddHttpClientInstrumentation()

     // Collect runtime metrics such as GC, CPU usage, thread pool, and memory
     .AddRuntimeInstrumentation()

     // Expose a Prometheus-compatible /metrics endpoint for scraping
     // (used by Prometheus or Azure Managed Prometheus)
     .AddPrometheusExporter();
});

// Enables Azure-native OpenTelemetry export so traces and metrics are automatically sent
// to Azure Monitor / Application Insights using the configured connection string.
// ---- Azure export (App Insights / Azure Monitor)
if (useAzureNative)
{
    // Requires the Azure.Monitor.OpenTelemetry.AspNetCore NuGet package
    // Enables seamless integration between OpenTelemetry and Azure Monitor
    // Uses APPLICATIONINSIGHTS_CONNECTION_STRING from Azure App Service settings
    otel.UseAzureMonitor();
}

// Create the WebApplication instance by compiling all builder configurations
var app = builder.Build();

// Put trace/span into response headers (your middleware)
app.UseMiddleware<SampleMiddleware>();

// Enables Swagger middleware only in the Development environment,
// exposing interactive API documentation and testing UI.
// Check if the application is running in the Development environment
if (app.Environment.IsDevelopment())
{
    // Enable middleware that serves the generated OpenAPI specification (swagger.json)
    app.UseSwagger();

    // Enable Swagger UI for interactive API exploration and testing
    app.UseSwaggerUI();
}

// Enable Serilog request logging to capture one log entry per HTTP request
app.UseSerilogRequestLogging(option =>
{
    option.EnrichDiagnosticContext = (diag, http) =>
    {
        diag.Set("RequestHost", http.Request.Host.Value);
        diag.Set("RequestHost", http.Request.Scheme);
        diag.Set("ClientIP", http.Connection.RemoteIpAddress?.ToString());
        diag.Set("UserAgent", http.Request.Headers.UserAgent.ToString());
        diag.Set("QueryString", http.Request.QueryString.Value);
    };
});

// Expose the /metrics endpoint for Prometheus-compatible metric scraping
app.MapPrometheusScrapingEndpoint();

// Enable routing to controller-based API endpoints
app.MapControllers();

// Run the ASP.NET Core application and block the current thread until shutdown
app.Run();



/*using Logging.API.Middleware;
using Logging.API.Service;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();

// Read from environment variables provided by docker-compose
var seqUrl = builder.Configuration["Seq:ServerUrl"] ?? "http://seq";
var otlpEndpoint = builder.Configuration["Otel:OtlpEndpoint"] ?? "http://otel-collector:4317";

//erilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithSpan()
    .WriteTo.Console()
    .WriteTo.Seq(seqUrl)
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddTransient<SampleService>();

//OpenTelemetry
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r =>
    {
        r.AddService("logging-api", serviceVersion: "1.0.0");
    })
    .WithTracing(t =>
    {
        t.SetSampler(new AlwaysOnSampler()); // ✅ force traces

        t.AddAspNetCoreInstrumentation(options =>
        {
            options.RecordException = true;
            options.EnrichWithHttpRequest = (activity, request) =>
            {
                activity.SetTag("http.request_content_length", request.ContentLength);
            };
            options.EnrichWithHttpResponse = (activity, response) =>
            {
                activity.SetTag("http.response_content_length", response.ContentLength);
            };
        });

        t.AddHttpClientInstrumentation(option =>
        {
            option.RecordException = true;
            option.EnrichWithHttpWebRequest = (activity, request) =>
            {
            };
            option.EnrichWithHttpWebResponse = (activity, response) =>
            {

            };
        });

        t.AddSource("SampleController");
        t.AddSource("SampleService");

        t.AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(builder.Configuration["Otel:OtlpEndpoint"] ?? "http://otel-collector:4317");
        });
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter();
    });

var app = builder.Build();

app.UseMiddleware<SampleMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();
// Expose /metrics (Prometheus scrapes this)
app.MapPrometheusScrapingEndpoint(); // default: /metrics

app.MapControllers();

app.Run();*/