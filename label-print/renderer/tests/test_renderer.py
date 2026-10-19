import base64
from copy import deepcopy
import io

import pypdfium2
import pytest
from PIL import Image
from reportlab.lib.units import mm

from label_renderer.service import fonts, prepared_objects, render, substitute

def request():
    return {"widthMm": 80, "heightMm": 50, "canvasData": {
        "version": 1, "stage": {"width_mm": 80, "height_mm": 50}, "objects": [
            {"id": "code", "type": "barcode", "x": 2, "y": 2, "width": 20, "height": 20,
             "barcodeType": "qr", "data": "{{条码}}"},
            {"id": "qty", "type": "text", "x": 25, "y": 3, "fontSize": 3,
             "fontFamily": "Arial", "fill": "#000000", "text": "Q={{数量}}"}
        ]}, "outputs": [{"outputId": "output-1", "code": "888|MAT|B|100|0001",
                        "variables": {"条码": "888|MAT|B|100|0001", "数量": "100"},
                        "frozenAt": "2026-10-19T08:30:00+08:00"}]}

def test_frozen_original_quantity_is_independent_of_current_balance():
    result = render(request())
    pdf = base64.b64decode(result["pages"][0]["pdf"])
    with pypdfium2.PdfDocument(pdf) as document:
        assert len(document) == 1
        assert document[0].get_size() == pytest.approx((80 * mm, 50 * mm), abs=.01)
        assert "Q=100" in document[0].get_textpage().get_text_range()
    with Image.open(io.BytesIO(base64.b64decode(result["pages"][0]["png"]))) as image:
        assert image.width == pytest.approx(80 / 25.4 * 300, abs=1)
        assert image.height == pytest.approx(50 / 25.4 * 300, abs=1)

def test_identical_snapshot_renders_identical_pages():
    first, second = render(request()), render(request())
    assert first == second

def test_missing_variable_fails_entire_output():
    value = request()
    del value["outputs"][0]["variables"]["数量"]
    with pytest.raises(ValueError, match="missing variable"): render(value)

def test_full_business_code_required():
    value = request()
    value["canvasData"]["objects"][0]["data"] = "0001"
    with pytest.raises(ValueError, match="full business code"): render(value)

def test_font_drift_blocks_reprint():
    value = request()
    value["fontManifest"] = {**fonts(), "cjk": "changed"}
    with pytest.raises(ValueError, match="published fonts changed"): render(value)

def test_dates_are_frozen_and_source_date_column_wins():
    assert substitute("{{date}}", {"date": "2026-01-01"}, "2026-10-19T00:00:00") == "2026-01-01"
    assert substitute("{{date}}", {}, "2026-10-19T00:00:00") == "19.10.2026"
    assert substitute("{{date:YYYY-MM-DD}}", {}, "2026-10-19T00:00:00") == "2026-10-19"

def test_nonprinting_reference_does_not_require_image_or_variables():
    value = request()
    value["canvasData"]["objects"].append({"id": "reference", "type": "image", "x": 0, "y": 0,
                                         "assetId": 999, "printable": False, "data": "{{missing}}"})
    assert render(value)["pages"]

def test_duplicate_outputs_fail_before_rendering():
    value = request()
    value["outputs"].append(deepcopy(value["outputs"][0]))
    with pytest.raises(ValueError, match="invalid outputs"): render(value)

def test_unknown_object_never_silently_disappears():
    value = request()
    value["canvasData"]["objects"][0]["type"] = "custom"
    with pytest.raises(ValueError, match="unknown object"): render(value)
