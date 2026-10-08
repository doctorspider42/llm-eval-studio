var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("llmeval-pgdata")
    .WithPgWeb()
    .WithLifetime(ContainerLifetime.Persistent);

var db = postgres.AddDatabase("llmevaldb");

builder.AddProject<Projects.LlmEval_Web>("web")
    .WithReference(db)
    .WaitFor(db)
    .WithExternalHttpEndpoints()
    .WithUrlForEndpoint("http", u => u.DisplayText = "LLM Eval")
    .WithUrl("/scalar", "API docs");

builder.Build().Run();
