var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("llmeval-pgdata")
    .WithPgWeb()
    .WithLifetime(ContainerLifetime.Persistent);

var db = postgres.AddDatabase("llmevaldb");

var web = builder.AddProject<Projects.LlmEval_Web>("web")
    .WithReference(db)
    .WaitFor(db)
    .WithExternalHttpEndpoints()
    .WithUrlForEndpoint("http", u => u.DisplayText = "LLM Eval (HTTP)")
    .WithUrlForEndpoint("https", u => u.DisplayText = "LLM Eval (HTTPS)");

web.WithUrl($"{web.GetEndpoint("http")}/scalar", "API docs (HTTP)")
    .WithUrl($"{web.GetEndpoint("https")}/scalar", "API docs (HTTPS)");

builder.Build().Run();
