# Runner regression checks

Run `dotnet run --project tests/LlmEval.Runner.Tests` from the repository root.

The checks use a temporary in-memory SQLite database and a controlled fake LLM client.
No running app, credentials, paid calls, or PostgreSQL instance are required.

They exercise queued and active cancellation, restart behavior for cancelled jobs,
retry, late provider responses, judge ratings, provider costs, and suppression of
automatic judging when an iteration or series is stopped.
