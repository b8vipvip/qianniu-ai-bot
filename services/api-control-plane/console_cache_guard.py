from __future__ import annotations

from typing import Any


_CONSOLE_CACHE_CONTROL = "no-store, no-cache, must-revalidate, max-age=0"


def install(control_plane: Any) -> None:
    """Keep the admin console HTML/JS/CSS from surviving a server deployment in browser cache.

    The console is a small authenticated admin surface, so correctness after an online update is
    more important than saving a few static-file requests. API and Bot runtime responses are left
    untouched.
    """

    @control_plane.app.middleware("http")
    async def control_console_cache_guard(request: Any, call_next: Any) -> Any:
        response = await call_next(request)
        path = request.url.path
        if path == "/" or path.startswith("/static/"):
            response.headers["Cache-Control"] = _CONSOLE_CACHE_CONTROL
            response.headers["Pragma"] = "no-cache"
            response.headers["Expires"] = "0"
            response.headers["X-Qianniu-Console-Cache"] = "no-store"
        return response
