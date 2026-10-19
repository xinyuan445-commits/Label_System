"""Strict, database-free adapter around the imported ReportLab renderer."""
from __future__ import annotations

import base64
import hashlib
import io
import math
import re
import tempfile
import threading
from copy import deepcopy
from datetime import datetime
from pathlib import Path
from typing import Any

import pypdfium2
from PIL import Image
from reportlab.lib.units import mm
from reportlab.pdfgen.canvas import Canvas

from . import pdf_renderer as pdf
from .assets import Asset, _asset_dir
from .placeholders import evaluate_date_key, is_plain_date_key

VERSION = "label-renderer/1"
PLACEHOLDER = re.compile(r"\{\{\s*([^}]+?)\s*\}\}")
_render_lock = threading.RLock()
_loaded_fonts = None

def fonts() -> dict[str, Any]:
    with _render_lock:
        return _fonts()

def _fonts() -> dict[str, Any]:
    global _loaded_fonts
    pdf._ensure_fonts()
    candidates = list(pdf._CJK_FONT_PATHS) + list(pdf._CJK_BOLD_FONT_PATHS)
    for name, _ in pdf._FONT_FILES.values():
        candidates.append(str(Path(pdf._LIBERATION_DIR) / name))
    win = Path(pdf.os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"
    candidates += [str(win / n) for n in ("arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf",
        "times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf", "cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf")]
    found = {}
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            found[Path(candidate).name] = hashlib.sha256(Path(candidate).read_bytes()).hexdigest().upper()
    result = {"version": VERSION, "files": found, "cjk": bool(pdf._cjk_font)}
    if _loaded_fonts is not None and result != _loaded_fonts:
        raise ValueError("font files changed; restart renderer before publishing")
    _loaded_fonts = result
    return result

def substitute(value: str, row: dict, frozen: str) -> str:
    today = datetime.fromisoformat(frozen).date()
    def replace(match):
        key = match.group(1).strip()
        computed = evaluate_date_key(key, today=today)
        if computed is not None and not (is_plain_date_key(key) and key in row):
            return computed
        if key not in row or row[key] is None or str(row[key]) == "":
            raise ValueError("missing variable: " + key)
        return str(row[key])
    return PLACEHOLDER.sub(replace, value)

def prepared_objects(canvas: dict, row: dict, frozen: str) -> list[dict]:
    result = deepcopy(canvas["objects"])
    for obj in result:
        if obj.get("printable") is False:
            continue
        for field in ("text", "data"):
            if isinstance(obj.get(field), str):
                obj[field] = substitute(obj[field], row, frozen)
        if isinstance(obj.get("cells"), list):
            obj["cells"] = [[substitute(c, row, frozen) if isinstance(c, str) else c for c in line]
                            for line in obj["cells"]]
    return result

def validate_canvas(canvas: dict, width: float, height: float) -> None:
    if not all(math.isfinite(x) and 0 < x <= 1000 for x in (width, height)):
        raise ValueError("invalid dimensions")
    if canvas.get("version") != 1 or canvas.get("stage", {}).get("width_mm") != width or canvas.get("stage", {}).get("height_mm") != height:
        raise ValueError("canvas dimensions differ")
    objects = canvas.get("objects")
    if not isinstance(objects, list) or not 1 <= len(objects) <= 50:
        raise ValueError("invalid objects")
    ids = [str(o.get("id", "")) for o in objects]
    if any(not x for x in ids) or len(set(ids)) != len(ids):
        raise ValueError("duplicate or missing object identity")
    for obj in objects:
        if obj.get("type") not in ("text", "rect", "line", "image", "barcode", "table"):
            raise ValueError("unknown object")
        if obj.get("printable") is False:
            continue
        for key in ("x", "y", "width", "height", "fontSize", "rotation"):
            if key in obj and (not isinstance(obj[key], (int, float)) or not math.isfinite(obj[key])):
                raise ValueError("invalid geometry")
        x, y = obj.get("x", 0), obj.get("y", 0)
        if x < 0 or y < 0 or x >= width or y >= height:
            raise ValueError("object outside label")
        if x + obj.get("width", 0) > width + .01 or y + obj.get("height", 0) > height + .01:
            raise ValueError("object outside label")

def render(request: dict) -> dict:
    # ReportLab font registration and PDFium use process-global native state.
    with _render_lock:
        return _render(request)

def _render(request: dict) -> dict:
    width, height = float(request["widthMm"]), float(request["heightMm"])
    canvas = request["canvasData"]
    validate_canvas(canvas, width, height)
    actual_fonts = fonts()
    if request.get("fontManifest") is not None and request["fontManifest"] != actual_fonts:
        raise ValueError("published fonts changed")
    outputs = request.get("outputs", [])
    if not 1 <= len(outputs) <= 100 or len({o["outputId"] for o in outputs}) != len(outputs):
        raise ValueError("invalid outputs")
    pages = []
    total = 0
    with tempfile.TemporaryDirectory(prefix="label-render-") as temporary:
        mapping = {}
        for item in request.get("assets", []):
            raw = base64.b64decode(item["dataBase64"], validate=True)
            if not 0 < len(raw) <= 5 * 1024 * 1024 or hashlib.sha256(raw).hexdigest().upper() != item["sha256"]:
                raise ValueError("asset digest differs")
            with Image.open(io.BytesIO(raw)) as im:
                if im.format not in ("PNG", "JPEG") or im.width * im.height > 40_000_000:
                    raise ValueError("unsupported image")
                im.verify()
            name = hashlib.sha256(raw).hexdigest() + ".image"
            (Path(temporary) / name).write_bytes(raw)
            mapping[int(item["id"])] = Asset(name)
        token = _asset_dir.set(Path(temporary))
        try:
            for item in outputs:
                objects = prepared_objects(canvas, item["variables"], item["frozenAt"])
                if not request.get("preview") and not any(o.get("type") == "barcode" and
                    o.get("printable") is not False and o.get("data") == item["code"] for o in objects):
                    raise ValueError("full business code barcode required")
                buffer = io.BytesIO()
                page = Canvas(buffer, pagesize=(width * mm, height * mm), invariant=1)
                warnings = []
                for obj in objects:
                    if obj.get("printable") is False:
                        continue
                    texts = str(obj.get("text", "")) + str(obj.get("cells", ""))
                    if pdf._CJK_RE.search(texts) and not pdf._cjk_font:
                        raise ValueError("CJK font unavailable")
                    kind = obj["type"]
                    if kind == "text": pdf._draw_text(page, obj, height * mm, warnings=warnings)
                    elif kind == "rect": pdf._draw_rect(page, obj, height * mm)
                    elif kind == "line": pdf._draw_line(page, obj, height * mm)
                    elif kind == "table": pdf._draw_table(page, obj, height * mm, warnings=warnings)
                    elif kind == "image": pdf._draw_image(page, obj, height * mm, mapping.get)
                    elif kind == "barcode": pdf._draw_barcode(page, obj, height * mm)
                if warnings:
                    raise ValueError("text overflow")
                page.showPage()
                page.save()
                document = buffer.getvalue()
                with pypdfium2.PdfDocument(document) as doc:
                    if len(doc) != 1: raise ValueError("page count mismatch")
                    bitmap = doc[0].render(scale=300 / 72).to_pil()
                    raster = io.BytesIO()
                    bitmap.save(raster, format="PNG")
                    image = raster.getvalue()
                total += len(document) + len(image)
                if total > 128 * 1024 * 1024: raise ValueError("render output too large")
                pages.append({"outputId": item["outputId"], "pdf": base64.b64encode(document).decode(),
                              "png": base64.b64encode(image).decode()})
        finally:
            _asset_dir.reset(token)
    return {"version": VERSION, "fontManifest": actual_fonts, "pages": pages}
