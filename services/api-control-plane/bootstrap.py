from __future__ import annotations

import os

import uvicorn

import app as control_plane
import runtime_routing_guard
import wecom_bridge
import wecom_settings
from wecom_crypto import install_on_bridge


runtime_routing_guard.install(control_plane)
install_on_bridge(wecom_bridge)
control_plane.app.include_router(wecom_bridge.router)
control_plane.app.include_router(wecom_settings.router)


@control_plane.app.on_event("startup")
def initialize_wecom_bridge() -> None:
    wecom_bridge.init_wecom_db()
    wecom_settings.init_wecom_settings_db()
    wecom_settings.apply_to_bridge(wecom_bridge)


if __name__ == "__main__":
    uvicorn.run(
        control_plane.app,
        host="0.0.0.0",
        port=int(os.getenv("PORT", "8080")),
        reload=False,
    )
