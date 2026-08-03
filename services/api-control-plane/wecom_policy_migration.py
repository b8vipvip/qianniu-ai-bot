from __future__ import annotations

from pathlib import Path
from typing import Any, Dict

from starlette.middleware.base import BaseHTTPMiddleware
from starlette.requests import Request
from starlette.responses import HTMLResponse, JSONResponse, Response


STATIC_DIR = Path(__file__).resolve().parent / "static"
MIGRATION_MESSAGE = (
    "AI 转人工策略已迁移到 Windows Bot："
    "功能设置 → 消息通知 → 转人工通知 → 通知策略。"
)


def deprecated_policy_state() -> Dict[str, Any]:
    return {
        "deprecated": True,
        "message": MIGRATION_MESSAGE,
        "policy_text": MIGRATION_MESSAGE,
        "summary": {
            "manual": [],
            "confirm": [],
            "safe_exceptions": [],
            "manual_count": 0,
            "confirm_count": 0,
            "safe_exception_count": 0,
            "enabled_rule_count": 0,
            "total_rule_count": 0,
        },
        "rules": [],
        "revision": "",
        "generated_at": None,
        "published_at": None,
        "updated_at": None,
        "can_rollback": False,
        "version_count": 0,
    }


def transform_wecom_html(html: str) -> str:
    html = html or ""
    hidden_css = (
        "\n/* AI handoff policy moved to Windows Bot. Keep hidden DOM nodes so the"
        " legacy page script cannot fail while deployments transition. */\n"
        ".panel:has(#policyText),.metric:has(#mRules){display:none!important}\n"
    )
    if "</style>" in html and "AI handoff policy moved to Windows Bot" not in html:
        html = html.replace("</style>", hidden_css + "</style>", 1)

    html = html.replace(
        "管理应用消息、加密回调、人工回复权限和 AI 转人工策略。",
        "管理应用消息、加密回调和人工回复权限。AI 转人工策略已迁移到 Windows Bot。",
    )
    html = html.replace(
        "企业微信敏感字段使用 Fernet 加密。转人工策略由 AI 编译为本地规则，买家消息判断不会逐条调用 AI。",
        "企业微信敏感字段使用 Fernet 加密。AI 转人工策略已迁移到 Windows Bot 本机管理。",
    )

    marker = "id=\"handoffPolicyMigrationNotice\""
    if marker not in html:
        notice = """
    <section class="panel" id="handoffPolicyMigrationNotice">
      <div class="panel-head">
        <div>
          <h2>AI 转人工策略已迁移</h2>
          <p>企业微信服务端不再编辑、编译或执行 AI 转人工规则，只保留旧规则的受令牌保护只读迁移接口。</p>
        </div>
      </div>
      <div class="policy-help">
        请在 Windows Bot 中打开：<strong>功能设置 → 消息通知 → 转人工通知 → 通知策略</strong>。<br>
        新版 Windows Bot 首次启动会自动读取一次旧服务端规则并保存为本机 JSON，之后不再轮询。<br>
        本机策略支持导入、导出、覆盖、合并、追加、勾选删除和清空。
      </div>
    </section>
"""
        if "</form>" in html:
            html = html.replace("</form>", "</form>" + notice, 1)
        elif "</main>" in html:
            html = html.replace("</main>", notice + "</main>", 1)
    return html


class WeComPolicyMigrationMiddleware(BaseHTTPMiddleware):
    async def dispatch(self, request: Request, call_next) -> Response:
        path = request.url.path.rstrip("/") or "/"
        method = request.method.upper()

        if path == "/static/wecom.html" and method == "GET":
            source = STATIC_DIR / "wecom.html"
            if not source.exists():
                return JSONResponse({"detail": "企业微信配置页不存在"}, status_code=404)
            return HTMLResponse(transform_wecom_html(source.read_text(encoding="utf-8-sig")))

        # Keep the existing authenticated runtime GET route reachable only for
        # one-time migration by upgraded Windows clients. The client writes a
        # local marker after success and never polls this endpoint again.
        if path == "/api/runtime/v1/handoff/rules" and method == "GET":
            return await call_next(request)

        if path == "/api/admin/wecom/handoff-rules":
            if method == "GET":
                return JSONResponse(
                    {
                        "deprecated": True,
                        "message": MIGRATION_MESSAGE,
                        "revision": "",
                        "updated_at": None,
                        "rules": [],
                    }
                )
            return JSONResponse({"detail": MIGRATION_MESSAGE}, status_code=410)

        if path.startswith("/api/admin/wecom/handoff-policy"):
            if method == "GET":
                return JSONResponse(deprecated_policy_state())
            return JSONResponse({"detail": MIGRATION_MESSAGE}, status_code=410)

        return await call_next(request)


def install(control_plane: Any) -> None:
    control_plane.app.add_middleware(WeComPolicyMigrationMiddleware)
