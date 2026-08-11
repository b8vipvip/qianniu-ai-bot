from __future__ import annotations

import re
from typing import Any, Dict, List


VISION_TEST_ID = "VISION-7382"
DEPRECATED_CHAT2API_IDS = {"default", "chatgpt-web"}
CHAT2API_REASONING_SUFFIX = re.compile(
    r"^gpt-[0-9]+(?:\.[0-9]+)*(?:-[a-z0-9]+)*-(?:instant|fast|low|medium|high|xhigh)$",
    re.I,
)

_PREFERRED_ROOTS: dict[str, str] = {}
_MODEL_DETAILS: dict[tuple[str, str], dict[str, Any]] = {}


def _normalize_base(control_plane, value: str) -> str:
    return control_plane.normalize_base_url(value).rstrip("/")


def _service_root(control_plane, value: str) -> str:
    base = _normalize_base(control_plane, value)
    return base[:-3].rstrip("/") if base.endswith("/v1") else base


def _model_rows(data: Any) -> List[Dict[str, Any]]:
    candidates: Any = None
    if isinstance(data, dict):
        if isinstance(data.get("data"), list):
            candidates = data["data"]
        elif isinstance(data.get("models"), list):
            candidates = data["models"]
    elif isinstance(data, list):
        candidates = data
    rows: List[Dict[str, Any]] = []
    for raw in candidates or []:
        if isinstance(raw, str):
            rows.append({"id": raw.strip(), "capabilities": []})
            continue
        if not isinstance(raw, dict):
            continue
        model_id = str(raw.get("id") or raw.get("name") or raw.get("model") or "").strip()
        if not model_id:
            continue
        capabilities = raw.get("capabilities")
        rows.append({
            **raw,
            "id": model_id,
            "capabilities": [str(x).strip() for x in capabilities] if isinstance(capabilities, list) else [],
        })
    unique: dict[str, Dict[str, Any]] = {}
    for row in rows:
        unique.setdefault(row["id"], row)
    return list(unique.values())


def _is_chat2api_catalog(rows: List[Dict[str, Any]], health: Dict[str, Any]) -> bool:
    if any(str(row.get("owned_by") or "").lower() == "chat2api" for row in rows):
        return True
    service = str(health.get("service") or health.get("name") or "").lower()
    return "chat2api" in service


def _deprecated_chat2api_id(model_id: str) -> bool:
    value = str(model_id or "").strip().lower()
    return value in DEPRECATED_CHAT2API_IDS or bool(CHAT2API_REASONING_SUFFIX.match(value))


def _testability(row: Dict[str, Any], chat2api_catalog: bool) -> tuple[bool, str]:
    model_id = str(row.get("id") or "").strip()
    caps = {str(x).strip().lower() for x in row.get("capabilities") or []}
    if chat2api_catalog and _deprecated_chat2api_id(model_id):
        return False, "deprecated-chat2api-model-id"
    if "image-generation" in caps and not ({"text", "vision", "file-understanding"} & caps):
        return False, "image-generation-only"
    if ("voice-generation" in caps or "voice-conversation" in caps) and not ({"vision", "file-understanding"} & caps):
        return False, "voice-route-not-deep-text-vision"
    if caps and not ({"text", "vision", "file-understanding"} & caps):
        return False, "no-text-or-vision-capability"
    return True, ""


def _health_probe(control_plane, base_url: str, api_key: str, timeout: int) -> Dict[str, Any]:
    url = _service_root(control_plane, base_url) + "/healthz"
    result = control_plane.do_request("GET", url, api_key, timeout=timeout)
    output: Dict[str, Any] = {
        "url": url,
        "network_success": result.get("network_success", False),
        "elapsed": result.get("elapsed"),
    }
    if not result.get("network_success"):
        output["error"] = result.get("error")
        return output
    response = result["response"]
    output["status_code"] = response.status_code
    data = control_plane.safe_json(response)
    if 200 <= response.status_code < 300 and isinstance(data, dict):
        output["success"] = True
        output["data"] = {
            key: data.get(key)
            for key in ("status", "version", "service", "name", "online_extensions")
            if key in data
        }
    else:
        output["error"] = control_plane.response_error(response)
    return output


