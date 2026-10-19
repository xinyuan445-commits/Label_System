from io import BytesIO
from types import SimpleNamespace
from zipfile import ZipFile

import pytest

from app.db.session import get_session
from app.services.users import create_user
from tests.conftest import CsrfHelper


def test_download_requires_login(client):
    assert client.get("/api/print-assistant/windows").status_code == 401


@pytest.mark.parametrize("bridge_mode", [False, True])
def test_download_packages_built_binary_and_development_gate(
    app, client, tmp_path, monkeypatch, bridge_mode
):
    with app.app_context():
        create_user(get_session(), email="download-user", plain_password="test-password")
    csrf = CsrfHelper(client)
    assert (
        client.post(
            "/api/auth/login",
            json={"email": "download-user", "password": "test-password"},
            headers=csrf.headers(),
        ).status_code
        == 200
    )
    binary = tmp_path / "assistant.exe"
    app.config["PRINT_ASSISTANT_BINARY"] = str(binary)
    assert client.get("/api/print-assistant/windows").status_code == 503
    binary.write_bytes(b"MZ-test-build")
    # Only select the package mode; this test does not connect to an authority.
    from app.routes import print_assistant

    monkeypatch.setattr(
        print_assistant, "labelsystem", SimpleNamespace(enabled=lambda: bridge_mode)
    )
    monkeypatch.setattr(
        print_assistant, "invoke", lambda *args: {"serverPublicKey": "test-public-key"}
    )
    response = client.get("/api/print-assistant/windows")
    assert response.status_code == 200
    assert response.mimetype == "application/zip"
    with ZipFile(BytesIO(response.data)) as archive:
        assert set(archive.namelist()) == {
            "blg-connector.exe",
            "config.yaml",
            "install.ps1",
            "install.bat",
        }
        assert archive.read("blg-connector.exe") == b"MZ-test-build"
        config = archive.read("config.yaml").decode()
        assert ("disable_printing: true" in config) is bridge_mode
        assert "client_only: true" in config
        assert "server_url:" in config
        assert "-WindowStyle Hidden" in archive.read("install.ps1").decode("utf-8-sig")
