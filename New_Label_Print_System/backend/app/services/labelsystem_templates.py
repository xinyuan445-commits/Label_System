"""Publish original designer drafts into immutable 017 versions, without a new ledger."""

from __future__ import annotations

import base64
import hashlib
import json
import re
import threading
from copy import deepcopy
from typing import Any

from app.db.session import get_session
from app.services import assets, templates
from app.services.labelsystem_transport import IntegrationError, invoke
from app.services.placeholders import DATE_KEY_RE

_publish_lock = threading.RLock()
_ROOT = "/api/v1/print"
_ALIASES = {
    "料号": "物料编码",
    "存货编码": "物料编码",
    "存货名称": "物料名称",
    "品名": "物料名称",
    "单据号": "单据编码",
    "单号": "单据编码",
    "流水号": "sn",
    "SN": "sn",
    "原始数量": "数量",
}
_VARIABLES = {
    "条码",
    "Code",
    "sn",
    "数量",
    "quantity",
    "单位",
    "物料编码",
    "物料名称",
    "批号",
    "单据编码",
    "行号",
    "仓库编码",
    "货位编码",
}


def _formal_templates() -> list[dict[str, Any]]:
    active = invoke("GET", _ROOT + "/templates")
    archived = invoke("GET", _ROOT + "/templates/archived")
    return [*active, *archived]


def _enable(template_id: int) -> None:
    current = invoke("GET", _ROOT + "/templates/" + str(template_id))
    if current.get("is_enabled"):
        return
    invoke(
        "PUT",
        _ROOT + "/templates/" + str(template_id) + "/state",
        {"expectedRowVersion": current["row_version"], "enabled": True},
    )


def normalize(value: Any) -> Any:  # noqa: ANN401 -- recursive designer JSON
    if isinstance(value, dict):
        return {key: normalize(item) for key, item in value.items()}
    if isinstance(value, list):
        return [normalize(item) for item in value]
    if isinstance(value, str):

        def replace(match: re.Match[str]) -> str:
            key = match.group(1).strip()
            key = _ALIASES.get(key, key)
            if key not in _VARIABLES and not DATE_KEY_RE.fullmatch(key):
                raise IntegrationError(
                    "print_variable_unsupported", "正式打印暂不支持变量：" + key, 400
                )
            return "{{" + key + "}}"

        return re.sub(r"\{\{\s*([^}]+?)\s*\}\}", replace, value)
    return value


def publish(template_id: int, user_id: int) -> dict[str, Any]:
    # Cross-process duplicate publication cannot duplicate inventory or a print job;
    # authoritative edits use SQL row versions and each published version is immutable.
    with _publish_lock:
        db = get_session()
        try:
            source = templates.get(db, template_id, requesting_user_id=user_id)
        except templates.TemplateNotFoundError:
            raise IntegrationError("template_not_found", "模板不存在。", 404) from None
        except templates.TemplateAccessError:
            raise IntegrationError("template_forbidden", "没有此模板的访问权限。", 403) from None
        canvas = deepcopy(source.canvas_data)
        canvas = normalize(canvas)
        objects = canvas.get("objects", [])
        objects = [o for o in objects if isinstance(o, dict) and o.get("printable") is not False]
        if not any(
            o.get("type") == "barcode"
            and re.fullmatch(r"\{\{\s*(?:Code|条码)\s*\}\}", str(o.get("data", "")))
            for o in objects
        ):
            raise IntegrationError(
                "print_full_code_required", "请在模板中添加内容为 {{条码}} 的条码或二维码。", 400
            )
        digest = hashlib.sha256(
            json.dumps(
                {
                    "canvas": canvas,
                    "name": source.name,
                    "width": str(source.width_mm),
                    "height": str(source.height_mm),
                },
                sort_keys=True,
                ensure_ascii=False,
            ).encode()
        ).hexdigest()
        target = None
        for row in _formal_templates():
            candidate = invoke("GET", _ROOT + "/templates/" + str(row["id"]))
            draft = json.loads(candidate["draft_canvas_json"])
            marker = draft.get("sourceDesigner", {})
            if (
                marker.get("system") == "New_Label_Print_System"
                and marker.get("templateId") == template_id
            ):
                target = candidate
                if marker.get("sourceDigest") == digest:
                    published = candidate.get("published_version_id")
                    if published is not None and published != marker.get(
                        "previousPublishedVersionId"
                    ):
                        _enable(int(candidate["id"]))
                        return {
                            "templateId": candidate["id"],
                            "versionId": published,
                            "reused": True,
                        }
                    version = invoke(
                        "POST",
                        _ROOT + "/templates/" + str(candidate["id"]) + "/publish",
                        {"expectedRowVersion": candidate["row_version"]},
                    )
                    return {
                        "templateId": candidate["id"],
                        "versionId": version["id"],
                        "reused": False,
                    }
                break
        for obj in objects:
            if obj.get("type") != "image":
                continue
            asset = assets.get_asset(db, int(obj.get("assetId", 0)))
            if asset is None:
                raise IntegrationError("print_asset_missing", "模板图片缺失。", 400)
            if asset.mime_type not in {"image/png", "image/jpeg"}:
                raise IntegrationError(
                    "print_asset_unsupported", "正式打印图片请使用PNG或JPEG。", 400
                )
            folder = assets.assets_dir().resolve()
            file = (folder / asset.storage_filename).resolve()
            if not file.is_relative_to(folder) or not file.is_file():
                raise IntegrationError("print_asset_missing", "模板图片文件缺失。", 400)
            raw = file.read_bytes()
            if hashlib.sha256(raw).hexdigest() != asset.sha256:
                raise IntegrationError("print_asset_changed", "模板图片校验失败，请重新上传。", 409)
            saved = invoke(
                "POST",
                _ROOT + "/assets",
                {
                    "mediaType": asset.mime_type,
                    "dataBase64": base64.b64encode(raw).decode(),
                    "widthPx": asset.width_px,
                    "heightPx": asset.height_px,
                },
            )
            obj["assetId"] = saved["id"]
        canvas["objects"] = objects
        canvas["sourceDesigner"] = {
            "system": "New_Label_Print_System",
            "templateId": template_id,
            "sourceDigest": digest,
            "previousPublishedVersionId": target.get("published_version_id") if target else None,
        }
        payload = {
            "name": source.name,
            "canvasData": canvas,
            "widthMm": float(source.width_mm),
            "heightMm": float(source.height_mm),
            "expectedRowVersion": target["row_version"] if target else None,
        }
        saved = invoke(
            "PUT" if target else "POST",
            _ROOT + "/templates" + ("/" + str(target["id"]) if target else ""),
            payload,
        )
        version = invoke(
            "POST",
            _ROOT + "/templates/" + str(saved["id"]) + "/publish",
            {"expectedRowVersion": saved["row_version"]},
        )
        _enable(int(saved["id"]))
        return {"templateId": saved["id"], "versionId": version["id"], "reused": False}