def install(control_plane) -> None:
    if getattr(control_plane, "_deep_test_guard_installed", False):
        return
    control_plane._deep_test_guard_installed = True

    base_test_model = control_plane.test_model
    base_apply = control_plane.apply_deep_test_result
    base_analysis = control_plane.generate_analysis
    base_classify = control_plane.classify_failure

    def create_vision_test_data_url_v2() -> str:
        image = control_plane.Image.new("RGB", (900, 420), "white")
        draw = control_plane.ImageDraw.Draw(image)
        draw.rectangle((70, 70, 250, 250), fill=(50, 110, 230))
        draw.ellipse((650, 70, 830, 250), fill=(230, 70, 70))
        draw.text((285, 80), "VISION TEST CARD", fill=(0, 0, 0), font=control_plane.get_test_font(30))
        draw.text((245, 195), f"TEST-ID: {VISION_TEST_ID}", fill=(0, 0, 0), font=control_plane.get_test_font(48))
        draw.text((220, 305), "Read the TEST-ID and the two colored shapes", fill=(0, 0, 0), font=control_plane.get_test_font(27))
        buffer = control_plane.io.BytesIO()
        image.save(buffer, format="PNG")
        return "data:image/png;base64," + control_plane.base64.b64encode(buffer.getvalue()).decode("ascii")

    def test_vision_v2(kind: str, url: str, api_key: str, model: str, image_data_url: str, timeout: int) -> Dict[str, Any]:
        instruction = (
            "这是一张人工生成的视觉功能测试卡片。请读取图片中央 TEST-ID 后面的测试标识符，"
            "并观察左侧蓝色方形和右侧红色圆形。严格只回复："
            "TEST_ID=<标识符>;LEFT=BLUE_SQUARE;RIGHT=RED_CIRCLE"
        )
        if kind == "responses":
            payload = {
                "model": model,
                "input": [{
                    "role": "user",
                    "content": [
                        {"type": "input_text", "text": instruction},
                        {"type": "input_image", "image_url": image_data_url},
                    ],
                }],
                "max_output_tokens": 80,
            }
        else:
            payload = {
                "model": model,
                "messages": [{
                    "role": "user",
                    "content": [
                        {"type": "text", "text": instruction},
                        {"type": "image_url", "image_url": {"url": image_data_url}},
                    ],
                }],
                "max_tokens": 80,
                "temperature": 0,
            }
        result = control_plane.do_request("POST", url, api_key, payload, timeout=timeout)
        output: Dict[str, Any] = {
            "success": False,
            "kind": kind,
            "api_type": "OpenAI Responses Vision" if kind == "responses" else "OpenAI Chat Completions Vision",
            "method": "POST",
            "url": url,
            "elapsed": result["elapsed"],
            "expected": VISION_TEST_ID,
            "benchmark": "synthetic-ocr-shapes-v2",
        }
        if not result["network_success"]:
            output["status_code"] = None
            output["error"] = result["error"]
            return output
        response = result["response"]
        output["status_code"] = response.status_code
        if not (200 <= response.status_code < 300):
            output["error"] = control_plane.response_error(response)
            return output
        data = control_plane.safe_json(response)
        if data is None:
            output["error"] = "视觉请求返回的内容不是 JSON。"
            output["response_preview"] = control_plane.body_preview(response)
            return output
        text = control_plane.extract_responses_text(data) if kind == "responses" else control_plane.extract_chat_text(data)
        output["model_answer"] = text
        normalized = re.sub(r"[\s`*_]", "", text.upper())
        id_ok = VISION_TEST_ID.replace("_", "") in normalized
        output["test_id_ok"] = id_ok
        output["shape_hints_ok"] = (
            ("BLUE" in text.upper() or "蓝" in text) and
            ("RED" in text.upper() or "红" in text)
        )
        if id_ok:
            output["success"] = True
        else:
            output["error"] = f"接口接受了图片，但未正确识别视觉测试标识符。期望：{VISION_TEST_ID}，实际：{text!r}"
        return output

    def discover_models_v2(base_url: str, api_key: str, options: Dict[str, Any]) -> Dict[str, Any]:
        attempts: List[Dict[str, Any]] = []
        timeout = int(options.get("timeout_seconds", control_plane.REQUEST_TIMEOUT_SECONDS))
        health = _health_probe(control_plane, base_url, api_key, timeout)
        urls = [
            root.rstrip("/") + "/models"
            for root in control_plane.get_api_roots(
                base_url,
                include_v1_root=bool(options.get("include_v1_root", True)),
                include_root=bool(options.get("include_root", True)),
            )
        ]
        for url in list(dict.fromkeys(urls)):
            result = control_plane.do_request("GET", url, api_key, timeout=timeout)
            attempt: Dict[str, Any] = {
                "url": url,
                "network_success": result["network_success"],
                "elapsed": result["elapsed"],
            }
            if not result["network_success"]:
                attempt["error"] = result["error"]
                attempts.append(attempt)
                continue
            response = result["response"]
            attempt["status_code"] = response.status_code
            attempt["content_type"] = response.headers.get("content-type", "")
            if response.status_code != 200:
                attempt["error"] = control_plane.response_error(response)
                attempts.append(attempt)
                continue
            data = control_plane.safe_json(response)
            if data is None:
                attempt["error"] = "HTTP 200，但返回内容不是 JSON"
                attempt["response_preview"] = control_plane.body_preview(response)
                attempts.append(attempt)
                continue
            rows = _model_rows(data)
            if not rows:
                attempt["error"] = "返回 JSON，但未识别到模型列表"
                attempts.append(attempt)
                continue

            chat2api_catalog = _is_chat2api_catalog(rows, health.get("data") or {})
            testable: List[str] = []
            skipped: List[Dict[str, Any]] = []
            base_key = _normalize_base(control_plane, base_url)
            for row in rows:
                ok, reason = _testability(row, chat2api_catalog)
                _MODEL_DETAILS[(base_key, row["id"])] = row
                if ok:
                    testable.append(row["id"])
                else:
                    skipped.append({"id": row["id"], "reason": reason, "capabilities": row.get("capabilities") or []})

            preferred_root = url[: -len("/models")].rstrip("/")
            _PREFERRED_ROOTS[base_key] = preferred_root
            attempt["success"] = True
            attempt["models_count"] = len(rows)
            attempt["testable_models_count"] = len(testable)
            attempts.append(attempt)
            return {
                "success": True,
                "url": url,
                "preferred_api_root": preferred_root,
                "models": list(dict.fromkeys(testable)),
                "all_models": [row["id"] for row in rows],
                "model_details": rows,
                "skipped_models": skipped,
                "chat2api_catalog": chat2api_catalog,
                "server_health": health,
                "server_version": (health.get("data") or {}).get("version"),
                "attempts": attempts,
            }
        return {"success": False, "models": [], "model_details": [], "skipped_models": [], "server_health": health, "attempts": attempts}

    def test_model_v2(provider: Dict[str, Any], model: str, options: Dict[str, Any], image_data_url: str) -> Dict[str, Any]:
        adjusted = dict(options or {})
        base_key = _normalize_base(control_plane, provider["base_url"])
        preferred = _PREFERRED_ROOTS.get(base_key)
        if preferred:
            adjusted["include_v1_root"] = preferred.endswith("/v1")
            adjusted["include_root"] = not preferred.endswith("/v1")
        detail = _MODEL_DETAILS.get((base_key, model)) or {}
        caps = {str(x).lower() for x in detail.get("capabilities") or []}
        if caps and "vision" not in caps and "file-understanding" not in caps:
            adjusted["responses_vision"] = False
            adjusted["chat_vision"] = False
        return base_test_model(provider, model, adjusted, image_data_url)

    def apply_deep_test_result_v2(provider_id: int, provider: Dict[str, Any], result: Dict[str, Any], options: Dict[str, Any]) -> Dict[str, Any]:
        clean_provider = dict(provider)
        if bool((result.get("discovery") or {}).get("chat2api_catalog")):
            if _deprecated_chat2api_id(clean_provider.get("main_text_model", "")):
                clean_provider["main_text_model"] = ""
            clean_provider["backup_text_models"] = [
                item for item in clean_provider.get("backup_text_models", [])
                if not _deprecated_chat2api_id(item)
            ]
            if _deprecated_chat2api_id(clean_provider.get("main_vision_model", "")):
                clean_provider["main_vision_model"] = ""
            clean_provider["backup_vision_models"] = [
                item for item in clean_provider.get("backup_vision_models", [])
                if not _deprecated_chat2api_id(item)
            ]
        return base_apply(provider_id, clean_provider, result, options)

    def classify_failure_v2(reason: str) -> str:
        if "未正确识别视觉测试标识符" in str(reason or ""):
            return "视觉理解失败"
        return base_classify(reason)

    def generate_analysis_v2(provider: Dict[str, Any], result: Dict[str, Any]) -> str:
        text = base_analysis(provider, result)
        text = text.replace("验证码", "视觉测试标识符")
        discovery = result.get("discovery") or {}
        details = [
            "",
            "## 深度测试探测信息",
            "",
            f"- 视觉基准：人工合成 OCR + 图形测试卡（{VISION_TEST_ID}），不使用验证码测试。",
            f"- 服务端版本：`{discovery.get('server_version') or '未知'}`",
            f"- 首选 API Root：`{discovery.get('preferred_api_root') or '未锁定'}`",
            f"- 原始发现模型：{len(discovery.get('all_models') or discovery.get('models') or [])}",
            f"- 纳入文本/视觉深测模型：{len(discovery.get('models') or [])}",
        ]
        skipped = discovery.get("skipped_models") or []
        if skipped:
            details.append("- 跳过模型：" + "、".join(f"{item.get('id')}({item.get('reason')})" for item in skipped))
        return text + "\n" + "\n".join(details)

    control_plane.VISION_CODE = VISION_TEST_ID
    control_plane.create_vision_test_data_url = create_vision_test_data_url_v2
    control_plane.test_vision = test_vision_v2
    control_plane.discover_models = discover_models_v2
    control_plane.test_model = test_model_v2
    control_plane.apply_deep_test_result = apply_deep_test_result_v2
    control_plane.classify_failure = classify_failure_v2
    control_plane.generate_analysis = generate_analysis_v2
