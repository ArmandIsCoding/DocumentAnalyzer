using System.Text;
using InvoiceExplorer.Core;
using InvoiceExplorer.Web.Components;

ApplicationCulture.UseEnglish();
var builder = WebApplication.CreateBuilder(args);
var settings = Settings.Load(Settings.FindRoot());
var repository = new InvoiceRepository(settings.DatabasePath);
repository.Initialize();
builder.Services.AddSingleton(repository);
builder.Services.AddSingleton(settings);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var app = builder.Build();
// This portable review tool intentionally serves the local operator only. Add enterprise authentication
// before adapting it for shared hosting; a URL override must not accidentally expose invoice data.
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is { } address && !System.Net.IPAddress.IsLoopback(address))
    { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
    await next();
});
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("en-US")
    .AddSupportedCultures("en-US")
    .AddSupportedUICultures("en-US"));
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/downloads/business", (string? batch, bool? demo, string? shape, InvoiceRepository repo) =>
    Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(shape == "wide" ? BusinessExport.WideCsv(repo, string.IsNullOrWhiteSpace(batch) ? null : batch, demo == true) : BusinessExport.Csv(repo, string.IsNullOrWhiteSpace(batch) ? null : batch, demo == true))).ToArray(), "text/csv; charset=utf-8", "invoice-business-report.csv"));
app.MapGet("/sources/executions/{id:long}", (long id, InvoiceRepository repo, Settings configuration) =>
{
    var execution = repo.GetExecution(id);
    if (execution is null || execution.IsDemo || string.IsNullOrEmpty(execution.ArtifactPath)) return Results.NotFound();
    var root = Path.GetFullPath(configuration.Storage.ArtifactDirectory) + Path.DirectorySeparatorChar;
    var source = Path.GetFullPath(execution.ArtifactPath);
    if (!source.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(source)) return Results.NotFound();
    return Results.File(source, "application/pdf", enableRangeProcessing: true);
});
app.MapGet("/downloads/runs/{id:long}/{format}", (long id, string format, InvoiceRepository repo) =>
{
    var details = repo.GetRun(id);
    if (details is null || details.Run.Status != "Succeeded") return Results.NotFound();
    var demo = details.Run.IsDemo ? "demo-" : "";
    return format switch
    {
        "csv" => Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(details.Csv)).ToArray(), "text/csv; charset=utf-8", $"{demo}run-{id}.csv"),
        "json" => Results.File(Encoding.UTF8.GetBytes(details.RawJson), "application/json", $"{demo}run-{id}.json"),
        _ => Results.NotFound()
    };
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

