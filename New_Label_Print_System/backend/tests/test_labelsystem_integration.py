"""017 integration boundaries: no live SQL Server, stock writes or printers."""

import json
from email.message import Message
from io import BytesIO
from urllib.error import HTTPError, URLError

import pytest

from app.db.session import get_session
from app.models.label_format import FormatKind, LabelFormat
from app.services import labelsystem_templates, templates
from app.services.labelsystem_transport import IntegrationError
from tests.test_labelsystem_auth import bridge as authority_bridge

bridge = authority_bridge


def test_catalog_is_forwarded_without_exposing_upstream_token(bridge, monkeypatch):
    client, _, _, _ = bridge
    from app.routes import labelsystem_print as route

    monkeypatch.setattr(route, "invoke", lambda method, path, body: {"rules": [{"id": 1}]})
    result = client.get("/api/labelsystem/rules/catalog")
    assert result.status_code == 200
    assert result.json == {"rules": [{"id": 1}]}
    assert "upstream-private-token" not in result.text


def test_effective_deny_precedes_rule_management(bridge, monkeypatch):
    client, csrf, state, _ = bridge
    from app.routes import labelsystem_print as route

    calls = []
    monkeypatch.setattr(route, "invoke", lambda *args: calls.append(args))
    state["codes"] = {"print.view"}
    assert client.post("/api/labelsystem/rules", json={}, headers=csrf.headers()).status_code == 403
    assert (
        client.put("/api/labelsystem/print/code-rule", json={}, headers=csrf.headers()).status_code
        == 403
    )
    assert calls == []


def test_first_and_receipt_forward_original_identities(bridge, monkeypatch):
    client, csrf, state, _ = bridge
    state["codes"] |= {"print.first", "print.confirm"}
    from app.routes import labelsystem_print as route

    calls = []

    def upstream(method, path, body):
        calls.append((method, path, body))
        return json.dumps(
            {"success": True, "data": {"jobId": "same-job"}}
        ).encode(), "application/json"

    monkeypatch.setattr(route, "exchange", upstream)
    body = {"clientRequestId": "original-request", "targets": [{"labelId": 5}]}
    for _ in range(2):
        assert (
            client.post(
                "/api/labelsystem/print/jobs/first", json=body, headers=csrf.headers()
            ).status_code
            == 200
        )
    assert calls[0][2] == calls[1][2] == body
    signed = {"payload": "original-payload", "signature": "original-signature"}
    job = "11111111-1111-4111-8111-111111111111"
    assert (
        client.post(
            f"/api/labelsystem/print/jobs/{job}/receipts", json=signed, headers=csrf.headers()
        ).status_code
        == 200
    )
    assert calls[-1][2] == signed


@pytest.mark.parametrize(
    "path",
    ["unknown", "devices/anything", "jobs/not-a-uuid", "jobs?target=http://evil", "../admin/users"],
)
def test_proxy_has_no_arbitrary_forwarding(bridge, path):
    client, _, _, _ = bridge
    assert client.get("/api/labelsystem/print/" + path).status_code == 404


def test_template_state_routes_keep_permissions_and_row_version(bridge, monkeypatch):
    client, csrf, state, _ = bridge
    from app.routes import labelsystem_print as route

    calls = []

    def upstream(method, path, body):
        calls.append((method, path, body))
        return json.dumps({"success": True, "data": [] if method == "GET" else body}).encode(), (
            "application/json"
        )

    monkeypatch.setattr(route, "exchange", upstream)
    assert client.get("/api/labelsystem/print/templates/archived").status_code == 200
    body = {"expectedRowVersion": "A1", "enabled": False}
    state["codes"] = {"print.view"}
    assert (
        client.put(
            "/api/labelsystem/print/templates/9/state", json=body, headers=csrf.headers()
        ).status_code
        == 403
    )
    state["codes"].add("print.template.manage")
    response = client.put(
        "/api/labelsystem/print/templates/9/state", json=body, headers=csrf.headers()
    )
    assert response.status_code == 200
    assert calls == [
        ("GET", "/api/v1/print/templates/archived", None),
        ("PUT", "/api/v1/print/templates/9/state", body),
    ]


