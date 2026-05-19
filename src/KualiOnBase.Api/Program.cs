using System.Threading.RateLimiting;
using KualiOnBase.Api.Controllers;
using KualiOnBase.Api.Models;
using KualiOnBase.Api.Services;
using Microsoft.AspNetCore.RateLimiting;
using Polly;
using Polly.Extensions.Http;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Program.cs is only startup wiring: config binding, dependency registration,
// middleware, and route registration. The behavior stays in CONTROLLERS/SERVICES.
builder.Host.UseSerilog((ctx, sp, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(sp)
    .Enrich.FromLogContext());

builder.Services.Configure<AppSettings>(builder.Configuration);

builder.Services.AddSingleton<Db>();
builder.Services.AddScoped<JobsService>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddScoped<ExportCallbackStore>();
builder.Services.AddScoped<JobEventLog>();
builder.Services.AddSingleton<EmailNotificationService>();

// Explicit Timeout on the named HttpClient: covers SendAsync (connect + headers)
// for GraphQL calls AND the ResponseHeadersRead phase of DownloadToFileAsync.
// Large bodies stream after headers so this doesn't cap download size; it DOES
// stop a stuck-handshake or dead-peer scenario from pinning a worker thread
// and hogging a socket forever. 60s is generous — Kuali's own GraphQL is
// typically sub-second, but export callback polling / export-start mutation
// can occasionally sit at the edge.
builder.Services.AddHttpClient<IKualiClient, KualiClient>(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(60);
    })
    .AddPolicyHandler(GetKualiRetryPolicy());

// Separate named client for Kuali signed-URL downloads — it must NOT carry
// the Bearer header (S3/CDN URLs are self-authenticating), and it must have
// a generous per-request timeout because PDF exports can be large. Pulled
// through IHttpClientFactory to prevent socket exhaustion from `new HttpClient()`.
builder.Services.AddHttpClient(KualiClient.DownloadHttpClientName, c =>
{
    // Covers handshake + headers; the body stream copy is gated by a
    // per-chunk idle timeout inside KualiClient.DownloadToFileAsync.
    c.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddHostedService<RetryWorker>();
builder.Services.AddHostedService<BackupCleanupWorker>();

// Partition rate limit by API key so one runaway caller can't starve the rest.
// 60 req/minute is well above any real Kuali workflow cadence and catches
// accidental loops (dashboard replay spam, workflow misconfigured to call us
// in a retry loop, etc.) before they become a DoS on ourselves.
//
// Note: app.UseRateLimiter() is wired AFTER ApiKeyMiddleware below, so by the
// time we partition here we've already rejected unauthenticated traffic.
// That means the partition key is always the (authenticated) bearer token —
// we use the same extractor the middleware does, so there is exactly one
// answer to "which token is this call from".
const string ImportRateLimiterPolicy = "import";
builder.Services.AddRateLimiter(opt =>
{
    opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opt.AddPolicy(ImportRateLimiterPolicy, httpCtx =>
    {
        var token = ApiKeyMiddleware.ExtractBearerToken(httpCtx.Request);
        var partition = token ?? (httpCtx.Connection.RemoteIpAddress?.ToString() ?? "anon");
        return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

var settings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppSettings>>().Value;

StartupValidator.ValidateOrThrow(
    settings,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup"));

app.Services.GetRequiredService<Db>().Migrate(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Db"));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Kuali's HTTP Action surfaces our response body as-is but stringifies JSON
// as "[object Object]", so returning plain-text errors keeps operator feedback
// readable in Kuali. This handler catches anything that escapes endpoint-level
// try/catch (e.g. DB failures during job insert) and does the same.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("GlobalException");
        logger.LogError(ex, "Unhandled exception at {Method} {Path}",
            context.Request.Method, context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            $"Internal error: {ex?.Message ?? "unknown"}");
    });
});

app.UseSerilogRequestLogging();

if (settings.Ui.Enabled)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

// Auth BEFORE rate-limit on purpose: the rate limiter partitions by bearer
// token, and without auth gating first an attacker can spray millions of
// unique Authorization values and explode the partition dictionary (DoS on
// memory). UseMiddleware<ApiKeyMiddleware> only guards /api/*, so static
// files / health / callback endpoints are unaffected.
app.UseMiddleware<ApiKeyMiddleware>();
app.UseRateLimiter();

ApiController.MapHealth(app);
ApiController.MapImport(app).RequireRateLimiting(ImportRateLimiterPolicy);
ApiController.MapJobs(app);
ApiController.MapKualiCallback(app);
// db-status is cheap and read-only; no rate-limit needed.
// kuali-probe-export actually calls Kuali + writes to temp disk — partition it
// with the same bucket as /api/kuali-onbase-import so an operator can't script
// the probe endpoint to hammer their Kuali export quota.
ApiController.MapDbStatus(app);
ApiController.MapProbeExport(app).RequireRateLimiting(ImportRateLimiterPolicy);

static IAsyncPolicy<HttpResponseMessage> GetKualiRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retry => TimeSpan.FromSeconds(Math.Pow(2, retry)));
}

app.Run();
