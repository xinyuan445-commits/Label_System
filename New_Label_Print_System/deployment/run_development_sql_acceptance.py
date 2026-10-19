"""Explicit real SQL acceptance orchestrator; business SQL is restricted by the .NET runner.

No production configuration is loaded. Original Flask routes and frontend functions are used.
The helper is a product Go test executable with both driver and discovery forcibly replaced.
"""
from __future__ import annotations

import json
import logging
import os
import secrets
import subprocess
import sys
import threading
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

REPO = Path(os.environ["PRINT_DEV_REPO"]).resolve()
RUN = os.environ["PRINT_DEV_RUN"]
WORK = REPO / ".tools" / "print-development-acceptance" / RUN
WORK.mkdir(parents=True, exist_ok=True)
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


class Rpc:
    def __init__(self, args, env, name):
        self.lock = threading.Lock()
        self.stderr = (WORK / (name + "-stderr.log")).open("w", encoding="utf-8")
        self.process = subprocess.Popen(
            args, cwd=REPO, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=self.stderr, text=True, encoding="utf-8", creationflags=NO_WINDOW,
        )
        if self.process.stdout.readline().strip() != "READY":
            raise RuntimeError(name + " did not initialize; inspect private diagnostics")

    def call(self, body):
        with self.lock:
            self.process.stdin.write(json.dumps(body, ensure_ascii=False) + "\n")
            self.process.stdin.flush()
            line = self.process.stdout.readline()
            if not line:
                raise RuntimeError("Acceptance child exited")
            value = json.loads(line)
            if value.get("ok") is False:
                raise RuntimeError(json.dumps(value, ensure_ascii=False))
            return value.get("data", value)

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        self.stderr.close()


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    bridge = helper = web = control = memory_anchor = engine = None
    assertions = []
    final = None
    passed = False
    try:
        bridge = Rpc(["dotnet", str(REPO / "label-api/tools/PrintDevelopmentAcceptance/bin/Debug/net8.0/PrintDevelopmentAcceptance.dll")], os.environ.copy(), "sql")
        state = bridge.call({"method": "state"})
        child_env = os.environ.copy()
        child_env.pop("PRINT_DEV_LABEL", None)
        child_env.pop("PRINT_DEV_U8", None)
        child_env.update({
            "PRINT_ACCEPTANCE_AGENT_ROOT": str(WORK / "agent"),
            "PRINT_ACCEPTANCE_DEVICE": state["deviceId"],
            "PRINT_ACCEPTANCE_SERVER_PUBLIC": state["serverPublicKey"],
        })

        def start_helper():
            return Rpc([str(WORK / "product-agent-test.exe"), "-test.run", "^TestDevelopmentSqlAcceptanceBridge$"], child_env, "mock-helper")

        helper = start_helper()
        # Original app, temporary in-memory technical identities only. No extra SQL Server DB.
        sys.path.insert(0, str(REPO / "New_Label_Print_System/backend"))
        os.environ["LABELSYSTEM_API_URL"] = state["apiUrl"]
        for folder in ("UPLOADS", "ASSETS", "PDFS"):
            os.environ[folder + "_DIR"] = str(WORK / folder.lower())
        from sqlalchemy import create_engine, event
        from sqlalchemy.orm import sessionmaker
        from sqlalchemy.pool import QueuePool
        from app import models  # noqa: F401
        from app.config import Config
        from app.db.base import Base
        from app.models.template import Template
        import app.db.session as sessions
        from app.factory import create_app
        from local_server import seed_label_formats
        from werkzeug.serving import WSGIRequestHandler, make_server

        app = create_app(Config("sqlite://", "redis://invalid:1/0", "development", secrets.token_hex(32), False, True), init_db=False, use_redis_sessions=False)
        engine = create_engine(
            f"sqlite:///file:printaccept_{RUN}?mode=memory&cache=shared&uri=true",
            poolclass=QueuePool, pool_size=12, max_overflow=12,
            connect_args={"check_same_thread": False},
        )
        memory_anchor = engine.connect()
        sessions._engine = engine
        sessions._SessionFactory = sessionmaker(bind=engine, expire_on_commit=False)
        Base.metadata.create_all(engine)
        @event.listens_for(Template, "before_insert")
        def unique_test_source_id(_mapper, _connection, target):
            # Independent in-memory runs must never impersonate a persistent designer draft ID.
            target.id = int(RUN, 16)
        app.teardown_appcontext(lambda _: sessions.close_session())
        with app.app_context():
            seed_label_formats(sessions.get_session())
        logging.getLogger("werkzeug").setLevel(logging.ERROR)

        class Quiet(WSGIRequestHandler):
            def log_request(self, *args, **kwargs):
                pass

        web = make_server("127.0.0.1", 0, app, threaded=True, request_handler=Quiet)
        threading.Thread(target=web.serve_forever, daemon=True).start()
        control_key = secrets.token_urlsafe(32)

        class Control(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_POST(self):
                nonlocal helper
                if not secrets.compare_digest(self.headers.get("X-Acceptance-Key", ""), control_key):
                    self.send_error(403)
                    return
                try:
                    body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
                    status = 200
                    if self.path == "/helper/status":
                        value = {**state, "capabilities": ["driver-labels-v2"], "print_dispatch_enabled": True}
                    elif self.path == "/helper/request":
                        result = helper.call(body)
                        status, value = result["status"], json.loads(result["body"])
                    elif self.path == "/helper/count":
                        value = helper.call({"method": "COUNT"})
                    elif self.path == "/helper/restart":
                        helper.close()
                        helper = start_helper()
                        value = {"restartedMockOnly": True}
                    elif self.path == "/record":
                        assertions.append(body)
                        (WORK / "assertions.json").write_text(json.dumps(assertions, ensure_ascii=False, indent=2), encoding="utf-8")
                        value = {"recorded": True}
                    elif self.path == "/sql":
                        if body.get("method") not in {"seed", "audit", "fault", "consume", "drop-next-initial-response"}:
                            raise ValueError("Control action rejected")
                        value = bridge.call(body)
                    else:
                        self.send_error(404)
                        return
                except Exception as exc:
                    status, value = 500, {"error": str(exc)}
                raw = json.dumps(value, ensure_ascii=False).encode()
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(raw)))
                self.end_headers()
                self.wfile.write(raw)

        control = ThreadingHTTPServer(("127.0.0.1", 0), Control)
        threading.Thread(target=control.serve_forever, daemon=True).start()
        node_env = child_env.copy()
        node_env.update({
            "LABEL_SQL_ACCEPTANCE_WEB": f"http://127.0.0.1:{web.server_port}",
            "LABEL_SQL_ACCEPTANCE_CONTROL": f"http://127.0.0.1:{control.server_port}",
            "LABEL_SQL_ACCEPTANCE_KEY": control_key,
            "LABEL_SQL_ACCEPTANCE_STATE": json.dumps(state),
        })
        result = subprocess.run(
            ["cmd.exe", "/d", "/c", "npm", "test", "--", "src/lib/labelSystem.sql-acceptance.test.ts", "--reporter=verbose"],
            cwd=REPO / "New_Label_Print_System/frontend", env=node_env,
            creationflags=NO_WINDOW, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=480,
        )
        # Test output has no environment or credentials. Keep it separate from configuration.
        (WORK / "frontend-test.log").write_text(result.stdout + result.stderr, encoding="utf-8")
        (WORK / "frontend-exit-code.json").write_text(json.dumps({"exitCode": result.returncode}), encoding="utf-8")
        print(result.stdout, flush=True)
        if result.returncode:
            raise RuntimeError("Frontend/Flask/real SQL acceptance failed; see run evidence")
        final = bridge.call({"method": "finalize"})
        assert all(final["originalRowsUnchanged"].values()), "Existing rows changed"
        assert final["ruleBindingRestored"] and final["faultTriggerRemoved"]
        assert final["remainingOwnedLocks"] == 0
        passed = True
    finally:
        if bridge and final is None:
            try:
                final = bridge.call({"method": "finalize"})
            except Exception as error:
                final = {"cleanupError": str(error)}
        for server in (control, web):
            if server:
                server.shutdown()
                server.server_close()
        for child in (helper, bridge):
            if child:
                child.close()
        if memory_anchor is not None:
            memory_anchor.close()
        if engine is not None:
            engine.dispose()
        record = {
            "at": datetime.now(timezone(timedelta(hours=8))).isoformat(), "runId": RUN,
            "passed": passed, "labelDatabase": "LabelSystem_Dev", "tracebackDatabase": "UFDATA_888_2021",
            "account": "888", "realSql": True, "newDatabaseCreated": False, "migrationsExecuted": False,
            "originalFlaskRoutes": True, "originalFrontendFunctions": True, "productGoV2MockDriver": True,
            "realPrinterStarted": False, "production008Accessed": False, "publicServiceGatesChanged": False,
            "assertions": assertions, "final": final,
        }
        (WORK / "result.json").write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        if passed:
            destination = REPO / "docs/test-builds/2026-10-19-print-development-sql-evidence.json"
            destination.write_text(json.dumps(record, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print("PASS" if passed else "FAIL", "existing-development SQL acceptance; run", RUN, flush=True)


if __name__ == "__main__":
    main()
