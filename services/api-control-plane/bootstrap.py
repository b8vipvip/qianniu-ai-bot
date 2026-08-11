from __future__ import annotations

import os

import uvicorn

import app as control_plane
import bot_client_shop_binding
import bot_update_cache
import bot_update_prefetch
import bot_web_admin
import bot_web_bot_enabled
import bot_web_bot_qa
import bot_web_console
import bot_web_conversation_knowledge
import client_data_backup
import recharge_status_query
import runtime_embedding_guard
import runtime_routing_guard
import runtime_streaming_guard
import scheduled_deep_test_retry
import store_rule_sync
import wecom_bridge
import wecom_policy_migration
import wecom_settings
from wecom_crypto import install_on_bridge


runtime_routing_guard.install(control_plane)
runtime_streaming_guard.install(control_plane)
runtime_embedding_guard.install(control_plane)
scheduled_deep_test_retry.install(control_plane)
wecom_policy_migration.install(control_plane)
install_on_bridge(wecom_bridge)
control_plane.app.include_router(wecom_bridge.router)
control_plane.app.include_router(wecom_settings.router)
control_plane.app.include_router(recharge_status_query.router)
control_plane.app.include_router(bot_update_cache.router)
bot_web_console.install(control_plane)
bot_client_shop_binding.install(control_plane)
bot_web_bot_enabled.install(control_plane)
bot_web_admin.install(control_plane)
# Register Bot-only Q&A routes before the legacy all-chat routes. Starlette
# resolves the first matching route, while knowledge-management endpoints from
# the legacy module remain available below.
bot_web_bot_qa.install(control_plane)
bot_web_conversation_knowledge.install(control_plane)
client_data_backup.install(control_plane)
store_rule_sync.install(control_plane)


@control_plane.app.on_event("startup")
def initialize_control_plane_extensions() -> None:
    wecom_bridge.init_wecom_db()
    wecom_settings.init_wecom_settings_db()
    recharge_status_query.init_recharge_query_db()
    bot_web_console.init_bot_web_db()
    bot_client_shop_binding.init_db()
    bot_web_bot_enabled.init_db()
    bot_web_conversation_knowledge.init_db()
    client_data_backup.init_db()
    store_rule_sync.init_db()
    bot_update_cache.init_bot_update_cache()
    bot_update_prefetch.init_bot_update_prefetch()
    wecom_settings.apply_to_bridge(wecom_bridge)


@control_plane.app.on_event("shutdown")
def shutdown_control_plane_extensions() -> None:
    bot_update_prefetch.stop_bot_update_prefetch()
    bot_update_cache.stop_bot_update_cache()


if __name__ == "__main__":
    uvicorn.run(
        control_plane.app,
        host="0.0.0.0",
        port=int(os.getenv("PORT", "8080")),
        reload=False,
    )
