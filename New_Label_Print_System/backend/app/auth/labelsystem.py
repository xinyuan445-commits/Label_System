"""Optional standalone UI authentication against Label system.

Only opaque handles enter the signed browser session. Upstream tokens stay in
this process and are revalidated, with effective permissions, on every request.
Local users are ownership references for templates, not another password store.
"""

from __future__ import annotations

import hashlib
import json
import os
import secrets
import threading
import time
from contextlib import suppress
from dataclasses import dataclass
from typing import Any, cast
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen

from flask import Flask, g, jsonify, request, session
from flask.typing import ResponseReturnValue
from flask_login import current_user
from sqlalchemy import select

from app.auth.password import hash_password
from app.db.session import get_session
from app.models.user import Role, User


def enabled() -> bool:
    return bool(os.environ.get("LABELSYSTEM_API_URL", "").strip())


def api_base_url() -> str:
    """Return the fixed upstream origin after enforcing its transport boundary."""
    base = os.environ.get("LABELSYSTEM_API_URL", "").rstrip("/")
    uri = urlsplit(base)
    if uri.username or uri.password or not uri.hostname or uri.path or uri.query or uri.fragment:
        raise AuthorityError("labelsystem_configuration_invalid")
    if uri.scheme == "https":
        return base
    if uri.scheme == "http" and uri.hostname in {"127.0.0.1", "localhost"}:
        return base
    allowed = os.environ.get("LABELSYSTEM_API_HTTP_ORIGIN", "").rstrip("/")
    if uri.scheme == "http" and allowed and base == allowed:
        return base
    raise AuthorityError("labelsystem_https_required")


class AuthorityError(Exception):
    def __init__(self, code: str, status: int = 503) -> None:
        self.code, self.status = code, status
        super().__init__(code)


@dataclass
class AuthoritySession:
    token: str
    remote_id: str
    local_id: int
    login_name: str
    expires: float


def call(path: str, token: str = "", body: dict[str, Any] | None = None) -> Any:  # noqa: ANN401 -- JSON transport
    base = api_base_url()
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    data = None
    if body is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(body).encode("utf-8")
    try:
        # Schemes and host are restricted above; callers use fixed API paths.
        with urlopen(Request(base + path, data=data, headers=headers), timeout=12) as response:  # noqa: S310
            result = json.load(response)
    except HTTPError as exc:
        raise AuthorityError(
            "invalid_credentials" if exc.code == 401 else "labelsystem_access_denied", exc.code
        ) from None
    except (URLError, TimeoutError, ValueError, OSError):
        raise AuthorityError("labelsystem_unavailable") from None
    if not isinstance(result, dict) or not result.get("success") or "data" not in result:
        raise AuthorityError("labelsystem_unavailable")
    return result["data"]


def permissions(codes: set[str]) -> dict[str, bool]:
    view = "print.view" in codes
    manage = view and "print.template.manage" in codes
    return {
        "bills": view,
        "templates": manage,
        "library": manage,
        "history": view,
        "printers": view and "print.device.manage" in codes,
    }


class Authority:
    def __init__(self) -> None:
        self.sessions: dict[str, AuthoritySession] = {}
        self.lock = threading.Lock()

    @staticmethod
    def context(codes: set[str], login_name: str) -> None:
        if "print.view" not in codes:
            raise AuthorityError("print_access_denied", 403)
        g.labelsystem_permissions = permissions(codes)
        g.labelsystem_login_name = login_name
        g.labelsystem_codes = codes

    def login(self, login_name: str, password: str) -> User:
        result = call("/api/v1/auth/login", body={"username": login_name, "password": password})
        token, profile = result["token"], result["user"]
        codes = {item["code"] for item in call("/api/v1/functions", token)}
        self.context(codes, login_name)
        db = get_session()
        identity = (
            "label-" + hashlib.sha256(profile["id"].encode()).hexdigest() + "@external.invalid"
        )
        user = db.scalar(select(User).where(User.email == identity))
        role = Role.ADMIN if "admin" in profile["roles"] else Role.EDITOR
        if user is None:
            user = User(
                email=identity,
                password_hash=hash_password(secrets.token_urlsafe(48)),
                role=role,
                language="zh",
                must_change_password=False,
            )
            db.add(user)
        else:
            user.role, user.is_active, user.must_change_password = role, True, False
        db.commit()
        handle = secrets.token_urlsafe(32)
        with self.lock:
            now = time.time()
            self.sessions = {
                key: value for key, value in self.sessions.items() if value.expires > now
            }
            if len(self.sessions) >= 1024:
                raise AuthorityError("labelsystem_session_limit")
            self.sessions[handle] = AuthoritySession(
                token, profile["id"], user.id, login_name, now + 8 * 3600
            )
        session["labelsystem_handle"] = handle
        return user

    def load(self, local_id: int) -> User | None:
        with self.lock:
            entry = self.sessions.get(session.get("labelsystem_handle", ""))
        if entry is None or entry.local_id != local_id or entry.expires <= time.time():
            return None
        try:
            profile = call("/api/v1/auth/me", entry.token)
            codes = {item["code"] for item in call("/api/v1/functions", entry.token)}
            if profile["id"] != entry.remote_id:
                return None
            self.context(codes, entry.login_name)
        except AuthorityError:
            return None  # Fail closed; never fall back to the local password hash.
        user = get_session().get(User, local_id)
        if user is None:
            return None
        user.role = Role.ADMIN if "admin" in profile["roles"] else Role.EDITOR
        g.labelsystem_codes = codes
        return user

    def logout(self) -> None:
        with self.lock:
            entry = self.sessions.pop(session.pop("labelsystem_handle", ""), None)
        if entry:
            with suppress(AuthorityError):
                call("/api/v1/auth/logout", entry.token, {})

    def token(self) -> str:
        """Server-only credential for the current, revalidated user."""
        with self.lock:
            entry = self.sessions.get(session.get("labelsystem_handle", ""))
        if entry is None or entry.expires <= time.time():
            raise AuthorityError("unauthorized", 401)
        return entry.token


def authority() -> Authority:
    from flask import current_app

    return cast(Authority, current_app.extensions["labelsystem_authority"])


def init(app: Flask) -> None:
    if not enabled():
        return
    app.extensions["labelsystem_authority"] = Authority()
    app.config["SESSION_COOKIE_NAME"] = "NewLabelPrint.Session"

    @app.before_request
    def control() -> ResponseReturnValue | None:
        # Revalidate once per request even if a caller retains an app context.
        g.pop("_login_user", None)
        g.pop("labelsystem_codes", None)
        g.pop("labelsystem_permissions", None)
        g.pop("labelsystem_login_name", None)
        path = request.path
        # Agent routes use device tokens, so this gate must also precede login.
        if request.blueprint in {"agent", "print_jobs", "captures"} or (
            request.blueprint == "u8"
            and request.method not in {"GET", "HEAD", "OPTIONS"}
            and ("/print" in path or "/reprint" in path)
        ):
            return jsonify({"error": "print_dispatch_disabled"}), 409
        if not current_user.is_authenticated:
            return None
        if path == "/api/auth/change-password" or request.blueprint == "admin":
            return jsonify({"error": "accounts_managed_by_labelsystem"}), 409
        if (
            request.method not in {"GET", "HEAD", "OPTIONS"}
            and request.blueprint in {"templates", "assets", "folders", "library"}
            and "print.template.manage" not in g.get("labelsystem_codes", set())
        ):
            return jsonify({"error": "feature_disabled"}), 403
        return None
