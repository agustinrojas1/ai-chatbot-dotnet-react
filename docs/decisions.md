# Decisions

Short ADRs: context, decision, alternatives discarded.

## ADR-001: Monorepo with Core / Infrastructure / Api split
- **Context:** The agent logic (tools, tracing, language detection) should be testable without the web host.
- **Decision:** `Chatbot.Core` (domain, agent, tools, no web deps), `Chatbot.Infrastructure` (EF Core, LLM client, seed), `Chatbot.Api` (endpoints, DI). Revisit and merge Core + Infrastructure if the split proves excessive.
- **Alternatives:** single project (simpler, but the agent would be tied to ASP.NET and EF).
