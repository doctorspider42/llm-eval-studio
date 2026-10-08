Run `dotnet run --project tests/LlmEval.Llm.Tests` from the repository root.

These checks compile the production LLM clients and entities directly and use a fake HTTP handler. No database, network requests or API keys are needed. They verify OpenRouter request formatting, authentication, model discovery, responses and errors, plus compatibility with the existing OpenAI client. A failed assertion exits with a nonzero status.
