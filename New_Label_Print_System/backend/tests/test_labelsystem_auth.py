"""Standalone authority boundary; no SQL Server or printer is used."""

import pytest

from app.auth import labelsystem
from app.auth.password import verify_password
from app.models.user import User
from app.services import u8_bills
from tests.conftest import CsrfHelper


@pytest.fixture
def bridge(app, monkeypatch):
    monkeypatch.setenv("LABELSYSTEM_API_URL", "http://127.0.0.1:9891")
    labelsystem.init(app)
    state = {"codes": {"print.view", "print.template.manage"}, "active": True}
    calls = []

    def upstream(path, token="", body=None):
        calls.append((path, token))
        if not state["active"]:
            raise labelsystem.AuthorityError("invalid_credentials", 401)
        profile = {"id": "remote-123", "name": "Test Admin", "roles": ["admin"]}
        if path.endswith("/login"):
            if body["password"] != "remote-password":
                raise labelsystem.AuthorityError("invalid_credentials", 401)
            return {"token": "upstream-private-token", "user": profile}
        if path.endswith("/functions"):
            return [{"code": code} for code in state["codes"]]
        if path.endswith("/me"):
            return profile
        return {}

    monkeypatch.setattr(labelsystem, "call", upstream)
    client = app.test_client()
    csrf = CsrfHelper(client)
    response = client.post(
        "/api/auth/login",
        json={"email": "operator", "password": "remote-password"},
        headers=csrf.headers(),
    )
    assert response.status_code == 200
    assert response.json["user"]["print_dispatch_enabled"] is False
    assert response.json["user"]["authentication_source"] == "labelsystem"
    return client, csrf, state, calls


def test_account_is_reference_and_token_stays_on_server(bridge, app):
    client, _, _, calls = bridge
    response = client.get("/api/me")
    assert response.status_code == 200
    profile = response.json
    assert profile["email"] == "operator"
    assert profile["authentication_source"] == "labelsystem"
    assert profile["print_dispatch_enabled"] is False
    assert profile["visibility"]["users"] is False
    assert profile["permissions"]["printers"] is False
    assert any(path.endswith("/me") for path, _ in calls)
    cookie = client.get_cookie("NewLabelPrint.Session").value
    decoded = app.session_interface.get_signing_serializer(app).loads(cookie)
    assert "labelsystem_handle" in decoded
    assert "upstream-private-token" not in str(decoded)
    with app.app_context():
        from app.db.session import get_session

        user = get_session().get(User, profile["id"])
        assert not verify_password(user.password_hash, "remote-password")


def test_effective_permission_deny_applies_to_admin_immediately(bridge):
    client, _, state, _ = bridge
    state["codes"] = {"print.view"}
    assert client.get("/api/me").json["permissions"]["templates"] is False
    assert client.get("/api/templates").status_code == 200
    state["codes"] = set()
    assert client.get("/api/me").status_code == 401


def test_revocation_fails_closed(bridge):
    client, _, state, _ = bridge
    state["active"] = False
    assert client.get("/api/me").status_code == 401


@pytest.mark.parametrize(
    "path",
    [
        "/api/u8/print",
        "/api/u8/stock/print",
        "/api/u8/reprint",
        "/api/u8/print/example/complete",
        "/api/u8/print/example/register",
        "/api/print-jobs",
    ],
)
def test_legacy_dispatch_and_receipts_never_run(bridge, path):
    client, csrf, _, _ = bridge
    result = client.post(path, json={}, headers=csrf.headers())
    assert result.status_code == 409
    assert result.json["error"] == "print_dispatch_disabled"


def test_account_changes_are_centralized(bridge):
    client, csrf, _, _ = bridge
    assert client.get("/api/admin/users").status_code == 409
    assert (
        client.post("/api/auth/change-password", json={}, headers=csrf.headers()).status_code == 409
    )


def test_888_read_only_and_old_write_whitelist_stays_998(bridge, monkeypatch):
    monkeypatch.setenv("U8_DATABASE", "ufdata_888_2021")
    assert u8_bills.query_only()
    u8_bills.validate_database("Server=test;Database=ufdata_888_2021")
    with pytest.raises(u8_bills.U8DatabaseMismatchError):
        u8_bills.validate_database("Server=test;Database=ufdata_888_2021", for_write=True)


def test_bad_password_does_not_fall_back_to_local(bridge):
    client, csrf, _, _ = bridge
    result = client.post(
        "/api/auth/login",
        json={"email": "operator", "password": "wrong-password"},
        headers=csrf.headers(),
    )
    assert result.status_code == 401


def test_device_token_routes_are_blocked_without_a_browser_session(bridge, app):
    client = app.test_client()
    assert client.get("/api/agent/jobs").status_code == 409
