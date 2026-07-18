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

## Incoming message safety

The production Bot currently sends text-only OpenAI-compatible requests. Image, video, voice, file, location, empty, and unknown messages are not sent to the model and are never auto-replied. The Bot panel records a skipped task and reason instead.

Historical or unread messages whose timestamp predates the current Bot process are skipped. Duplicate message IDs are ignored, and when Qianniu returns several unread buyer messages in one batch, only the newest message for that buyer is processed.

Background new-message notifications do not change the active visible buyer. Before any automatic or manual resend, the Bot serializes the send operation, queries Qianniu for the actual active conversation, opens the target buyer when required, and blocks sending unless the target buyer is confirmed.

The product inquiry area deduplicates items by item ID, then URL, then title and price.

Vision support should reuse an endpoint's BaseUrl and ApiKey with an explicitly configured vision-capable model. It also requires a separate, authenticated image-download pipeline and multimodal request payload; it must not be inferred from a text-only model name.

## Secret handling

API keys remain in the local `data/params.db`. Do not commit, upload, screenshot, or paste real keys into public issue/PR discussions. Exported endpoint configuration can contain credentials and must be treated as a secret.
