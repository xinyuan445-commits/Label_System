"""888-only mock helper. The product Go protocol runs with its driver replaced before input."""

from __future__ import annotations

import atexit
import base64
import hashlib
import json
import os
import re
import subprocess
import threading
from pathlib import Path
from typing import Any

from flask import current_app

from app.services.labelsystem_transport import IntegrationError, invoke

MODES = {"normal", "drop-helper", "drop-receipt", "fail-sync"}
PRINTER = "888模拟打印机"


class PrintSimulation:
    def __init__(self, settings: dict[str, Any]) -> None:
        self.root = Path(settings["statePath"])
        self.binary = Path(settings["binaryPath"])
        self.key = Path(settings["privateKeyPath"])
        self.identity = json.loads(Path(settings["identityPath"]).read_text(encoding="utf-8-sig"))
        self.identity["name"] = "888模拟打印助手"
        if not self.binary.is_file() or not self.key.is_file() or not self.root.is_absolute():
            raise RuntimeError("Simulation helper installation is incomplete")
        self.root.mkdir(parents=True, exist_ok=True)
        self.lock = threading.RLock()
        self.process = None
        self.log = None
        atexit.register(self.close)

    def close(self) -> None:
        with self.lock:
            if self.process is not None and self.process.poll() is None:
                self.process.stdin.close()
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=5)
            if self.log is not None:
                self.log.close()
            self.process = self.log = None

    def capabilities(self) -> dict[str, Any]:
        cap = invoke("GET", "/api/v1/print/capabilities")
        if not cap.get("simulationOnly") or cap.get("simulationDeviceId") != self.identity["id"]:
            raise IntegrationError("simulation_configuration_invalid", "888模拟环境未就绪。", 503)
        return cap

    def rpc(self, body: dict[str, Any]) -> dict[str, Any]:
        with self.lock:
            cap = self.capabilities()
            if self.process is None or self.process.poll() is not None:
                self.close()
                env = os.environ.copy()
                env.update(
                    PRINT_ACCEPTANCE_AGENT_ROOT=str(self.root),
                    PRINT_ACCEPTANCE_DEVICE=self.identity["id"],
                    PRINT_ACCEPTANCE_DEVICE_KEY=str(self.key),
                    PRINT_ACCEPTANCE_SERVER_PUBLIC=cap["serverPublicKey"],
                )
                self.log = (self.root / "mock-helper.stderr.log").open("a", encoding="utf-8")
                self.process = subprocess.Popen(
                    [str(self.binary), "-test.run", "^TestDevelopmentSqlAcceptanceBridge$"],
                    env=env,
                    stdin=subprocess.PIPE,
                    stdout=subprocess.PIPE,
                    stderr=self.log,
                    text=True,
                    encoding="utf-8",
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
                if self.process.stdout.readline().strip() != "READY":
                    raise IntegrationError(
                        "simulation_helper_unavailable", "模拟助手启动失败。", 503
                    )
            self.process.stdin.write(json.dumps(body, ensure_ascii=False) + "\n")
            self.process.stdin.flush()
            raw = self.process.stdout.readline()
            if not raw:
                raise IntegrationError(
                    "simulation_helper_unavailable", "模拟助手暂不可用，请查询原任务。", 503
                )
            return json.loads(raw)

    def _user_path(self, user: str) -> Path:
        return self.root / ("mode-" + hashlib.sha256(user.encode()).hexdigest() + ".json")

    @staticmethod
    def write(path: Path, value: object) -> None:
        temporary = path.with_suffix(path.suffix + ".tmp")
        temporary.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
        os.replace(temporary, path)

    def mode(self, user: str, value: str | None = None) -> str:
        with self.lock:
            path = self._user_path(user)
            if value is not None:
                if value not in MODES:
                    raise IntegrationError("simulation_mode_invalid", "请选择有效测试方式。", 400)
                self.write(path, value)
            return json.loads(path.read_text(encoding="utf-8")) if path.exists() else "normal"

    def status(self, user: str) -> dict[str, Any]:
        cap = self.capabilities()
        return dict(
            deviceId=self.identity["id"],
            deviceKeyHash=cap["simulationDeviceKeyHash"],
            serverPublicKey=cap["serverPublicKey"],
            capabilities=["driver-labels-v2"],
            print_dispatch_enabled=True,
            simulationOnly=True,
            mode=self.mode(user),
            simulatedSends=self.rpc({"method": "COUNT"})["count"],
        )

    def request(
        self, method: str, path: str, body: dict[str, Any] | None, user: str
    ) -> tuple[dict[str, Any], int]:
        if method == "GET" and path == "status":
            return self.status(user), 200
        if method == "GET" and path == "printers":
            return {"printers": [{"name": PRINTER}]}, 200
        if method == "GET" and path == "v2/device":
            return self.identity, 200
        if not (
            method == "POST"
            and path == "v2/print-labels"
            or method == "GET"
            and re.fullmatch(r"v2/label-jobs/[0-9a-f-]{36}/[0-9a-f-]{36}\?scope=.{1,2048}", path)
        ):
            raise IntegrationError("simulation_route_invalid", "模拟接口无效。", 404)
        with self.lock:
            result = self.rpc({"method": method, "path": "/" + path, "body": body, "mode": "sent"})
            if method == "POST" and result["status"] == 200:
                # Parse only after the real Go V2 verifier has accepted the signed authorization.
                ticket = json.loads(
                    base64.b64decode(body["authorization"]["payload"], validate=True)
                )
                job = ticket["jobId"]
                if not re.fullmatch(r"[0-9a-f-]{36}", job):
                    raise IntegrationError("simulation_ticket_invalid", "模拟授权无效。", 409)
                mode = self.mode(user)
                self.mode(user, "normal")
                self.write(self.root / (job + ".receipt-mode.json"), {"user": user, "mode": mode})
                if mode == "drop-helper":
                    raise IntegrationError(
                        "simulation_response_lost",
                        "模拟发送回执已丢失，请查询原任务并恢复登记。",
                        503,
                    )
            return json.loads(result["body"]), result["status"]

    def before_receipt(self, job: str, user: str) -> str:
        with self.lock:
            path = self.root / (job + ".receipt-mode.json")
            if not path.exists():
                return "normal"
            state = json.loads(path.read_text(encoding="utf-8"))
            if state["user"] != user:
                return "normal"
            if state["mode"] == "fail-sync":
                fault = self.root / (job + ".fail-sync-once")
                if not fault.exists() and not Path(str(fault) + ".consumed").exists():
                    fault.write_text("one simulated sync failure", encoding="ascii")
            return state["mode"]

    def after_receipt(self, job: str, user: str, mode: str) -> None:
        with self.lock:
            path = self.root / (job + ".receipt-mode.json")
            if path.exists():
                self.write(path, {"user": user, "mode": "normal"})
            if mode == "drop-receipt":
                raise IntegrationError(
                    "simulation_response_lost", "模拟登记响应已丢失，请查询原任务。", 503
                )


def simulation() -> PrintSimulation | None:
    return current_app.extensions.get("print_simulation")
