from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field

import wecom_settings


router = APIRouter()

DEFAULT_POLICY_TEXT = """退款、退货、投诉、差评、赔偿、发票、税票、订单隐私、身份证、银行卡、法律、维权、平台介入等问题必须转人工，不自动承诺具体处理结果。
手机号、地址、隐私、密码、验证码、转账、补偿、客服主管等问题由人工确认。
涉及账号密码、验证码、登录、找回、被盗、冻结、封禁、绑定、解绑、实名、身份证、泄露、安全、申诉或换绑时必须转人工。
买家只是给朋友、给别人、帮朋友、帮别人或其他账号购买月卡、充值、代充、再次购买时属于正常购买，不转人工。
正常代充场景可回复：可以的，月卡可以给朋友或其他账号充值，您再拍对应月卡即可；下单后按页面提示提供需要充值的账号。"""

EXPECTED_VALUES = {"manual", "confirm", "handoff", "safe", "safe_reply", "none"}
TERM_SPLIT_RE = re.compile(r"[|｜,，;；\n\r]+")


class HandoffPolicyCompileInput(BaseModel):
    policy_text: str = Field(min_length=10, max_length=12000)


class HandoffPolicyPublishInput(BaseModel):
    policy_text: str = Field(min_length=10, max_length=12000)
    rules: List[wecom_settings.HandoffRuleInput] = Field(default_factory=list)
    summary: Dict[str, Any] = Field(default_factory=dict)
    tests: List[Dict[str, Any]] = Field(default_factory=list)


