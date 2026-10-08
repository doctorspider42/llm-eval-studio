using LlmEval.Web.Components.Reports;
using LlmEval.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace LlmEval.Web.Api;

public static class ReportEndpoints
{
    /// <summary>
    /// Print-ready HTML reports. "PDF export" = this page + the browser's Save as PDF (auto-opened with print=true),
    /// which keeps the app free of PDF-library licensing and lets CSS do the typesetting.
    /// </summary>
    public static void MapReports(this WebApplication app)
    {
        app.MapGet("/reports/batches/{id:guid}", RenderBatch).ExcludeFromDescription();

        app.MapGet("/api/batches/{id:guid}/report", RenderBatch)
            .WithTags("Series")
            .WithSummary("Self-contained HTML report of a series (ranking, chart, per-case matrix, judge comments). " +
                         "?lang=pl|en&reveal=true&comments=true&answers=false&print=false&summaries=false&summaryId=. Summaries require a saved, current AI summary. Open in a browser and print to PDF.")
            .Produces(200, contentType: "text/html");
    }

    private static async Task<IResult> RenderBatch(Guid id, HttpContext ctx, ReportService reports, ILoggerFactory loggerFactory,
        string? lang = null, bool reveal = true, bool comments = true, bool answers = false, bool print = false,
        bool summaries = false, Guid? summaryId = null)
    {
        var language = I18n.Languages.Contains(lang) ? lang! : I18n.English;
        BatchReport report;
        try
        {
            report = await reports.BuildBatchReportAsync(id, reveal, ctx.RequestAborted, summaries, language, summaryId);
        }
        catch (EvalException ex)
        {
            return Results.Problem(statusCode: ex.StatusCode, title: ex.Message);
        }

        await using var renderer = new HtmlRenderer(ctx.RequestServices, loggerFactory);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<BatchReportView>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(BatchReportView.R)] = report,
                [nameof(BatchReportView.O)] = new ReportOptions(language, reveal, comments && !summaries, answers, print, summaries, report.AiSummary?.Id),
                [nameof(BatchReportView.BaseUrl)] = ctx.Request.Path.Value,
            }));
            return output.ToHtmlString();
        });
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
