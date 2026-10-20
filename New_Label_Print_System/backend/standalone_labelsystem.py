"""Independent development server using Label system accounts and account 888.

No admin password is seeded and no print helper is started. Existing legacy
print preparation, confirmation and queue routes are blocked in this mode.
"""

from __future__ import annotations

import json
import os
import secrets
from pathlib import Path
from urllib.parse import urlsplit

from flask import Response, abort, send_from_directory


def main() -> None:
    config_path = Path(os.environ["NEW_LABEL_SYSTEM_CONFIG"])
    settings = json.loads(config_path.read_text(encoding="utf-8-sig"))
    if settings["u8Database"].casefold() != "ufdata_888_2021":
        raise SystemExit("Standalone development requires account 888")
    source_host = str(settings.get("sourceHost", "")).strip()
    api_url = str(settings["labelSystemApiUrl"]).rstrip("/")
    api_uri = urlsplit(api_url)
    u8_host = str(settings.get("u8Server", "")).split(",", 1)[0].strip()
    if (
        not source_host
        or api_uri.hostname != source_host
        or u8_host.casefold() != source_host.casefold()
    ):
        raise SystemExit("Label system API and U8 must use the configured source host")
    if settings.get("hosts") != ["127.0.0.1"] or int(settings.get("port", 0)) != 18003:
        raise SystemExit("Local printing workspace must only listen on 127.0.0.1:18003")
    os.environ["LABELSYSTEM_API_URL"] = api_url
    if api_uri.scheme == "http":
        allowed_http_origin = str(settings.get("labelSystemApiHttpOrigin", "")).rstrip("/")
        if allowed_http_origin != api_url:
            raise SystemExit("The fixed HTTP Label system origin must be explicitly approved")
        os.environ["LABELSYSTEM_API_HTTP_ORIGIN"] = allowed_http_origin
    os.environ["U8_DATABASE"] = "ufdata_888_2021"
    os.environ["U8_BARCODE_ACCOUNT_CODE"] = "888"
    for name in ("SERVER", "USER", "PASSWORD", "ODBC_DRIVER"):
        if settings.get("u8" + name.title().replace("_", "")):
            os.environ["U8_" + name] = settings["u8" + name.title().replace("_", "")]
    data = Path(settings["dataDirectory"])
    data.mkdir(parents=True, exist_ok=True)
    for name in ("UPLOADS", "ASSETS", "PDFS"):
        folder = data / name.lower()
        folder.mkdir(exist_ok=True)
        os.environ[name + "_DIR"] = str(folder)
    if settings.get("regularFont"):
        os.environ["LABEL_CJK_FONT_PATH"] = settings["regularFont"]
    if settings.get("boldFont"):
        os.environ["LABEL_CJK_BOLD_FONT_PATH"] = settings["boldFont"]
    secret_file = data / "secret.key"
    if not secret_file.exists():
        secret_file.write_text(secrets.token_hex(32), encoding="ascii")

    from app import models  # noqa: F401 — register all existing application models
    from app.config import Config
    from app.db.base import Base
    from app.db.session import get_engine, get_session
    from app.factory import create_app
    from local_server import seed_label_formats, serve_on_hosts

    app = create_app(
        Config(
            database_url="sqlite:///" + (data / "pilot.db").as_posix(),
            redis_url="redis://127.0.0.1:6379/0",
            flask_env="development",
            secret_key=secret_file.read_text(encoding="ascii"),
            cookie_secure=False,
            shared_templates=True,
        ),
        use_redis_sessions=False,
    )
    app.config["HEALTH_SKIP_REDIS"] = True
    app.config["PRINT_SOURCE_SIGNING_KEY"] = settings.get("printSourceSigningKey", "")
    with app.app_context():
        Base.metadata.create_all(get_engine())
        seed_label_formats(get_session())
    dist = Path(__file__).resolve().parents[1] / "frontend" / "dist"

    @app.get("/")
    def index() -> Response:
        return send_from_directory(dist, "index.html")

    @app.get("/<path:path>")
    def spa(path: str) -> Response:
        if path.startswith("api/"):
            abort(404)
        return send_from_directory(dist, path if (dist / path).is_file() else "index.html")

    serve_on_hosts(app, settings["hosts"], int(settings["port"]))


if __name__ == "__main__":
    main()
