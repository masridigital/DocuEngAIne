# LLM providers

Tenant-scoped chat completion used later for document assist. Chat bodies are **not** stored in SQL. Provider selection is app configuration / Key Vault only — there is no settings write API.

## Providers

| Provider | Default model | Endpoint | Auth |
|---|---|---|---|
| **Ollama** (default) | `llama3.1` | `{Llm:Ollama:BaseUrl}/v1/chat/completions` | None (self-hosted) |
| **Together** | `meta-llama/Meta-Llama-3.1-8B-Instruct-Turbo` | `https://api.together.xyz/v1/chat/completions` | `Authorization: Bearer` |
| **Anthropic** | `claude-sonnet-4-20250514` | `https://api.anthropic.com/v1/messages` | `x-api-key` + `anthropic-version: 2023-06-01` |

Ollama and Together speak the OpenAI chat-completions JSON shape. Anthropic uses the Messages API: `system` is a top-level field; `user` / `assistant` turns go in `messages`.

## Configuration

`appsettings.json` / `appsettings.Development.json`:

```json
"Llm": {
  "Provider": "Ollama",
  "Model": "llama3.1",
  "Ollama": {
    "BaseUrl": "http://127.0.0.1:11434"
  }
}
```

Environment variables (double-underscore binds to the JSON section):

| Variable | Purpose |
|---|---|
| `LLM__Provider` | `Ollama` (default), `Together`, or `Anthropic` |
| `LLM__Model` | Optional model override. Empty uses the provider default. |
| `LLM__Ollama__BaseUrl` | Ollama host. Default `http://127.0.0.1:11434`. |
| `TogetherApiKey` | Together AI key (config or Key Vault secret name `TogetherApiKey`) |
| `AnthropicApiKey` | Anthropic key (config or Key Vault secret name `AnthropicApiKey`) |

Ollama has no API key. Together and Anthropic keys are never written to SQL and never logged (Authorization / `x-api-key` are `[redacted]`).

If Together or Anthropic is selected and the key is missing, the host still starts. `ChatAsync` throws `InvalidOperationException` naming the missing secret.

## API

Authenticated technician routes (same `RequireAuthorization` gate as documents):

| Method | Path | Body / response |
|---|---|---|
| GET | `/api/llm/config` | `{ provider, model }` — no secrets |
| POST | `/api/llm/chat` | `{ messages, model? }` → `{ content, model, provider }` |

`messages` is `{ role, content }[]`. A successful chat is audit-logged as `Llm.Chat` with provider and model only — not the prompt or completion.

## Local Ollama

```bash
ollama serve
ollama pull llama3.1
export LLM__Provider=Ollama
export LLM__Ollama__BaseUrl=http://127.0.0.1:11434
```
