using System.Text.Json.Serialization;
using LlmEval.Web.Api;
using LlmEval.Web.Components;
using LlmEval.Web.Data;
using LlmEval.Web.Llm;
using LlmEval.Web.Services;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddDbContextFactory<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("llmevaldb")));
builder.EnrichNpgsqlDbContext<AppDbContext>();

#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient("llm", c => c.Timeout = TimeSpan.FromMinutes(10))
    .RemoveAllResilienceHandlers(); // LLM calls are slow & non-idempotent: no standard retry/timeout pipeline
#pragma warning restore EXTEXP0001
builder.Services.AddHttpClient("hf", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<ImportService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<ILlmClient, OpenAiClient>();
builder.Services.AddSingleton<ILlmClient, OpenRouterClient>();
builder.Services.AddSingleton<ILlmClient, AnthropicClient>();
builder.Services.AddSingleton<ILlmClient, OllamaClient>();
builder.Services.AddSingleton<ILlmClient, ClaudeCliClient>();
builder.Services.AddSingleton<ILlmClient, CodexCliClient>();
builder.Services.AddSingleton<LlmClientFactory>();

builder.Services.AddSingleton<EvalEvents>();
builder.Services.AddSingleton<ResultQueue>();
builder.Services.AddSingleton<JudgeQueue>();
builder.Services.AddSingleton<EvalService>();
builder.Services.AddHostedService<IterationRunner>();
builder.Services.AddHostedService<JudgeRunner>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<Toaster>();
builder.Services.AddScoped<Localizer>();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "LLM Eval API";
    doc.Info.Description = "Blind evaluation of LLM answers: users, providers, models, test cases, iterations, ratings.";
    return Task.CompletedTask;
}));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

await DbSeeder.InitializeAsync(app.Services);

if (app.Environment.IsDevelopment())
    foreach (var missing in I18n.MissingKeys())
        app.Logger.LogWarning("Missing translation {Key}", missing);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("LLM Eval API").WithTheme(ScalarTheme.DeepSpace));
app.MapEvalApi();
app.MapReports();
app.MapDefaultEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