def test_recovery_only_calls_original_sync_endpoint(bridge, monkeypatch):
    client, csrf, state, _ = bridge
    state["codes"].add("print.confirm")
    from app.routes import labelsystem_print as route

    calls = []

    def upstream(method, path, body):
        calls.append(path)
        raise IntegrationError("PRINT_WRITES_BLOCKED", "当前环境未允许追溯写入。")

    monkeypatch.setattr(route, "exchange", upstream)
    job = "11111111-1111-4111-8111-111111111111"
    response = client.post(
        f"/api/labelsystem/print/jobs/{job}/retry-sync", json={}, headers=csrf.headers()
    )
    assert response.status_code == 409
    assert response.json["error"] == "PRINT_WRITES_BLOCKED"
    assert calls == [f"/api/v1/print/jobs/{job}/retry-sync"]


def test_template_publication_normalizes_aliases_and_reuses_immutable_version(
    bridge, app, monkeypatch
):
    client, _, _, _ = bridge
    user_id = client.get("/api/me").json["id"]
    with app.app_context():
        db = get_session()
        fmt = LabelFormat(name="test", width_mm=76.2, height_mm=50.8, kind=FormatKind.CUSTOM)
        db.add(fmt)
        db.commit()
        draft = templates.create(
            db,
            owner_id=user_id,
            name="draft",
            description=None,
            format_id=fmt.id,
            canvas_data={
                "version": 1,
                "stage": {"width_mm": 76.2, "height_mm": 50.8},
                "objects": [
                    {"id": "barcode", "type": "barcode", "data": "{{条码}}", "barcodeType": "qr"},
                    {"id": "text", "type": "text", "text": "{{存货编码}}"},
                ],
            },
        )
        original = json.dumps(draft.canvas_data, sort_keys=True)
        state = {"template": None, "versions": 0, "fail_publish": False}

        def upstream(method, path, body=None):
            if method == "GET" and path.endswith("/templates/archived"):
                return (
                    [{"id": 9}] if state["template"] and not state["template"]["is_enabled"] else []
                )
            if method == "GET" and path.endswith("/templates"):
                return [{"id": 9}] if state["template"] and state["template"]["is_enabled"] else []
            if method == "GET":
                return state["template"]
            if path.endswith("/state"):
                state["template"]["is_enabled"] = body["enabled"]
                return {"id": 9, "enabled": body["enabled"], "rowVersion": "DEF"}
            if path.endswith("/publish"):
                if state["fail_publish"]:
                    state["fail_publish"] = False
                    raise IntegrationError("simulated_timeout", "test timeout", 503)
                state["versions"] += 1
                version_id = 16 + state["versions"]
                state["template"]["published_version_id"] = version_id
                return {"id": version_id}
            assert "jobs" not in path and "label-code" not in path
            old_version = (
                state["template"].get("published_version_id") if state["template"] else None
            )
            state["template"] = {
                "id": 9,
                "row_version": "ABC",
                "draft_canvas_json": json.dumps(body["canvasData"]),
                "published_version_id": old_version,
                "is_enabled": True,
            }
            return state["template"]

        monkeypatch.setattr(labelsystem_templates, "invoke", upstream)
        first = labelsystem_templates.publish(draft.id, user_id)
        second = labelsystem_templates.publish(draft.id, user_id)
        assert first["versionId"] == second["versionId"] == 17
        assert second["reused"] and state["versions"] == 1
        formal = json.loads(state["template"]["draft_canvas_json"])
        assert formal["objects"][1]["text"] == "{{物料编码}}"
        assert json.dumps(draft.canvas_data, sort_keys=True) == original
        draft.name = "changed draft"
        db.commit()
        state["fail_publish"] = True
        with pytest.raises(IntegrationError):
            labelsystem_templates.publish(draft.id, user_id)
        # The saved new draft still points to old version17 until its publish succeeds.
        assert state["template"]["published_version_id"] == 17
        recovered = labelsystem_templates.publish(draft.id, user_id)
        assert recovered["versionId"] == 18 and not recovered["reused"]
        assert labelsystem_templates.publish(draft.id, user_id)["reused"]
        assert state["versions"] == 2
        state["template"]["is_enabled"] = False
        assert labelsystem_templates.publish(draft.id, user_id)["reused"]
        assert state["template"]["is_enabled"]
        assert state["versions"] == 2


