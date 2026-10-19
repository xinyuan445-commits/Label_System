"""Explicit 017 routes; the Label API owns all business writes and idempotency."""

from __future__ import annotations

import re

from flask import Blueprint, Response, g, jsonify, request
from flask.typing import ResponseReturnValue
from flask_login import current_user, login_required

from app.auth import labelsystem
from app.services.labelsystem_transport import IntegrationError, exchange, invoke

labelsystem_print_bp = Blueprint("labelsystem_print", __name__)


@labelsystem_print_bp.after_request
def private_response(response: Response) -> Response:
    response.headers["Cache-Control"] = "private, no-store"
    return response


_UUID = r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
# method, client path, permission, administrator-only
_PRINT = [
    (
        "GET",
        r"capabilities|pending-labels|source-summary|jobs|templates|code-rule|devices",
        "print.view",
        False,
    ),
    ("GET", rf"jobs/{_UUID}(?:/bundle|/pages/{_UUID})?", "print.view", False),
    ("GET", r"templates/[1-9]\d*|assets/[1-9]\d*(?:/metadata)?", "print.view", False),
    ("GET", r"labels/[1-9]\d*/original", "print.view", False),
    ("POST", r"jobs/first", "print.first", False),
    ("POST", r"jobs/reprint", "print.reprint", False),
    ("POST", r"labels/resolve", "print.view", False),
    ("POST", r"labels/legacy-baseline", "print.reconcile", False),
    (
        "POST",
        rf"jobs/{_UUID}/(?:render|receipts|confirmations|retry-sync|dispatch-authorizations|cancel|resume)",
        "print.confirm",
        False,
    ),
    ("POST", rf"jobs/{_UUID}/reconciliations", "print.reconcile", True),
    ("PUT", r"code-rule", "print.template.manage", True),
    ("POST", r"templates|templates/[1-9]\d*/publish|assets", "print.template.manage", False),
    ("PUT", r"templates/[1-9]\d*", "print.template.manage", False),
    ("POST", r"previews|barcode-preview", "print.view", False),
    ("POST", r"devices", "print.device.manage", True),
    ("PUT", rf"devices/{_UUID}", "print.device.manage", True),
]


def require(permission: str, admin: bool = False) -> None:
    if not labelsystem.enabled():
        raise IntegrationError("integration_disabled", "当前未接入标签业务服务。", 404)
    if permission not in g.get("labelsystem_codes", set()) or admin and not current_user.is_admin:
        raise IntegrationError("print_forbidden", "没有此操作权限。", 403)


@labelsystem_print_bp.errorhandler(IntegrationError)
def integration_error(error: IntegrationError) -> ResponseReturnValue:
    return jsonify({"error": error.code, "detail": error.message}), error.status


@labelsystem_print_bp.route("/labelsystem/print/<path:path>", methods=["GET", "POST", "PUT"])
@login_required
def print_api(path: str) -> ResponseReturnValue:
    policy = next((p for p in _PRINT if p[0] == request.method and re.fullmatch(p[1], path)), None)
    if policy is None or request.query_string:
        return jsonify({"error": "integration_route_invalid"}), 404
    require(policy[2], policy[3])
    body = request.get_json(silent=True) if request.method != "GET" else None
    data, media = exchange(request.method, "/api/v1/print/" + path, body)
    if media == "application/json":
        import json

        value = json.loads(data)
        if value.get("success") is not True or "data" not in value:
            raise IntegrationError("integration_response_invalid", "业务服务返回内容无效。", 502)
        return jsonify(value["data"])
    if media not in {"application/pdf", "application/zip", "image/png", "image/jpeg"}:
        raise IntegrationError("integration_response_invalid", "业务服务返回文件无效。", 502)
    return Response(data, mimetype=media, headers={"Cache-Control": "private, no-store"})


@labelsystem_print_bp.post("/labelsystem/source-print/<kind>")
@login_required
def reserve_print_source(kind: str) -> ResponseReturnValue:
    require("print.first")
    from app.services.print_sources import reserve

    try:
        return jsonify(reserve(kind, request.get_json(silent=True) or {}))
    except (KeyError, TypeError, ValueError):
        return jsonify(
            {"error": "print_source_invalid", "detail": "请重新选择单据明细或库存。"}
        ), 400


@labelsystem_print_bp.get("/labelsystem/source-print/reprint")
@login_required
def original_print_label() -> ResponseReturnValue:
    require("print.reprint")
    from app.services.print_sources import original_label

    return jsonify({"label": original_label(request.args.get("query", ""))})


@labelsystem_print_bp.get("/labelsystem/designer-templates/<int:template_id>/published")
@login_required
def published_designer_template(template_id: int) -> ResponseReturnValue:
    require("print.view")
    from app.services.print_sources import published_version

    return jsonify({"versionId": published_version(template_id)})


@labelsystem_print_bp.route("/labelsystem/rules", defaults={"path": ""}, methods=["GET", "POST"])
@labelsystem_print_bp.route("/labelsystem/rules/<path:path>", methods=["GET", "POST", "PUT"])
@login_required
def rule_api(path: str) -> ResponseReturnValue:
    if request.method == "GET" and path in {"catalog", "capabilities"}:
        require("print.view")
        target = "/api/v1/label-code-rules/" + path
    elif (
        request.method in {"GET", "POST"}
        and path == ""
        or request.method == "POST"
        and path == "preview"
        or request.method == "PUT"
        and re.fullmatch(r"[1-9]\d*/draft", path)
        or request.method == "POST"
        and re.fullmatch(r"[1-9]\d*/versions/[1-9]\d*/(?:activate|retire)", path)
    ):
        require("print.template.manage", True)
        target = "/api/v1/admin/label-code-rules" + ("/" + path if path else "")
    else:
        return jsonify({"error": "integration_route_invalid"}), 404
    if request.query_string:
        return jsonify({"error": "integration_route_invalid"}), 404
    return jsonify(
        invoke(
            request.method,
            target,
            request.get_json(silent=True) if request.method != "GET" else None,
        )
    )


@labelsystem_print_bp.post("/labelsystem/designer-templates/<int:template_id>/publish")
@login_required
def publish_designer_template(template_id: int) -> ResponseReturnValue:
    require("print.template.manage")
    from app.services.labelsystem_templates import publish

    return jsonify(publish(template_id, current_user.id))
