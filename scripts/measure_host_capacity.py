#!/usr/bin/env python3
"""Measure synthetic history growth and CLI control latency under sustained load.

Uses only fixed repository fixtures and a temporary database. Every mutation is
submitted once. A missing reply aborts the probe; it never triggers a retry.
"""
import argparse
import copy
from contextlib import closing
import json
import math
from pathlib import Path
import sqlite3
import subprocess
import tempfile
import time


def require(condition, code, message):
    if not condition:
        raise RuntimeError(f"{code}: {message}")


def summarize_latency(samples):
    """Nearest-rank percentiles; retain the maximum rather than hiding outliers."""
    require(bool(samples), "capacity.no_samples", "No control requests were measured. Rerun with the host available for the full observation window.")
    ordered = sorted(samples)
    return {"samples": len(samples), "p50Ms": ordered[math.ceil(len(samples) * .50) - 1],
            "p95Ms": ordered[math.ceil(len(samples) * .95) - 1], "maximumMs": ordered[-1]}


def control_timing_sample(timing, elapsed_ms):
    fields = ("setupMs", "connectMs", "writeMs", "replyMs", "clientMs")
    require(all(isinstance(timing.get(key), (int, float)) and not isinstance(timing[key], bool)
                and math.isfinite(timing[key]) and timing[key] >= 0 for key in fields)
            and math.isfinite(elapsed_ms) and elapsed_ms >= timing["clientMs"]
            and sum(timing[key] for key in fields[:-1]) <= timing["clientMs"] + .1,
            "capacity.invalid_timing", "Diagnostic timing fields are missing or inconsistent. Rebuild matching tools and rerun before attributing latency to any phase.")
    return {key: timing[key] for key in fields} | {
        "totalMs": elapsed_ms, "processOverheadMs": elapsed_ms - timing["clientMs"]}


def make_model(template, session_id, historical):
    model = copy.deepcopy(template)
    session = model["session"]
    session["sessionId"] = session_id
    session["startUtc"] = "2026-09-01T00:00:00Z"
    session["endUtc"] = "2026-09-08T00:00:00Z" if historical else "2026-10-01T00:00:00Z"
    model["samplingIntervalMs"] = 60000 if historical else 10
    for tag in session["outputTags"]:
        tag["name"] = session_id + "." + tag["name"]
    for generator in model["generators"]:
        generator["tag"] = session_id + "." + generator["tag"]
    return model


def storage_bytes(database):
    return sum(path.stat().st_size for path in
               (database, Path(str(database) + "-wal"), Path(str(database) + "-shm")) if path.exists())


def audit_snapshot(database):
    # This diagnostic query is allowed only after the host has exited. It reads
    # synthetic probe state, never accepts a user database, and performs no writes.
    # The .NET owner checkpoints on close. Immutable reading avoids asking the
    # platform Python SQLite to create WAL sidecars for a read-only connection.
    # Refuse outstanding WAL rather than silently reading an older main file.
    wal = Path(str(database) + "-wal")
    require(not wal.exists() or wal.stat().st_size == 0, "capacity.uncheckpointed_state",
            "Closed synthetic state still contains a WAL. Check host shutdown/checkpoint behavior before reading storage metrics; do not delete the WAL.")
    with closing(sqlite3.connect(database.as_uri() + "?mode=ro&immutable=1", uri=True)) as connection:
        batches, points, payloads = connection.execute(
            "SELECT COUNT(*), COALESCE(SUM(point_count),0), SUM(CASE WHEN payload IS NOT NULL AND length(payload)>0 THEN 1 ELSE 0 END) FROM batches"
        ).fetchone()
        attempts = connection.execute("SELECT COUNT(*) FROM attempts").fetchone()[0]
    return {"databaseBytes": database.stat().st_size, "batches": batches,
            "points": points, "retainedPayloadBatches": payloads or 0, "attempts": attempts}