def test_unsupported_formal_variable_is_not_silently_blank():
    with pytest.raises(IntegrationError, match="print_variable_unsupported"):
        labelsystem_templates.normalize({"text": "{{不存在的字段}}"})


def test_real_envelope_errors_keep_business_message_and_server_only_token(bridge, monkeypatch):
    client, _, _, _ = bridge
    from app.services import labelsystem_transport as transport

    class FakeOpener:
        def open(self, request, timeout):
            assert request.get_header("Authorization") == "Bearer upstream-private-token"
            assert request.get_header("Cookie") is None
            data = json.dumps(
                {
                    "success": False,
                    "code": "PRINT_VERSION_CHANGED",
                    "message": "数据已变化，请刷新。",
                    "data": None,
                }
            ).encode()
            raise HTTPError(request.full_url, 409, "Conflict", None, BytesIO(data))

    monkeypatch.setattr(transport, "build_opener", lambda *args: FakeOpener())
    result = client.get("/api/labelsystem/print/code-rule")
    assert result.status_code == 409
    assert result.json == {"error": "PRINT_VERSION_CHANGED", "detail": "数据已变化，请刷新。"}
    assert "upstream-private-token" not in result.text
    assert result.headers["Cache-Control"] == "private, no-store"


@pytest.mark.parametrize(
    "kind,permission", [("first", "print.first"), ("reprint", "print.reprint")]
)
def test_transport_timeout_retry_keeps_original_creation_identity(
    bridge, monkeypatch, kind, permission
):
    client, csrf, state, _ = bridge
    state["codes"].add(permission)
    from app.services import labelsystem_transport as transport

    requests = []

    class Response(BytesIO):
        headers = Message()
        headers["Content-Type"] = "application/json"

    class Opener:
        def open(self, request, timeout):
            requests.append((request.full_url, json.loads(request.data)))
            assert request.get_header("Authorization") == "Bearer upstream-private-token"
            assert request.get_header("Cookie") is None
            if len(requests) == 1:
                raise URLError("response lost after upstream commit")
            return Response(json.dumps({"success": True, "data": {"jobId": "original"}}).encode())

    monkeypatch.setattr(transport, "build_opener", lambda *args: Opener())
    body = {"clientRequestId": "saved-request", "labelId": 42}
    path = f"/api/labelsystem/print/jobs/{kind}"
    assert client.post(path, json=body, headers=csrf.headers()).status_code == 503
    result = client.post(path, json=body, headers=csrf.headers())
    assert result.status_code == 200 and result.json == {"jobId": "original"}
    assert requests == [(f"http://127.0.0.1:9891/api/v1/print/jobs/{kind}", body)] * 2


@pytest.mark.parametrize(
    "suffix,permission",
    [
        ("first", "print.first"),
        ("reprint", "print.reprint"),
        ("receipts", "print.confirm"),
        ("retry-sync", "print.confirm"),
        ("dispatch-authorizations", "print.confirm"),
    ],
)
def test_each_write_route_rechecks_revoked_permission_before_transport(
    bridge, monkeypatch, suffix, permission
):
    client, csrf, state, _ = bridge
    from app.routes import labelsystem_print as route

    calls = []
    monkeypatch.setattr(route, "exchange", lambda *args: calls.append(args))
    state["codes"].discard(permission)
    job = "11111111-1111-4111-8111-111111111111"
    tail = suffix if suffix in {"first", "reprint"} else f"{job}/{suffix}"
    result = client.post(f"/api/labelsystem/print/jobs/{tail}", json={}, headers=csrf.headers())
    assert result.status_code == 403
    assert calls == []
