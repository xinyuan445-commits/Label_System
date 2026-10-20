"""Source packing rejects untrusted quantities before any SQL write."""

import json
import sys
from decimal import Decimal
from types import SimpleNamespace

import pytest

from app.services import print_sources
from app.services.labelsystem_transport import IntegrationError
from app.services.print_sources import number, split_quantity


def test_pack_preserves_selected_quantity_and_optional_remainder():
    assert split_quantity("125.5", "50", True) == ["50", "50", "25.5"]
    assert sum(map(Decimal, split_quantity("125.5", "50", True))) == Decimal("125.5")
    assert split_quantity("125.5", "50", False) == ["50", "50"]


@pytest.mark.parametrize("value", ["nan", "Infinity", "-1", "1.0000001", "not-number"])
def test_invalid_quantity_rejected(value):
    with pytest.raises(IntegrationError):
        number(value)


@pytest.mark.parametrize(
    "qty,pack,tail", [(0, 1, True), (1, 0, True), (2, 3, False), (1001, 1, True)]
)
def test_invalid_pack_or_excessive_pages_rejected(qty, pack, tail):
    with pytest.raises(IntegrationError):
        split_quantity(qty, pack, tail)


def test_whole_transfer_uses_specific_reprint_guidance():
    with pytest.raises(IntegrationError) as raised:
        print_sources.ensure_bill_first_printable(
            "DBD", {"需打印数量": "0", "打印状态": "无需打印"}
        )
    assert raised.value.code == "print_source_not_required"
    assert raised.value.message == "该明细为整托调拨，沿用原标签；需要再次出纸请使用标签补打。"


def test_positive_bill_quantity_remains_first_printable():
    print_sources.ensure_bill_first_printable(
        "DBD", {"需打印数量": "30", "打印状态": "未打印"}
    )


def trace_reader(monkeypatch, rows):
    cn = SimpleNamespace(
        timeout=0, close=lambda: None, cursor=lambda: SimpleNamespace(execute=lambda *args: rows)
    )
    monkeypatch.setitem(sys.modules, "pyodbc", SimpleNamespace(connect=lambda *a, **kw: cn))
    monkeypatch.setattr(print_sources.u8_bills, "get_connection_string", lambda: "development")
    monkeypatch.setattr(print_sources.u8_bills, "validate_database", lambda value: None)
    return {"item_code": "M", "batch": "B", "warehouse_code": "W", "location_code": "L"}


def test_stock_uses_full_code_balance_and_latest_goods_location(monkeypatch):
    goods = {
        "code": "M",
        "Attribute": {"batchNumber": "B"},
        "warehouseCode": "W",
        "inventory": {"storageLocationCode": "L"},
    }
    moved = {**goods, "warehouseCode": "OTHER"}
    row = trace_reader(
        monkeypatch,
        [
            ("888|M|B|100|SN1", Decimal(35), 0, json.dumps(goods)),
            ("888|M|B|100|SN2", Decimal(50), 0, json.dumps(moved)),
        ],
    )
    assert print_sources.stock_trace(row) == (Decimal(35), ["888|M|B|100|SN1"])


@pytest.mark.parametrize("raw", [None, "broken-json", "{}"])
def test_stock_missing_latest_goods_fails_closed(monkeypatch, raw):
    row = trace_reader(monkeypatch, [("888|M|B|100|SN1", Decimal(35), 0, raw)])
    with pytest.raises(IntegrationError):
        print_sources.stock_trace(row)


def test_original_label_prefers_frozen_unit_name(monkeypatch):
    code = "888|M|B|2.00|2610200001"

    def invoke(method, path, body=None):
        if path.endswith("/labels/resolve"):
            return {
                "label": {"labelId": 7, "rowVersion": "01", "status": "ACTIVE", "quantity": "2"},
                "inventory": {"unitCode": "0101", "unitName": "张"},
            }
        if path.endswith("/original"):
            return {
                "snapshot": json.dumps(
                    {
                        "serialNo": "2610200001",
                        "quantity": "2",
                        "unitCode": "0101",
                        "variables": {"单位": "张"},
                    }
                ),
                "templateId": 3,
                "templateVersionId": 5,
                "jobId": "job",
                "outputId": "output",
            }
        if path.endswith("/templates/3"):
            return {"draft_canvas_json": json.dumps({"sourceDesigner": {"templateId": 9}})}
        raise AssertionError((method, path, body))

    monkeypatch.setattr(print_sources, "invoke", invoke)
    assert print_sources.original_label(code)["unit"] == "张"
