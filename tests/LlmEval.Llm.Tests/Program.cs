using System.Net;
using System.Text.Json.Nodes;
using LlmEval.Web.Data;
using LlmEval.Web.Llm;

// Run with: dotnet run --project tests/LlmEval.Llm.Tests
// Fake HTTP transport: no API credentials, paid requests or database required.
const string completion = """
    {"choices":[{"message":{"content":"answer"}}],"usage":{"prompt_tokens":12,"completion_tokens":7}}
    """;
var oldRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
var oldOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
try
{
    Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "router-env-test");
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "openai-env-test");
    var provider = new Provider { Name = "Router", Type = ProviderType.OpenRouter, ApiKey = "saved-test" };
    var model = new LlmModel { ModelId = "vendor/model", DisplayName = "Model", Temperature = 0.25, MaxTokens = 123 };

    using var transport = new FakeHttpFactory(async req =>
    {
        Check(req.RequestUri!.ToString() == "https://openrouter.ai/api/v1/chat/completions", "default completion URL");
        Check(req.Method == HttpMethod.Post, "completion method");
        Check(req.Headers.Authorization?.ToString() == "Bearer saved-test", "saved key takes precedence");
        Check(req.Content!.Headers.ContentType!.MediaType == "application/json", "JSON content type");
        var body = JsonNode.Parse(await req.Content.ReadAsStringAsync())!;
        Check(body["model"]!.GetValue<string>() == "vendor/model", "full model ID");
        Check(body["messages"]![0]!["role"]!.GetValue<string>() == "system", "OpenRouter system role");
        Check(body["messages"]![0]!["content"]!.GetValue<string>() == "instructions", "system prompt");
        Check(body["messages"]![1]!["content"]!.GetValue<string>() == "question", "user prompt");
        Check(body["temperature"]!.GetValue<double>() == 0.25, "temperature");
        Check(body["max_tokens"]!.GetValue<int>() == 123 && body["max_completion_tokens"] is null, "OpenRouter token limit");
        return Json(completion);
    });
    var router = new OpenRouterClient(transport);
    var openAi = new OpenAiClient(transport);
    var factory = new LlmClientFactory([router, openAi]);
    Check(ReferenceEquals(factory.For(ProviderType.OpenRouter), router), "provider dispatch");
    var response = await router.CompleteAsync(new(provider, model, "instructions", "question"), default);
    Check(response == new LlmResponse("answer", 12, 7), "answer and token usage");
    Console.WriteLine("PASS: OpenRouter completion, authentication, parameters, usage and dispatch");

    provider.ApiKey = " ";
    provider.BaseUrl = "http://localhost:9999/custom/v1/";
    transport.Respond = req =>
    {
        Check(req.Method == HttpMethod.Get && req.RequestUri!.ToString() == "http://localhost:9999/custom/v1/models", "custom discovery URL");
        Check(req.Headers.Authorization?.ToString() == "Bearer router-env-test", "OpenRouter environment key");
        return Task.FromResult(Json("""{"data":[{"id":"z/model"},{"id":"a/model"}]}"""));
    };
    var models = await router.ListModelsAsync(provider, default);
    Check(models.SequenceEqual(["a/model", "z/model"]), "sorted model discovery");
    Console.WriteLine("PASS: custom URL, environment fallback and model discovery");

    model.Temperature = null;
    model.MaxTokens = null;
    transport.Respond = async req =>
    {
        Check(req.RequestUri!.ToString() == "http://localhost:9999/custom/v1/chat/completions", "custom completion URL");
        var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync())!;
        Check(body["messages"]!.AsArray().Count == 1 && body["messages"]![0]!["role"]!.GetValue<string>() == "user", "optional system prompt");
        Check(body["temperature"] is null && body["max_tokens"] is null, "optional generation parameters");
        return Json("""{"choices":[{"message":{"content":"answer"}}]}""");
    };
    response = await router.CompleteAsync(new(provider, model, null, "question"), default);
    Check(response.InputTokens is null && response.OutputTokens is null, "optional usage");
    Console.WriteLine("PASS: custom completion URL and optional fields");

    transport.Respond = _ => Task.FromResult(Json("""{"error":{"message":"insufficient credits"}}"""));
    await ExpectError(() => router.CompleteAsync(new(provider, model, null, "question"), default), "insufficient credits");
    transport.Respond = _ => Task.FromResult(Json("""{"error":{"message":"unauthorized"}}""", HttpStatusCode.Unauthorized));
    await ExpectError(() => router.ListModelsAsync(provider, default), "HTTP 401");
    Console.WriteLine("PASS: HTTP errors and HTTP 200 error payloads");

    Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);
    transport.Respond = _ => throw new Exception("Must not send a request without an OpenRouter key");
    await ExpectError(() => router.ListModelsAsync(provider, default), "OPENROUTER_API_KEY");
    Console.WriteLine("PASS: missing OpenRouter key does not use the OpenAI key");

    provider = new Provider { Name = "OpenAI", Type = ProviderType.OpenAI };
    model.MaxTokens = 42;
    transport.Respond = async req =>
    {
        Check(req.RequestUri!.ToString() == "https://api.openai.com/v1/chat/completions", "OpenAI default URL");
        Check(req.Headers.Authorization?.ToString() == "Bearer openai-env-test", "OpenAI environment key");
        var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync())!;
        Check(body["messages"]![0]!["role"]!.GetValue<string>() == "developer", "OpenAI developer role preserved");
        Check(body["max_completion_tokens"]!.GetValue<int>() == 42 && body["max_tokens"] is null, "OpenAI token limit preserved");
        return Json(completion);
    };
    await openAi.CompleteAsync(new(provider, model, "instructions", "question"), default);
    Console.WriteLine("PASS: OpenAI compatibility regression");
}
finally
{
    Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", oldRouterKey);
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", oldOpenAiKey);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception($"Failed: {description}");
}

static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

static async Task ExpectError(Func<Task> action, string fragment)
{
    try { await action(); }
    catch (LlmException ex) when (ex.Message.Contains(fragment)) { return; }
    throw new Exception($"Expected LlmException containing: {fragment}");
}

sealed class FakeHttpFactory : HttpMessageHandler, IHttpClientFactory
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; }
    private readonly HttpClient _client;
    public FakeHttpFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        Respond = respond;
        _client = new HttpClient(this, disposeHandler: false);
    }
    public HttpClient CreateClient(string name) => _client;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Respond(request);
    protected override void Dispose(bool disposing)
    {
        if (disposing) _client.Dispose();
        base.Dispose(disposing);
    }
}