def measure(dotnet, history_count, seconds, diagnose=False, command_runner=None, host_started=None):
    root = Path(__file__).resolve().parents[1]
    dll = root / "src/IndustrialDataSim.Cli/bin/Release/net10.0/IndustrialDataSim.Cli.dll"
    require(dll.exists(), "capacity.build_missing", "Build IndustrialDataSim.slnx in Release before measuring host capacity.")
    command = [dotnet, str(dll)]
    diagnostic_dll = root / "tools/IndustrialDataSim.ControlProbe/bin/Release/net10.0/IndustrialDataSim.ControlProbe.dll"
    require(not diagnose or diagnostic_dll.exists(), "capacity.probe_missing", "Build the entire solution in Release to include the control diagnostic probe.")
    timing_samples = []
    constant = json.loads((root / "examples/constant-simulation.json").read_text())
    sequence = json.loads((root / "examples/sequence-simulation.json").read_text())

    def invoke(*arguments, timeout=40):
        start = time.monotonic()
        started_unix_ms = time.time() * 1000
        diagnostic = diagnose and arguments[:2] == ("live", "list")
        invocation = [dotnet, str(diagnostic_dll), arguments[2]] if diagnostic else command + list(arguments)
        result = (command_runner(invocation, timeout) if command_runner else
                  subprocess.run(invocation, capture_output=True, text=True, timeout=timeout))
        elapsed_ms = (time.monotonic() - start) * 1000
        operation = ".".join(arguments[:2])  # Fixed command words only, never paths or configuration.
        require(result.returncode == 0, "capacity.command_failed",
                f"{operation} did not return success. Run the host verification suite and inspect local storage/pipe permissions. This probe will not retry the operation.")
        reply = json.loads(result.stdout)
        if diagnostic:
            timing = reply["timings"]
            # Residual includes process launch/JIT before Main, output encoding
            # and process exit; it is not a pure startup measurement.
            timing_samples.append(control_timing_sample(timing, elapsed_ms) | {"startedUnixMs": started_unix_ms})
        require(reply["valid"], "capacity.invalid_reply", f"{operation} returned an invalid response. Run the CLI tests before repeating this probe.")
        return reply["result"], elapsed_ms

    with tempfile.TemporaryDirectory(prefix="sim-host-capacity-") as folder:
        database = Path(folder) / "state.db"
        model_path = Path(folder) / "model.json"
        for index in range(history_count):
            model_path.write_text(json.dumps(make_model(constant, f"history-{index:02}", True)))
            invoke("session", "start", str(database), str(model_path))
        if history_count:
            finished, history_ms = invoke("session", "run-simulated", str(database), "1000", timeout=180)
            require(all(session["status"] == "Complete" for session in finished["sessions"]),
                    "capacity.history_incomplete", "Historical sessions did not complete. Inspect runtime tests before using the storage results.")
            history = audit_snapshot(database)
            require(history["points"] == history_count * 30240 and history["retainedPayloadBatches"] == 0,
                    "capacity.history_mismatch", "Historical point count or payload pruning differed from the fixture. Check generation and pruning before interpreting growth.")
        else:
            history_ms = 0
            history = {"databaseBytes": 0, "batches": 0, "points": 0, "retainedPayloadBatches": 0, "attempts": 0}

        active_ids = [f"active-{index}" for index in range(4)]
        for index, session_id in enumerate(active_ids):
            template = constant if index < 2 else sequence
            model_path.write_text(json.dumps(make_model(template, session_id, False)))
            invoke("session", "start", str(database), str(model_path))
        # Files avoid blocking the child on full stdout/stderr pipes. Runtime
        # diagnostics already rotate; only the terminal result goes to stdout.
        with (Path(folder) / "host-output.txt").open("w+") as output, (Path(folder) / "host-errors.txt").open("w+") as errors:
            host = subprocess.Popen(command + ["host", "run", str(database)], stdout=output, stderr=errors)
            try:
                if host_started:
                    host_started(host.pid)
                deadline = time.monotonic() + 15
                while True:
                    probe = subprocess.run(command + ["host", "status", str(database)], capture_output=True, timeout=5)
                    if probe.returncode == 0:
                        break
                    require(host.poll() is None and time.monotonic() < deadline, "capacity.host_not_ready",
                            "Host did not become available within fifteen seconds. Check process/pipe access and run verify_host.py.")
                    time.sleep(.05)  # Only read-only readiness is retried.

                start = time.monotonic()
                latencies = []
                first_positions = None
                last_positions = None
                maximum_storage = 0
                while time.monotonic() - start < seconds:
                    inventory, duration = invoke("live", "list", str(database))
                    latencies.append(duration)
                    active = [session for session in inventory["sessions"] if session["sessionId"] in active_ids]
                    require(len(active) == 4 and all(session["status"] == "Ready" for session in active),
                            "capacity.load_ended", "An active fixture stopped before the observation window ended. Inspect status and reduce the requested duration if the fixture completed.")
                    positions = {session["sessionId"]: session["nextSlot"] for session in active}
                    if last_positions is not None:
                        require(all(positions[key] > last_positions[key] for key in positions),
                                "capacity.no_progress", "An active session did not advance between observations. Investigate scheduler fairness or runtime errors before interpreting latency.")
                    first_positions = first_positions or positions
                    last_positions = positions
                    maximum_storage = max(maximum_storage, storage_bytes(database))
                    require(maximum_storage <= 256 * 1024 * 1024, "capacity.storage_limit",
                            "Synthetic state exceeded the 256 MiB probe budget. Reduce the observation duration; do not delete production recovery state.")
                    time.sleep(.5)
                observed_seconds = time.monotonic() - start

                paused, pause_ms = invoke("live", "pause", str(database), active_ids[0])
                checked, _ = invoke("live", "status", str(database), active_ids[0])
                require(checked["session"]["nextSlot"] == paused["session"]["nextSlot"] and checked["session"]["status"] == "Paused",
                        "capacity.pause_changed", "Paused state advanced after acknowledgement. Inspect host pause-boundary tests before continuing development.")
                _, resume_ms = invoke("live", "resume", str(database), active_ids[0])
                _, stop_ms = invoke("host", "stop", str(database))
                host.wait(timeout=15)
                require(host.returncode == 0, "capacity.stop_failed", "Host did not exit successfully after control stop. Inspect graceful-shutdown tests; no command is retried.")
                output.seek(0)
                require(json.load(output)["valid"], "capacity.stop_reply", "Host terminal result was invalid. Run process verification before accepting capacity results.")
            finally:
                if host.poll() is None:
                    host.kill()
                    host.wait(timeout=15)

        final = audit_snapshot(database)
        slow_operations = []
        for log in sorted((Path(folder) / "logs").glob("runtime*.jsonl")):
            for line in log.read_text().splitlines():
                entry = json.loads(line)
                if entry.get("eventCode") == "host.slow_operation":
                    slow_operations.append({"timestampUtc": entry["timestampUtc"], "message": entry["message"]})
        # Resume and stop can leave Pending work, so payloads here are reported,
        # not required to be zero. Only the finished-history snapshot must prune.
        return {"schemaVersion": 1, "mode": "simulation-only", "historySessions": history_count,
                "inventoryClient": "diagnostic-probe" if diagnose else "standard-cli",
                "historyGenerationMs": history_ms, "history": history, "activeSessions": 4,
                "patterns": ["constant", "sequence"], "requestedSeconds": seconds,
                "observedSeconds": observed_seconds, "liveListLatency": summarize_latency(latencies),
                "controlTimingSamples": timing_samples,
                "slowHostOperations": slow_operations[:100], "slowHostOperationsOmitted": max(0, len(slow_operations) - 100),
                "pauseMs": pause_ms, "resumeMs": resume_ms, "stopReplyMs": stop_ms,
                "observedCandidateSlotAdvance": sum(last_positions.values()) - sum(first_positions.values()),
                "maximumSampledStorageBytes": maximum_storage, "afterStop": final}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--history", type=int, choices=(0, 20, 80), default=0)
    parser.add_argument("--seconds", type=int, choices=(5, 30, 60), default=30)
    parser.add_argument("--diagnose", action="store_true", help="Use the read-only diagnostic client to separate control timing phases.")
    args = parser.parse_args()
    try:
        print(json.dumps(measure(args.dotnet, args.history, args.seconds, args.diagnose), indent=2))
    except RuntimeError as error:
        parser.exit(1, f"{error}\n")
    except (OSError, ValueError, KeyError, TypeError, sqlite3.Error, subprocess.TimeoutExpired):
        parser.exit(1, "capacity.environment: Measurement could not complete. Check the Release build, temporary storage, child-process and local-pipe access. Run verify_host.py first; no mutation is automatically retried.\n")
