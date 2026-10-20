"""Verified source/packing adapter. Reads original U8 queries; only the 017 API reserves labels."""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import time
from contextlib import closing
from copy import deepcopy
from datetime import date
from decimal import Decimal, InvalidOperation
from typing import Any

from flask import current_app
from flask_login import current_user

from app.db.session import get_session
from app.services import templates, u8_bills, u8_inventory
from app.services.labelsystem_templates import normalize
from app.services.labelsystem_transport import IntegrationError, invoke


def number(value: object) -> Decimal:
    try:
        result = Decimal(str(value))
        if not result.is_finite() or result < 0 or result.quantize(Decimal("0.000001")) != result:
            raise InvalidOperation
        return result
    except (InvalidOperation, ValueError):
        raise IntegrationError("print_quantity_invalid", "数量或包装方式无效。", 400) from None


def split_quantity(quantity: object, packaging: object, remainder: bool) -> list[str]:
    qty, pack = number(quantity), number(packaging)
    if qty <= 0 or pack <= 0:
        raise IntegrationError("print_quantity_invalid", "数量或包装方式须大于0。", 400)
    full = int(qty // pack)
    if full > 1000:
        raise IntegrationError("print_too_many_labels", "一次最多打印1000张。", 400)
    result = [str(pack)] * full
    tail = qty - pack * full
    if remainder and tail > 0:
        result.append(str(tail))
    if not result:
        raise IntegrationError("print_no_labels", "请调整包装方式或勾选尾数。", 400)
    return result


def ensure_bill_first_printable(bill_type: str, row: dict[str, Any]) -> None:
    """Explain zero eligible movement lines before the generic source quota check."""
    if number(row.get("需打印数量", 0)) > 0:
        return
    if bill_type in {"DBD", "HWTZ"} and row.get("打印状态") == "无需打印":
        movement = "整托调拨" if bill_type == "DBD" else "整托货位调整"
        raise IntegrationError(
            "print_source_not_required",
            f"该明细为{movement}，沿用原标签；需要再次出纸请使用标签补打。",
            409,
        )
    raise IntegrationError(
        "print_source_not_required",
        "该明细没有可首打数量；需要再次出纸请使用标签补打。",
        409,
    )


def published_version(template_id: int) -> int:
    try:
        source = templates.get(get_session(), template_id, requesting_user_id=current_user.id)
    except (templates.TemplateAccessError, templates.TemplateNotFoundError):
        raise IntegrationError("print_template_invalid", "模板不可用。", 404) from None
    digest = hashlib.sha256(
        json.dumps(
            {
                "canvas": normalize(deepcopy(source.canvas_data)),
                "name": source.name,
                "width": str(source.width_mm),
                "height": str(source.height_mm),
            },
            sort_keys=True,
            ensure_ascii=False,
        ).encode()
    ).hexdigest()
    for row in invoke("GET", "/api/v1/print/templates"):
        if not row.get("publishedVersionId"):
            continue
        detail = invoke("GET", "/api/v1/print/templates/" + str(row["id"]))
        canvas = json.loads(detail["draft_canvas_json"])
        marker = canvas.get("sourceDesigner", {})
        if (
            marker.get("system") == "New_Label_Print_System"
            and marker.get("templateId") == template_id
        ):
            if marker.get("sourceDigest") != digest or row["publishedVersionId"] == marker.get(
                "previousPublishedVersionId"
            ):
                raise IntegrationError(
                    "print_template_unpublished", "模板已修改，请重新发布。", 409
                )
            return int(row["publishedVersionId"])
    raise IntegrationError("print_template_unpublished", "请先在模板管理中发布此模板。", 409)


def _unit(code: str) -> str:
    import pyodbc

    connection = u8_bills.get_connection_string()
    u8_bills.validate_database(connection)
    with closing(pyodbc.connect(connection, timeout=5)) as cn:
        cn.timeout = 20
        value = (
            cn.cursor()
            .execute("SELECT cComUnitCode FROM Inventory WHERE cInvCode=?", code)
            .fetchone()
        )
    if not value or not value[0]:
        raise IntegrationError("print_material_invalid", "物料缺少计量单位。", 409)
    return str(value[0]).strip()


def stock_trace(row: dict[str, Any]) -> tuple[Decimal, list[str]]:
    import pyodbc

    connection = u8_bills.get_connection_string()
    u8_bills.validate_database(connection)
    # Aggregate by full Code, then use the latest flow's Goods for the current location.
    sql = """
      WITH b AS(SELECT traceBackCode,SUM(TRY_CONVERT(decimal(18,6),[sum])) qty,MAX(id) latest,
       SUM(CASE WHEN TRY_CONVERT(decimal(18,6),[sum]) IS NULL THEN 1 ELSE 0 END) bad
       FROM TraceBackOnlyCode GROUP BY traceBackCode)
      SELECT b.traceBackCode,b.qty,b.bad,g.[json] FROM b JOIN TraceBackOnlyCode f ON f.id=b.latest
      LEFT JOIN TraceBackGoods g ON g.pdaGoodsUuid=f.pdaGoodsUuid
      CROSS APPLY(VALUES(CONVERT(nvarchar(max),g.[json]))) raw(value)
      CROSS APPLY(VALUES(CASE WHEN ISJSON(raw.value)=1 THEN raw.value ELSE '{}' END)) j(value)
      WHERE (COALESCE(JSON_VALUE(j.value,'$.materialCode'),JSON_VALUE(j.value,'$.code'))=?
       AND COALESCE(JSON_VALUE(j.value,'$.batchNo'),
         JSON_VALUE(j.value,'$.Attribute.batchNumber'),'')=?
       AND JSON_VALUE(j.value,'$.warehouseCode')=?
       AND COALESCE(JSON_VALUE(j.value,'$.storageLocationCode'),
         JSON_VALUE(j.value,'$.inventory.storageLocationCode'),'')=?)
       OR (LEFT(b.traceBackCode,LEN(?))=? AND (
         ISNULL(ISJSON(raw.value),0)<>1 OR
         COALESCE(JSON_VALUE(j.value,'$.materialCode'),JSON_VALUE(j.value,'$.code'),'')<>? OR
         COALESCE(JSON_VALUE(j.value,'$.batchNo'),
           JSON_VALUE(j.value,'$.Attribute.batchNumber'),'')<>? OR
         JSON_VALUE(j.value,'$.warehouseCode') IS NULL));
    """
    total = Decimal(0)
    codes = []
    with closing(pyodbc.connect(connection, timeout=5)) as cn:
        cn.timeout = 30
        prefix = "888|" + row["item_code"] + "|" + row["batch"] + "|"
        for code, qty, bad, raw_goods in cn.cursor().execute(
            sql,
            row["item_code"],
            row["batch"],
            row["warehouse_code"],
            row["location_code"],
            prefix,
            prefix,
            row["item_code"],
            row["batch"],
        ):
            if bad or qty is None or qty < 0 or code in codes:
                raise IntegrationError("print_stock_review", "已有标签余额需核对。", 409)
            if qty > 0:
                try:
                    goods = json.loads(raw_goods)
                    attribute = goods.get("Attribute") or {}
                    inventory = goods.get("inventory") or {}
                    material = goods.get("materialCode") or goods.get("code")
                    batch = goods.get("batchNo", attribute.get("batchNumber", ""))
                    warehouse = goods.get("warehouseCode")
                    location = goods.get(
                        "storageLocationCode", inventory.get("storageLocationCode", "")
                    )
                    if material != row["item_code"] or batch != row["batch"] or not warehouse:
                        raise ValueError
                except (ValueError, TypeError, AttributeError):
                    raise IntegrationError(
                        "print_stock_review", "已有标签位置需核对。", 409
                    ) from None
                if warehouse != row["warehouse_code"] or location != row["location_code"]:
                    continue
                total += Decimal(qty)
                codes.append(str(code))
    return total, codes


def reserve(kind: str, body: dict[str, Any]) -> dict[str, Any]:
    cap = invoke("GET", "/api/v1/print/capabilities")
    if not cap.get("sourceEnabled") or u8_bills.database_name() != "ufdata_888_2021":
        raise IntegrationError("print_source_disabled", "当前环境未开放来源打印。", 409)
    key = current_app.config.get("PRINT_SOURCE_SIGNING_KEY", "")
    if len(key) < 32:
        raise IntegrationError("print_source_disabled", "来源打印尚未配置。", 503)
    import uuid

    uuid.UUID(str(body["deviceId"]))
    printer = body["printer"]
    if not isinstance(printer, str) or not printer.strip() or len(printer) > 200:
        raise ValueError("Invalid printer")
    version = published_version(int(body["template_id"]))
    rows = []
    seen = set()
    remainder = bool(body.get("include_remainder", True))
    if kind == "bill":
        typ = str(body["bill_type"])
        bid = int(body["bill_id"])
        doc = str(body["bill_number"])
        headers, cut = u8_bills.list_bills(
            typ,
            code=doc,
            start_date=date.fromisoformat(body["bill_date"]),
            end_date=date.fromisoformat(body["bill_date"]),
        )
        if cut or not any(int(r["ID"]) == bid and str(r["单据号"]) == doc for r in headers):
            raise IntegrationError("print_bill_changed", "单据已变化，请重新查询。", 409)
        details, cut = u8_bills.list_pending_details(typ, bid, doc)
        if cut:
            raise IntegrationError("print_bill_changed", "单据明细不完整，请缩小查询范围。", 409)
        for selection in body["lines"]:
            index = int(selection["index"])
            if index < 0 or index >= len(details) or index in seen:
                raise IntegrationError("print_line_invalid", "明细选择无效。", 400)
            seen.add(index)
            row = details[index]
            detail_id = str(row.get("明细ID") or "")
            if (
                not detail_id
                or str(selection.get("detail_id")) != detail_id
                or selection["item_code"] != row["存货编码"]
                or selection.get("batch", "") != str(row.get("批号") or "")
            ):
                raise IntegrationError("print_bill_changed", "单据明细已变化，请重新查询。", 409)
            ensure_bill_first_printable(typ, row)
            quantities = split_quantity(selection["quantity"], selection["packaging"], remainder)
            identity = ["BILL", typ, str(bid), detail_id]
            rows.append(
                {
                    "sourceKey": hashlib.sha256("|".join(identity).encode()).hexdigest().upper(),
                    "sourceKind": "BILL",
                    "documentId": typ + ":" + str(bid),
                    "documentNo": doc,
                    "detailId": detail_id,
                    "materialCode": row["存货编码"],
                    "unitCode": _unit(row["存货编码"]),
                    "batchNo": str(row.get("批号") or ""),
                    "warehouseCode": str(row.get("仓库编码") or ""),
                    "locationCode": str(row.get("货位编码") or ""),
                    "eligibleQuantity": str(row["需打印数量"]),
                    "legacyQuantity": str(row["已打印数量"]),
                    "legacyCodes": row.get("已打印条码", []),
                    "amounts": quantities,
                }
            )
    elif kind == "stock":
        selected = body["lines"]
        stock = u8_inventory.get_selected(
            [(str(r["source"]), int(r["stock_id"])) for r in selected]
        )
        by_key = {(r["source"], int(r["stock_id"])): r for r in stock}
        for selection in selected:
            identity_key = (str(selection["source"]), int(selection["stock_id"]))
            row = by_key.get(identity_key)
            if (
                identity_key in seen
                or not row
                or row["item_code"] != selection["item_code"]
                or row["batch"] != selection.get("batch", "")
            ):
                raise IntegrationError("print_stock_changed", "库存已变化，请重新查询。", 409)
            seen.add(identity_key)
            unit = _unit(row["item_code"])
            legacy, codes = stock_trace(row)
            identity = [
                "STOCK",
                row["item_code"],
                row["batch"],
                unit,
                row["warehouse_code"],
                row["location_code"],
            ]
            rows.append(
                {
                    "sourceKey": hashlib.sha256("|".join(identity).encode()).hexdigest().upper(),
                    "sourceKind": "STOCK",
                    "documentId": "STOCK:"
                    + str(selection["source"])
                    + ":"
                    + str(selection["stock_id"]),
                    "documentNo": "STOCK-"
                    + str(selection["source"])
                    + "-"
                    + str(selection["stock_id"]),
                    "detailId": str(selection["stock_id"]),
                    "materialCode": row["item_code"],
                    "unitCode": unit,
                    "batchNo": row["batch"],
                    "warehouseCode": row["warehouse_code"],
                    "locationCode": row["location_code"],
                    "eligibleQuantity": str(row["quantity"]),
                    "legacyQuantity": str(legacy),
                    "legacyCodes": codes,
                    "amounts": split_quantity(
                        selection["quantity"], selection["packaging"], remainder
                    ),
                }
            )
    else:
        raise IntegrationError("print_source_invalid", "来源无效。", 400)
    user = invoke("GET", "/api/v1/auth/me")
    payload = {
        "clientRequestId": body["clientRequestId"],
        "planHash": hashlib.sha256(json.dumps(body, sort_keys=True, ensure_ascii=False).encode())
        .hexdigest()
        .upper(),
        "userId": user["id"],
        "expiresAt": int(time.time()) + 120,
        "rows": rows,
    }
    raw = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode()
    result = invoke(
        "POST",
        "/api/v1/print/source-targets",
        {
            "payload": base64.b64encode(raw).decode(),
            "signature": hmac.new(key.encode(), raw, hashlib.sha256).hexdigest(),
        },
    )
    return {
        **result,
        "templateVersionId": version,
        "deviceId": body["deviceId"],
        "printer": body["printer"],
    }


def original_label(query: str) -> dict[str, Any]:
    query = query.strip()
    if not query or len(query) > 200:
        raise IntegrationError("print_code_invalid", "请输入完整条码或流水号。", 400)
    code = query
    if "|" not in query:
        import pyodbc

        connection = u8_bills.get_connection_string()
        u8_bills.validate_database(connection)
        with closing(pyodbc.connect(connection, timeout=5)) as cn:
            rows = (
                cn.cursor()
                .execute(
                    "SELECT DISTINCT TOP(2) traceBackCode FROM TraceBackOnlyCode WHERE sn=?", query
                )
                .fetchall()
            )
        if len(rows) != 1:
            raise IntegrationError(
                "print_label_ambiguous", "流水号未找到或对应多个标签，请使用完整条码。", 409
            )
        code = str(rows[0][0])
    resolved = invoke("POST", "/api/v1/print/labels/resolve", {"code": code})
    label = resolved["label"]
    inventory = resolved.get("inventory", {})
    original = invoke("GET", "/api/v1/print/labels/" + str(label["labelId"]) + "/original")
    snapshot = json.loads(original["snapshot"]) if original else {}
    parts = code.split("|")
    quantity = snapshot.get("quantity") or (parts[3] if len(parts) == 5 else "")
    if not quantity:
        raise IntegrationError("print_original_required", "原标签信息不完整，请核对历史依据。", 409)
    draft_id = None
    if original:
        formal = invoke("GET", "/api/v1/print/templates/" + str(original["templateId"]))
        draft_id = (
            json.loads(formal["draft_canvas_json"]).get("sourceDesigner", {}).get("templateId")
        )
    return {
        "sn": snapshot.get("serialNo") or parts[-1],
        "barcode": code,
        "document_number": snapshot.get("sourceDocumentNo", ""),
        "item_code": snapshot.get("materialCode") or inventory.get("materialCode", ""),
        "item_name": snapshot.get("materialName") or inventory.get("materialName", ""),
        "batch": snapshot.get("batchNo") or inventory.get("batchNo", ""),
        "quantity": quantity,
        "unit": (
            (snapshot.get("variables") or {}).get("单位")
            or inventory.get("unitName")
            or snapshot.get("unitCode")
            or inventory.get("unitCode", "")
        ),
        "warehouse": snapshot.get("warehouseCode") or inventory.get("warehouseCode", ""),
        "location": snapshot.get("locationCode") or inventory.get("locationCode", ""),
        "specification": "",
        "printed_at": snapshot.get("frozenAt", ""),
        "printed_by": "",
        "line_number": None,
        "template_id": draft_id,
        "label_id": label["labelId"],
        "row_version": label["rowVersion"],
        "status": label["status"],
        "balance": label["quantity"],
        "original_job_id": original["jobId"] if original else None,
        "original_output_id": original["outputId"] if original else None,
        "original_template_version_id": original["templateVersionId"] if original else None,
        "original_variables": snapshot.get("variables", {}),
        "requires_baseline": original is None,
    }
