"""Internal renderer. Explicit launch only; never connects to a database."""
import hmac
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

from .service import fonts, render
from .barcodes import BarcodeType, render_png

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_args):
        pass  # Do not log payloads, credentials or business content.

    def do_POST(self):
        key = os.environ.get("LABEL_RENDERER_API_KEY", "")
        if not key or not hmac.compare_digest(self.headers.get("X-Renderer-Key", ""), key):
            self.send_error(403)
            return
        try:
            count = int(self.headers.get("Content-Length", "0"))
            if not 0 < count <= 48 * 1024 * 1024:
                raise ValueError("payload size")
            request = json.loads(self.rfile.read(count))
            path = urlsplit(self.path).path
            if path == "/fonts":
                result = fonts()
            elif path == "/render":
                result = render(request)
            elif path == "/barcode":
                result = {"dataBase64": __import__("base64").b64encode(render_png(
                    BarcodeType(request["type"]), request["data"])).decode()}
            elif path == "/image-info":
                import base64
                import io
                from PIL import Image
                raw = base64.b64decode(request["dataBase64"], validate=True)
                if not 0 < len(raw) <= 5 * 1024 * 1024:
                    raise ValueError("image size")
                with Image.open(io.BytesIO(raw)) as image:
                    if image.format not in ("PNG", "JPEG") or image.width * image.height > 40_000_000:
                        raise ValueError("image type")
                    result = {"width": image.width, "height": image.height,
                              "mediaType": "image/png" if image.format == "PNG" else "image/jpeg"}
                    image.verify()
            else:
                self.send_error(404)
                return
            data = json.dumps(result, ensure_ascii=False).encode("utf-8")
            self.send_response(200)
        except Exception:
            data = b'{"error":"PRINT_RENDER_INVALID"}'
            self.send_response(422)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

if __name__ == "__main__":
    if not os.environ.get("LABEL_RENDERER_API_KEY"):
        raise SystemExit("LABEL_RENDERER_API_KEY is required")
    # Bind loopback by default. No U8 or application database connection is used.
    ThreadingHTTPServer(("127.0.0.1", int(os.environ.get("LABEL_RENDERER_PORT", "18780"))), Handler).serve_forever()
