"""Fixed-origin, per-user API transport. Never forwards browser credentials."""

from __future__ import annotations

import json
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import HTTPRedirectHandler, Request, build_opener

from app.auth.labelsystem import AuthorityError, api_base_url, authority


class IntegrationError(AuthorityError):
    def __init__(self, code: str, message: str, status: int = 409) -> None:
        super().__init__(code, status)
        self.message = message


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args: Any, **kwargs: Any) -> None:  # noqa: ANN401
        return None


def exchange(method: str, path: str, body: object = None) -> tuple[bytes, str]:
    try:
        base = api_base_url()
    except AuthorityError:
        raise IntegrationError(
            "integration_configuration_invalid", "账号服务地址配置无效。", 503
        ) from None
    if not path.startswith("/api/v1/") or ".." in path or "?" in path or "#" in path:
        raise IntegrationError("integration_route_invalid", "接口地址无效。", 400)
    headers = {"Accept": "application/json", "Authorization": "Bearer " + authority().token()}
    raw = None
    if body is not None:
        raw = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json"
    try:
        # The configured origin is limited to HTTPS or HTTP loopback above.
        req = Request(base + path, data=raw, headers=headers, method=method)  # noqa: S310
        with build_opener(NoRedirect()).open(req, timeout=180) as response:
            data = response.read(90 * 1024 * 1024 + 1)
            if len(data) > 90 * 1024 * 1024:
                raise IntegrationError("integration_response_too_large", "返回文件过大。", 502)
            return data, response.headers.get_content_type()
    except HTTPError as exc:
        code, message = "integration_request_failed", "操作未完成，请刷新后重试。"
        try:
            value = json.loads(exc.read(65536))
            if isinstance(value.get("code"), str) and isinstance(value.get("message"), str):
                code, message = value["code"], value["message"]
            error = value.get("error", {})
            if isinstance(error, dict):
                code, message = error.get("code", code), error.get("message", message)
        except (ValueError, AttributeError):
            pass
        raise IntegrationError(code, message, exc.code if 400 <= exc.code <= 599 else 502) from None
    except (URLError, TimeoutError, OSError):
        raise IntegrationError(
            "labelsystem_unavailable", "业务服务暂不可用，请稍后重试。", 503
        ) from None


def invoke(method: str, path: str, body: object = None) -> Any:  # noqa: ANN401 -- decoded JSON contract
    raw, media = exchange(method, path, body)
    try:
        value = json.loads(raw)
        if (
            media != "application/json"
            or not isinstance(value, dict)
            or value.get("success") is not True
        ):
            raise ValueError
        return value["data"]
    except (ValueError, KeyError):
        raise IntegrationError(
            "integration_response_invalid", "业务服务返回内容无效。", 502
        ) from None
