using Logging.API.Middleware;
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

/* Serilog */
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Enrich.WithSpan()
    .WriteTo.Console()
    .WriteTo.Seq(seqUrl)
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddTransient<SampleService>();

/* OpenTelemetry */
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

app.Run();