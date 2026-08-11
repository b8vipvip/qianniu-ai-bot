FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    DATA_DIR=/data \
    PORT=8080

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates fonts-dejavu-core curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app.py bootstrap.py bot_client_shop_binding.py bot_update_cache.py bot_update_prefetch.py bot_web_console.py bot_web_bot_enabled.py bot_web_admin.py bot_web_bot_qa.py bot_web_bot_qa_logic.py bot_web_conversation_knowledge.py client_data_backup.py recharge_status_query.py runtime_routing_guard.py runtime_streaming_guard.py runtime_embedding_guard.py scheduled_deep_test_retry.py wecom_bridge.py wecom_crypto.py wecom_settings.py wecom_policy_migration.py ./
COPY static ./static

RUN useradd --create-home --uid 10001 appuser \
    && mkdir -p /data \
    && chown -R appuser:appuser /app /data

USER appuser
EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

CMD ["python", "bootstrap.py"]
