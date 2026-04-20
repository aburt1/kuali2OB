using KualiOnBase.Api.Auth;
using KualiOnBase.Api.BackgroundServices;
using KualiOnBase.Api.Data;
using KualiOnBase.Api.Endpoints;
using KualiOnBase.Api.Options;
using KualiOnBase.Api.Services;
using KualiOnBase.Api.Services.Import;
using KualiOnBase.Api.Services.Kuali;
using Polly;
using Polly.Extensions.Http;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, sp, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(sp)
    .Enrich.FromLogContext());

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<KualiOptions>(builder.Configuration.GetSection(KualiOptions.SectionName));
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection(RetryOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<UiOptions>(builder.Configuration.GetSection(UiOptions.SectionName));

builder.Services.AddSingleton<Db>();
builder.Services.AddScoped<RetryQueue>();
builder.Services.AddScoped<IBackupService, BackupService>();
builder.Services.AddScoped<IImportOrchestrator, ImportOrchestrator>();
builder.Services.AddScoped<IExportCallbackStore, ExportCallbackStore>();
builder.Services.AddScoped<IJobEventLog, JobEventLog>();

builder.Services.AddHttpClient<IKualiClient, KualiClient>()
    .AddPolicyHandler(GetKualiRetryPolicy());

builder.Services.AddHostedService<RetryWorker>();
builder.Services.AddHostedService<BackupCleanupWorker>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.Services.GetRequiredService<Db>().Migrate();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Kuali's HTTP Action surfaces our response body as-is but stringifies JSON
// as "[object Object]", so returning plain-text errors keeps operator feedback
// readable in Kuali. This handler catches anything that escapes endpoint-level
// try/catch (e.g. DB failures during retry-queue insert) and does the same.
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

var uiEnabled = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<UiOptions>>().Value.Enabled;
if (uiEnabled)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseMiddleware<ApiKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
ImportEndpoint.Map(app);
JobsEndpoint.Map(app);
JobFilesEndpoint.Map(app);
KualiExportCallbackEndpoint.Map(app);

static IAsyncPolicy<HttpResponseMessage> GetKualiRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retry => TimeSpan.FromSeconds(Math.Pow(2, retry)));
}

app.Run();

public partial class Program { }
