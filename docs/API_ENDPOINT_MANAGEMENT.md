# API Endpoint Management

## Diagnosis rules

The Bot sends a real OpenAI-compatible `POST /chat/completions` request when an endpoint is tested.

- `HTTP 401/403`: key, account permission, IP allowlist, or provider authorization problem.
- `HTTP 404`: BaseUrl/path problem, often a missing or duplicated `/v1`.
- `HTTP 429`: provider rate limit, quota, balance, or upstream overload.
- `HTTP 500-504`: relay/provider or its upstream model service is unavailable.
- timeout: relay congestion, upstream queueing, or network reachability problem.
- HTTP 200 with no `choices[0].message.content`: incompatible response format.

A successful full test verifies HTTP communication, Bearer authentication, request acceptance, model response parsing, and a random echo marker.

## Guided configuration

The API settings page provides:

- a resizable settings window;
- wrapping toolbar buttons;
- separate add/edit confirmation form;
- masked keys in the endpoint list;
- paste recognition for strict JSON, truncated JSON, code snippets, and loose text;
- editable confirmation before adding an endpoint;
- complete selected/all endpoint tests.

Recognized fields include common variants of BaseUrl, ApiKey/token, direct model values, and model-map keys.

## Secret handling

API keys remain in the local `data/params.db`. Do not commit, upload, screenshot, or paste real keys into public issue/PR discussions. Exported endpoint configuration can contain credentials and must be treated as a secret.
