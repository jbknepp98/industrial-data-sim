"""Bounded, selected evidence for synthetic diagnostic runs, including failures."""
from contextlib import contextmanager
import json
from pathlib import Path
import re
import subprocess
import sys
import uuid


def synthetic_log_directory(folder):
    # Matches CLI logging for this helper's fixed database filename, state.db.
    return Path(folder) / "logs" / "state.db"


class DiagnosticEvidence:
    def __init__(self, folder=None):
        self.folder = folder
        self.announced = False
        self.report = {"schemaVersion": 1, "status": "running", "stage": "setup",
                       "operations": [], "operationsOmitted": 0,
                       "hostEvents": [], "hostEventsOmitted": 0,
                       "logBytesOmitted": 0, "malformedLogLines": 0, "logFilesRead": 0}

    def operation(self, name, elapsed_ms, outcome):
        # Names come from fixed command words, not command lines or paths.
        records = self.report["operations"]
        records.append({"operation": name, "elapsedMs": elapsed_ms, "outcome": outcome})
        if len(records) > 200:
            records.pop(0)
            self.report["operationsOmitted"] += 1

    @contextmanager
    def retain_logs(self, folder):
        try:
            yield
        finally:
            # Runs inside TemporaryDirectory's lifetime, after child cleanup.
            # Never preserve raw output, model JSON, database content or errors.
            try:
                for path in sorted(synthetic_log_directory(folder).glob("runtime*.jsonl"))[:5]:
                    with path.open("rb") as stream:
                        size = path.stat().st_size
                        skipped = max(0, size - 128 * 1024)
                        stream.seek(skipped)
                        data = stream.read(128 * 1024)
                    self.report["logFilesRead"] += 1
                    self.report["logBytesOmitted"] += skipped
                    if skipped:
                        data = data.partition(b"\n")[2]  # Discard a partial leading record.
                    for line in data.splitlines():
                        try:
                            entry = json.loads(line)
                            code = entry.get("eventCode", "")
                            if not isinstance(code, str) or not re.fullmatch(r"[a-z_]{1,48}\.[a-z_]{1,64}", code):
                                continue
                            # Deliberately omit free text; numeric timing remains
                            # available through the measured command records.
                            event = {"eventCode": code}
                            stamp = entry.get("timestampUtc")
                            if isinstance(stamp, str) and re.fullmatch(r"[0-9T:.Z+\-]{1,40}", stamp):
                                event["timestampUtc"] = stamp
                            if code == "host.slow_operation":
                                match = re.fullmatch(r"Host (worker round|control queue wait|control execution|reply handoff|reply write) took ([0-9]{1,12}) ms\.", entry.get("message", ""))
                                if match:
                                    event.update(phase=match[1], elapsedMs=int(match[2]))
                            self.report["hostEvents"].append(event)
                            if len(self.report["hostEvents"]) > 100:
                                self.report["hostEvents"].pop(0)
                                self.report["hostEventsOmitted"] += 1
                        except (ValueError, TypeError, AttributeError):
                            self.report["malformedLogLines"] += 1
            except OSError:
                self.report["logReadFailed"] = True

    def fail(self, error):
        code = "capacity.environment"
        if isinstance(error, subprocess.TimeoutExpired):
            code = "capacity.command_timeout"
        elif isinstance(error, KeyboardInterrupt):
            code = "capacity.interrupted"
        elif isinstance(error, RuntimeError):
            match = re.match(r"(capacity\.[a-z_]{1,64}):", str(error))
            if match:
                code = match[1]
        self.report.update(status="failed", failure={
            "code": code,
            "message": ("A command exceeded its allowed wait; its outcome may be unknown." if code == "capacity.command_timeout" else
                        "The diagnostic run was interrupted." if code == "capacity.interrupted" else
                        "The diagnostic run failed during the recorded stage."),
            "action": "Inspect the recorded stage, operations and retained host events. Check build, storage and process access. No mutation was retried; preserve evidence before another run."})
        self.write()

    def write(self):
        try:
            if self.folder is None:
                root = Path(__file__).resolve().parents[1]
                self.folder = root / ".tools" / ("capacity-evidence-" + uuid.uuid4().hex[:12])
            self.folder.mkdir(parents=True, exist_ok=True, mode=0o700)
            payload = json.dumps(self.report, indent=2, allow_nan=False)
            if len(payload.encode("utf-8")) > 256 * 1024:
                # Keep failure context rather than write a partial JSON document.
                payload = json.dumps({"status": self.report["status"], "stage": self.report["stage"],
                                      "failure": self.report.get("failure"), "evidenceTruncated": True})
            temporary = self.folder / "report.json.tmp"
            temporary.write_text(payload)
            temporary.replace(self.folder / "report.json")
            if self.report["status"] == "failed" and not self.announced:
                print(f"capacity.evidence_saved: Inspect report.json in diagnostic folder {self.folder.name} under .tools.", file=sys.stderr)
                self.announced = True
            return True
        except (OSError, ValueError, TypeError):
            print("capacity.evidence_write_failed: Could not preserve diagnostic evidence. Check .tools permissions and free space; the original run outcome is unchanged.", file=sys.stderr)
            return False