def _json_text(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _parse_json(value: Any, default: Any) -> Any:
    if not value:
        return default
    if isinstance(value, (dict, list)):
        return value
    try:
        return json.loads(str(value))
    except Exception:
        return default


def _split_terms(value: Any, limit: int = 120) -> List[str]:
    items: Iterable[Any]
    if isinstance(value, list):
        items = value
    else:
        items = TERM_SPLIT_RE.split(str(value or ""))
    output: List[str] = []
    seen = set()
    for raw in items:
        text = re.sub(r"\s+", " ", str(raw or "")).strip(" ，,；;|｜")
        if not text:
            continue
        text = text[:120]
        key = text.casefold()
        if key in seen:
            continue
        seen.add(key)
        output.append(text)
        if len(output) >= limit:
            break
    return output


def _term_text(value: Any, max_chars: int = 3000) -> str:
    output: List[str] = []
    size = 0
    for term in _split_terms(value):
        extra = len(term) + (1 if output else 0)
        if size + extra > max_chars:
            break
        output.append(term)
        size += extra
    return "|".join(output)


def _summary_list(value: Any, max_items: int = 60) -> List[str]:
    return _split_terms(value, limit=max_items)


def summarize_rules(rules: Sequence[Dict[str, Any]]) -> Dict[str, Any]:
    manual: List[str] = []
    confirm: List[str] = []
    safe: List[str] = []
    for rule in rules:
        if not bool(rule.get("enabled", True)):
            continue
        keyword = str(rule.get("keyword") or "").strip()
        if keyword:
            target = manual if str(rule.get("rule_type")) == "manual" else confirm
            if keyword not in target:
                target.append(keyword)
        for item in _split_terms(rule.get("exceptions")):
            if item not in safe:
                safe.append(item)
    return {
        "manual": manual[:60],
        "confirm": confirm[:60],
        "safe_exceptions": safe[:80],
        "manual_count": len(manual),
        "confirm_count": len(confirm),
        "safe_exception_count": len(safe),
        "enabled_rule_count": sum(1 for x in rules if bool(x.get("enabled", True))),
        "total_rule_count": len(rules),
    }


def policy_text_from_rules(rules: Sequence[Dict[str, Any]]) -> str:
    if not rules:
        return DEFAULT_POLICY_TEXT
    summary = summarize_rules(rules)
    lines: List[str] = []
    if summary["manual"]:
        lines.append("以下问题必须转人工：" + "、".join(summary["manual"]) + "。")
    if summary["confirm"]:
        lines.append("以下问题由人工确认：" + "、".join(summary["confirm"]) + "。")
    for rule in rules:
        if not bool(rule.get("enabled", True)):
            continue
        risk = _split_terms(rule.get("risk_terms"))
        exceptions = _split_terms(rule.get("exceptions"))
        if risk:
            lines.append(
                f"涉及“{str(rule.get('keyword') or '').strip()}”并同时出现"
                + "、".join(risk)
                + "等风险语境时转人工。"
            )
        if exceptions:
            lines.append(
                f"涉及“{str(rule.get('keyword') or '').strip()}”时，"
                + "、".join(exceptions)
                + "属于可自动处理的例外，不转人工。"
            )
            reply = str(rule.get("safe_reply") or "").strip()
            if reply:
                lines.append("例外场景回复：" + reply)
    return "\n".join(lines).strip() or DEFAULT_POLICY_TEXT


def init_handoff_policy_db(path: Optional[Path] = None) -> None:
    wecom_settings.init_wecom_settings_db(path)
    with wecom_settings.db(path) as conn:
        conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS wecom_handoff_policy (
                id INTEGER PRIMARY KEY CHECK(id=1),
                policy_text TEXT NOT NULL DEFAULT '',
                summary_json TEXT NOT NULL DEFAULT '{}',
                generated_at TEXT,
                published_at TEXT,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS wecom_handoff_policy_versions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                policy_text TEXT NOT NULL,
                rules_json TEXT NOT NULL,
                summary_json TEXT NOT NULL DEFAULT '{}',
                revision TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_wecom_handoff_policy_versions
                ON wecom_handoff_policy_versions(id DESC);
            """
        )
        row = conn.execute("SELECT id FROM wecom_handoff_policy WHERE id=1").fetchone()
        if row:
            return
    rule_state = wecom_settings.load_handoff_rules(path)
    rules = list(rule_state.get("rules") or [])
    now = wecom_settings.iso_now()
    with wecom_settings.db(path) as conn:
        conn.execute(
            """
            INSERT OR IGNORE INTO wecom_handoff_policy(
                id,policy_text,summary_json,generated_at,published_at,updated_at
            ) VALUES(1,?,?,?,?,?)
            """,
            (
                policy_text_from_rules(rules),
                _json_text(summarize_rules(rules)),
                None,
                now,
                now,
            ),
        )


def load_policy_state(path: Optional[Path] = None) -> Dict[str, Any]:
    init_handoff_policy_db(path)
    rule_state = wecom_settings.load_handoff_rules(path)
    rules = list(rule_state.get("rules") or [])
    with wecom_settings.db(path) as conn:
        row = conn.execute("SELECT * FROM wecom_handoff_policy WHERE id=1").fetchone()
        version_count = int(
            conn.execute("SELECT COUNT(*) count FROM wecom_handoff_policy_versions").fetchone()["count"]
        )
    return {
        "policy_text": str(row["policy_text"] or DEFAULT_POLICY_TEXT) if row else DEFAULT_POLICY_TEXT,
        "summary": summarize_rules(rules),
        "rules": rules,
        "revision": str(rule_state.get("revision") or ""),
        "generated_at": row["generated_at"] if row else None,
        "published_at": row["published_at"] if row else None,
        "updated_at": row["updated_at"] if row else None,
        "can_rollback": version_count > 0,
        "version_count": version_count,
    }


def _extract_json_object(text: str) -> Dict[str, Any]:
    value = str(text or "").strip()
    value = re.sub(r"^```(?:json)?\s*", "", value, flags=re.IGNORECASE)
    value = re.sub(r"\s*```$", "", value)
    start = value.find("{")
    end = value.rfind("}")
    if start < 0 or end <= start:
        raise ValueError("AI 未返回 JSON 对象")
    try:
        payload = json.loads(value[start : end + 1])
    except Exception as exc:
        raise ValueError("AI 返回的规则 JSON 无法解析") from exc
    if not isinstance(payload, dict):
        raise ValueError("AI 返回内容必须是 JSON 对象")
    return payload


def _rule_type(value: Any) -> str:
    text = str(value or "confirm").strip().lower()
    if text in {"manual", "force", "forced", "强制转人工", "必须转人工", "转人工"}:
        return "manual"
    return "confirm"


def _match_mode(value: Any) -> str:
    text = str(value or "contains").strip().lower()
    if text in {"sensitive_context", "context", "关键词+敏感语境", "敏感语境"}:
        return "sensitive_context"
    return "contains"


def normalize_compiled_payload(payload: Dict[str, Any]) -> Dict[str, Any]:
    source_rules = payload.get("rules")
    if not isinstance(source_rules, list) or not source_rules:
        raise ValueError("AI 没有生成任何可发布的转人工规则")
    if len(source_rules) > 300:
        raise ValueError("AI 生成规则超过300条，请缩短策略后重试")

    normalized: List[Dict[str, Any]] = []
    seen = set()
    for index, raw in enumerate(source_rules):
        if not isinstance(raw, dict):
            raise ValueError(f"AI 生成的第 {index + 1} 条规则格式无效")
        keyword = re.sub(r"\s+", " ", str(raw.get("keyword") or "")).strip()[:120]
        if not keyword:
            raise ValueError(f"AI 生成的第 {index + 1} 条规则缺少关键词")
        key = keyword.casefold()
        if key in seen:
            continue
        seen.add(key)
        rule = wecom_settings.HandoffRuleInput(
            enabled=bool(raw.get("enabled", True)),
            rule_type=_rule_type(raw.get("rule_type")),
            keyword=keyword,
            match_mode=_match_mode(raw.get("match_mode")),
            risk_terms=_term_text(raw.get("risk_terms")),
            exceptions=_term_text(raw.get("exceptions")),
            safe_reply=str(raw.get("safe_reply") or "").strip()[:1200],
            note=str(raw.get("note") or "").strip()[:1000],
            sort_order=max(0, min(100000, int(raw.get("sort_order") or ((index + 1) * 10)))),
        )
        normalized.append(rule.dict())

    if not normalized:
        raise ValueError("AI 生成规则去重后为空")

    raw_summary = payload.get("summary") if isinstance(payload.get("summary"), dict) else {}
    generated = summarize_rules(normalized)
    summary = {
        "manual": _summary_list(raw_summary.get("manual")) or generated["manual"],
        "confirm": _summary_list(raw_summary.get("confirm")) or generated["confirm"],
        "safe_exceptions": _summary_list(raw_summary.get("safe_exceptions")) or generated["safe_exceptions"],
        "manual_count": generated["manual_count"],
        "confirm_count": generated["confirm_count"],
        "safe_exception_count": generated["safe_exception_count"],
        "enabled_rule_count": generated["enabled_rule_count"],
        "total_rule_count": generated["total_rule_count"],
    }
    tests = normalize_ai_tests(payload.get("tests"))
    return {"rules": normalized, "summary": summary, "tests": tests}


def normalize_ai_tests(value: Any) -> List[Dict[str, Any]]:
    if not isinstance(value, list):
        return []
    output: List[Dict[str, Any]] = []
    for item in value[:30]:
        if not isinstance(item, dict):
            continue
        message = str(item.get("message") or "").strip()[:500]
        expected = str(item.get("expected") or "").strip().lower()
        if not message or expected not in EXPECTED_VALUES:
            continue
        output.append(
            {
                "message": message,
                "expected": expected,
                "reason": str(item.get("reason") or "").strip()[:500],
                "required": False,
                "source": "ai",
            }
        )
    return output


def evaluate_message(rules: Sequence[Dict[str, Any]], message: str) -> Dict[str, Any]:
    text = str(message or "")
    ordered = sorted(
        [rule for rule in rules if bool(rule.get("enabled", True))],
        key=lambda item: (int(item.get("sort_order") or 0), str(item.get("keyword") or "")),
    )
    for rule in ordered:
        keyword = str(rule.get("keyword") or "").strip()
        if not keyword or keyword.casefold() not in text.casefold():
            continue
        risk_hit = next(
            (term for term in _split_terms(rule.get("risk_terms")) if term.casefold() in text.casefold()),
            "",
        )
        exception_hit = next(
            (term for term in _split_terms(rule.get("exceptions")) if term.casefold() in text.casefold()),
            "",
        )
        contextual = str(rule.get("match_mode") or "contains") == "sensitive_context"
        if contextual and risk_hit:
            return {
                "result": str(rule.get("rule_type") or "confirm"),
                "keyword": keyword,
                "hit": risk_hit,
            }
        if exception_hit:
            return {"result": "safe_reply", "keyword": keyword, "hit": exception_hit}
        return {
            "result": str(rule.get("rule_type") or "confirm"),
            "keyword": keyword,
            "hit": keyword,
        }
    return {"result": "none", "keyword": "", "hit": ""}


def _expected_passed(expected: str, actual: str) -> bool:
    if expected == "handoff":
        return actual in {"manual", "confirm"}
    if expected in {"safe", "safe_reply"}:
        return actual in {"safe_reply", "none"}
    return expected == actual


def _required_tests(policy_text: str) -> List[Dict[str, Any]]:
    text = str(policy_text or "")
    tests: List[Dict[str, Any]] = []
    if any(term in text for term in ("退款", "退货", "投诉", "差评", "赔偿", "平台介入")):
        tests.append(
            {
                "message": "我想申请退款",
                "expected": "handoff",
                "reason": "策略包含退款/售后风险，必须确保转人工。",
                "required": True,
                "source": "safety",
            }
        )
    if any(term in text for term in ("密码", "验证码", "账号安全", "登录", "找回", "被盗", "换绑")):
        tests.append(
            {
                "message": "我的账号密码忘了，验证码也收不到",
                "expected": "handoff",
                "reason": "账号安全风险必须优先转人工。",
                "required": True,
                "source": "safety",
            }
        )
    if any(term in text for term in ("给朋友", "给别人", "其他账号", "另一个账号", "代充", "充值")):
        tests.extend(
            [
                {
                    "message": "我现在充另一个账号，可以再拍那个月卡吗",
                    "expected": "safe",
                    "reason": "正常给其他账号购买不能被误转人工。",
                    "required": True,
                    "source": "safety",
                },
                {
                    "message": "可以给朋友充一个月吗",
                    "expected": "safe",
                    "reason": "正常给朋友购买应继续自动处理。",
                    "required": True,
                    "source": "safety",
                },
            ]
        )
    return tests


def validate_policy(
    policy_text: str,
    rules: Sequence[Dict[str, Any]],
    ai_tests: Optional[Sequence[Dict[str, Any]]] = None,
) -> Dict[str, Any]:
    tests: List[Dict[str, Any]] = []
    seen = set()
    for item in [*_required_tests(policy_text), *(list(ai_tests or []))]:
        message = str(item.get("message") or "").strip()
        expected = str(item.get("expected") or "").strip().lower()
        if not message or expected not in EXPECTED_VALUES:
            continue
        key = (message.casefold(), expected)
        if key in seen:
            continue
        seen.add(key)
        actual = evaluate_message(rules, message)
        tests.append(
            {
                "message": message,
                "expected": expected,
                "actual": actual["result"],
                "keyword": actual["keyword"],
                "hit": actual["hit"],
                "reason": str(item.get("reason") or "")[:500],
                "required": bool(item.get("required")),
                "source": str(item.get("source") or "ai"),
                "passed": _expected_passed(expected, actual["result"]),
            }
        )
    required_failures = [item for item in tests if item["required"] and not item["passed"]]
    return {
        "ok": not required_failures,
        "required_failure_count": len(required_failures),
        "passed_count": sum(1 for item in tests if item["passed"]),
        "total_count": len(tests),
        "tests": tests,
    }


def _ai_prompt(policy_text: str, current_rules: Sequence[Dict[str, Any]]) -> List[Dict[str, str]]:
    system = """你是千牛客服系统的“转人工策略编译器”。管理员只维护自然语言业务策略，你要把它编译成供 Windows Bot 本地快速判断的结构化规则。
只返回一个 JSON 对象，不要 Markdown、解释或代码围栏。不得编造价格、库存、时效、退款承诺、赔偿承诺或管理员没有提供的业务事实。
规则字段必须为：enabled、rule_type、keyword、match_mode、risk_terms、exceptions、safe_reply、note、sort_order。
rule_type 只能是 manual 或 confirm；match_mode 只能是 contains 或 sensitive_context。
keyword 是内部触发词，一条规则一个关键词；risk_terms 和 exceptions 必须是字符串数组；sort_order 从10开始递增。
对“账号”等宽泛词优先使用 sensitive_context。明确密码、验证码、登录安全、找回、被盗、实名、换绑等风险必须优先于正常购买例外。
正常购买例外要放在同一条宽泛规则的 exceptions 中，并提供只基于管理员原文的 safe_reply；管理员没有提供回复话术时 safe_reply 留空。
避免使用“可以、怎么、这个”等过宽关键词。尽量覆盖管理员策略中的常见同义表达，但不要扩展出新的业务结论。
JSON 结构：
{"summary":{"manual":[],"confirm":[],"safe_exceptions":[]},"rules":[],"tests":[{"message":"示例买家消息","expected":"manual|confirm|handoff|safe|none","reason":"原因"}]}"""
    current = json.dumps(list(current_rules), ensure_ascii=False, separators=(",", ":"))
    if len(current) > 18000:
        current = current[:18000]
    user = (
        "请编译下面的管理员策略。当前规则仅供迁移参考；若与管理员新策略冲突，以新策略为准。\n\n"
        "【管理员自然语言策略】\n"
        + policy_text.strip()
        + "\n\n【当前已发布规则】\n"
        + current
        + "\n\n至少生成5条具有代表性的 tests，并确保给朋友/其他账号正常购买不会被宽泛账号规则误伤。"
    )
    return [{"role": "system", "content": system}, {"role": "user", "content": user}]


def compile_policy_with_ai(policy_text: str, path: Optional[Path] = None) -> Dict[str, Any]:
    current = wecom_settings.load_handoff_rules(path)
    try:
        import app as control_plane

        result = control_plane.dispatch_chat(
            "admin-handoff-policy",
            "text-default",
            _ai_prompt(policy_text, current.get("rules") or []),
            4000,
            0.1,
            180,
        )
    except Exception as exc:
        raise HTTPException(status_code=502, detail="调用 AI 生成转人工策略失败：" + str(exc)[:500]) from exc

    if not result.get("success"):
        attempts = result.get("attempts") or []
        error = next((str(item.get("error")) for item in reversed(attempts) if item.get("error")), "没有可用的文本模型")
        raise HTTPException(status_code=502, detail="AI 生成转人工策略失败：" + error[:500])

    attempt = result.get("attempt") or {}
    try:
        normalized = normalize_compiled_payload(_extract_json_object(str(attempt.get("answer") or "")))
    except ValueError as exc:
        raise HTTPException(status_code=502, detail=str(exc)) from exc
    validation = validate_policy(policy_text, normalized["rules"], normalized["tests"])
    return {
        "policy_text": policy_text.strip(),
        "summary": normalized["summary"],
        "rules": normalized["rules"],
        "tests": normalized["tests"],
        "validation": validation,
        "generated_at": wecom_settings.iso_now(),
        "model": str(attempt.get("model") or ""),
        "provider": str(attempt.get("provider_name") or ""),
    }


def _backup_current_state(path: Optional[Path] = None) -> None:
    state = load_policy_state(path)
    if not state.get("rules"):
        return
    now = wecom_settings.iso_now()
    with wecom_settings.db(path) as conn:
        conn.execute(
            """
            INSERT INTO wecom_handoff_policy_versions(
                policy_text,rules_json,summary_json,revision,created_at
            ) VALUES(?,?,?,?,?)
            """,
            (
                state["policy_text"],
                _json_text(state["rules"]),
                _json_text(state["summary"]),
                state.get("revision") or "",
                now,
            ),
        )
        conn.execute(
            """
            DELETE FROM wecom_handoff_policy_versions
            WHERE id NOT IN (
                SELECT id FROM wecom_handoff_policy_versions ORDER BY id DESC LIMIT 20
            )
            """
        )


def publish_policy_state(
    policy_text: str,
    rules: Sequence[Dict[str, Any]],
    summary: Optional[Dict[str, Any]] = None,
    tests: Optional[Sequence[Dict[str, Any]]] = None,
    path: Optional[Path] = None,
) -> Dict[str, Any]:
    normalized = normalize_compiled_payload(
        {"rules": list(rules), "summary": summary or {}, "tests": list(tests or [])}
    )
    validation = validate_policy(policy_text, normalized["rules"], normalized["tests"])
    if not validation["ok"]:
        failed = next(item for item in validation["tests"] if item["required"] and not item["passed"])
        raise HTTPException(
            status_code=400,
            detail="策略安全测试未通过，已拒绝发布：" + failed["message"] + "，实际判断=" + failed["actual"],
        )

    init_handoff_policy_db(path)
    _backup_current_state(path)
    saved = wecom_settings.save_handoff_rules(
        wecom_settings.HandoffRuleSetInput(
            rules=[wecom_settings.HandoffRuleInput(**item) for item in normalized["rules"]]
        ),
        path,
    )
    now = wecom_settings.iso_now()
    with wecom_settings.db(path) as conn:
        conn.execute(
            """
            INSERT INTO wecom_handoff_policy(
                id,policy_text,summary_json,generated_at,published_at,updated_at
            ) VALUES(1,?,?,?,?,?)
            ON CONFLICT(id) DO UPDATE SET
                policy_text=excluded.policy_text,
                summary_json=excluded.summary_json,
                generated_at=excluded.generated_at,
                published_at=excluded.published_at,
                updated_at=excluded.updated_at
            """,
            (
                policy_text.strip(),
                _json_text(normalized["summary"]),
                now,
                now,
                now,
            ),
        )
    state = load_policy_state(path)
    state["validation"] = validation
    state["revision"] = saved.get("revision") or state.get("revision")
    return state


def rollback_policy_state(path: Optional[Path] = None) -> Dict[str, Any]:
    init_handoff_policy_db(path)
    with wecom_settings.db(path) as conn:
        row = conn.execute(
            "SELECT * FROM wecom_handoff_policy_versions ORDER BY id DESC LIMIT 1"
        ).fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="没有可恢复的上一个策略版本")
    rules = _parse_json(row["rules_json"], [])
    if not isinstance(rules, list):
        raise HTTPException(status_code=500, detail="历史策略版本损坏，无法恢复")
    saved = wecom_settings.save_handoff_rules(
        wecom_settings.HandoffRuleSetInput(
            rules=[wecom_settings.HandoffRuleInput(**item) for item in rules]
        ),
        path,
    )
    now = wecom_settings.iso_now()
    with wecom_settings.db(path) as conn:
        conn.execute(
            """
            UPDATE wecom_handoff_policy
            SET policy_text=?,summary_json=?,generated_at=NULL,published_at=?,updated_at=?
            WHERE id=1
            """,
            (row["policy_text"], row["summary_json"], now, now),
        )
        conn.execute("DELETE FROM wecom_handoff_policy_versions WHERE id=?", (row["id"],))
    state = load_policy_state(path)
    state["revision"] = saved.get("revision") or state.get("revision")
    state["restored"] = True
    return state


@router.get("/api/admin/wecom/handoff-policy")
def admin_get_handoff_policy(_: str = Depends(wecom_settings.require_admin)) -> Dict[str, Any]:
    return load_policy_state()


@router.post("/api/admin/wecom/handoff-policy/compile")
def admin_compile_handoff_policy(
    data: HandoffPolicyCompileInput,
    _: str = Depends(wecom_settings.require_admin),
) -> Dict[str, Any]:
    return compile_policy_with_ai(data.policy_text)


@router.put("/api/admin/wecom/handoff-policy/publish")
def admin_publish_handoff_policy(
    data: HandoffPolicyPublishInput,
    _: str = Depends(wecom_settings.require_admin),
) -> Dict[str, Any]:
    return publish_policy_state(
        data.policy_text,
        [item.dict() for item in data.rules],
        data.summary,
        data.tests,
    )


@router.post("/api/admin/wecom/handoff-policy/rollback")
def admin_rollback_handoff_policy(
    _: str = Depends(wecom_settings.require_admin),
) -> Dict[str, Any]:
    return rollback_policy_state()
