# syntax=docker/dockerfile:1.7
FROM python:3.11-slim

ARG APT_MIRROR=""
ARG APT_SECURITY_MIRROR=""
ARG PIP_INDEX_URL="https://pypi.org/simple"
ARG PIP_TRUSTED_HOST=""
ARG PIP_DEFAULT_TIMEOUT="120"
ARG PIP_RETRIES="8"
ARG SING_BOX_VERSION="1.13.19"
ARG SING_BOX_SHA256="ef88a9e577d474210867bd708933d042e9b70106529df2656182c9db90106aa1"

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    DATA_DIR=/data \
    PORT=8080

RUN set -eux; \
    if [ -n "$APT_MIRROR" ]; then \
      printf '%s\n' \
        "Types: deb" \
        "URIs: ${APT_MIRROR}" \
        "Suites: bookworm bookworm-updates" \
        "Components: main" \
        "Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg" \
        > /etc/apt/sources.list.d/debian.sources; \
      if [ -n "$APT_SECURITY_MIRROR" ]; then \
        printf '%s\n' \
          "Types: deb" \
          "URIs: ${APT_SECURITY_MIRROR}" \
          "Suites: bookworm-security" \
          "Components: main" \
          "Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg" \
          > /etc/apt/sources.list.d/debian-security.sources; \
      fi; \
    fi; \
    printf '%s\n' \
      'Acquire::Retries "8";' \
      'Acquire::http::Timeout "60";' \
      'Acquire::https::Timeout "60";' \
      > /etc/apt/apt.conf.d/99qianniu-network; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates fonts-dejavu-core curl; \
    rm -rf /var/lib/apt/lists/*

RUN set -eux; \
    archive="/tmp/sing-box.tar.gz"; \
    directory="/tmp/sing-box-${SING_BOX_VERSION}-linux-amd64"; \
    url="https://github.com/SagerNet/sing-box/releases/download/v${SING_BOX_VERSION}/sing-box-${SING_BOX_VERSION}-linux-amd64.tar.gz"; \
    curl -fL --retry 8 --retry-all-errors --retry-delay 2 --connect-timeout 15 --max-time 1500 "$url" -o "$archive"; \
    echo "${SING_BOX_SHA256}  ${archive}" | sha256sum -c -; \
    tar -xzf "$archive" -C /tmp; \
    install -m 0755 "${directory}/sing-box" /usr/local/bin/sing-box; \
    /usr/local/bin/sing-box version; \
    rm -rf "$archive" "$directory"

WORKDIR /app
COPY requirements.txt .
RUN --mount=type=cache,target=/root/.cache/pip,sharing=locked \
    set -eux; \
    trusted_args=""; \
    if [ -n "$PIP_TRUSTED_HOST" ]; then trusted_args="--trusted-host $PIP_TRUSTED_HOST"; fi; \
    python -m pip install \
      --disable-pip-version-check \
      --retries "$PIP_RETRIES" \
      --timeout "$PIP_DEFAULT_TIMEOUT" \
      --index-url "$PIP_INDEX_URL" \
      $trusted_args \
      -r requirements.txt

COPY app.py bootstrap.py bot_client_shop_binding.py bot_update_cache.py bot_update_prefetch.py bot_update_progress.py bot_update_push.py bot_web_console.py bot_web_bot_enabled.py bot_web_admin.py bot_web_bot_qa.py bot_web_bot_qa_logic.py bot_web_conversation_knowledge.py chat2api_runtime_guard.py client_data_backup.py console_cache_guard.py deep_test_guard.py github_vless_proxy.py message_processing_traces.py runtime_shop_ai_proxy.py store_rule_sync.py recharge_status_query.py runtime_routing_guard.py runtime_streaming_guard.py runtime_embedding_guard.py scheduled_deep_test_retry.py version_update_admin.py wecom_bridge.py wecom_crypto.py wecom_settings.py wecom_policy_migration.py ./
COPY static ./static

RUN useradd --create-home --uid 10001 appuser \
    && mkdir -p /data \
    && chown -R appuser:appuser /app /data

USER appuser

EXPOSE 8080

CMD ["python", "bootstrap.py"]
