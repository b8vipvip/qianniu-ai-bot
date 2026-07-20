from __future__ import annotations

import os

import uvicorn

import app as control_plane
from wecom_bridge import init_wecom_db, router as wecom_router


control_plane.app.include_router(wecom_router)


@control_plane.app.on_event("startup")
def initialize_wecom_bridge() -> None:
    init_wecom_db()


if __name__ == "__main__":
    uvicorn.run(
        control_plane.app,
        host="0.0.0.0",
        port=int(os.getenv("PORT", "8080")),
        reload=False,
    )
