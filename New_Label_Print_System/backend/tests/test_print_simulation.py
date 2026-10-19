from types import SimpleNamespace

from tests.test_labelsystem_auth import bridge as authority_bridge

bridge = authority_bridge


def test_simulation_routes_are_unavailable_unless_installed(bridge):
    client, _, _, _ = bridge
    assert client.get("/api/labelsystem/simulation/helper/status").status_code == 404


def test_removed_simulation_routes_stay_unavailable_even_with_old_extension(bridge):
    client, csrf, state, _ = bridge
    state["codes"].add("print.confirm")
    client.application.extensions["print_simulation"] = SimpleNamespace(
        request=lambda *args: (_ for _ in ()).throw(AssertionError("stale helper called"))
    )
    assert client.get("/api/labelsystem/simulation/helper/status").status_code == 404
    assert (
        client.post("/api/labelsystem/simulation/mode", json={}, headers=csrf.headers()).status_code
        == 404
    )


def test_real_receipts_ignore_old_simulation_extension(bridge, monkeypatch):
    client, csrf, state, _ = bridge
    state["codes"].add("print.confirm")
    calls = []
    from app.routes import labelsystem_print
    from app.services.labelsystem_transport import IntegrationError

    def after(*args):
        calls.append("lost-response")
        raise IntegrationError("simulation_response_lost", "模拟登记响应已丢失。", 503)

    client.application.extensions["print_simulation"] = SimpleNamespace(
        before_receipt=lambda *args: "drop-receipt", after_receipt=after
    )
    monkeypatch.setattr(
        labelsystem_print,
        "exchange",
        lambda *args: (
            calls.append("registered-original") or b'{"success":true,"data":{"phase":"COMPLETED"}}',
            "application/json",
        ),
    )
    result = client.post(
        "/api/labelsystem/print/jobs/11111111-1111-4111-8111-111111111111/receipts",
        json={"payload": "same"},
        headers=csrf.headers(),
    )
    assert result.status_code == 200
    assert calls == ["registered-original"]
