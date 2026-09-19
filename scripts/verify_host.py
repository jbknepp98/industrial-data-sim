#!/usr/bin/env python3
"""Exercise the built host and CLI in separate processes using temporary fake state.

No network services or credentials are used. A lost mutation reply fails this
check; it is never retried. Only read-only readiness/status checks are polled.
"""
import argparse
import json
from pathlib import Path
import signal
import subprocess
import tempfile
import time


def require(condition, message):
    # Verification must remain active when Python runs with -O.
    if not condition:
        raise RuntimeError(message)


def verify(dotnet):
    root = Path(__file__).resolve().parents[1]
    dll = root / "src/IndustrialDataSim.Cli/bin/Release/net10.0/IndustrialDataSim.Cli.dll"
    if not dll.exists():
        raise RuntimeError("Build the solution in Release before running host verification.")
    command = [dotnet, str(dll)]

    def invoke(*args, expected=0):
        result = subprocess.run(command + list(args), capture_output=True, text=True, timeout=40)
        if result.returncode != expected:
            raise RuntimeError("A CLI operation failed its expected exit status; inspect the operation in a local test run.")
        return json.loads(result.stdout)

    with tempfile.TemporaryDirectory(prefix="sim-host-verification-") as folder:
        database = str(Path(folder) / "simulation.db")
        model_path = Path(folder) / "model.json"
        model = json.loads((root / "examples/constant-simulation.json").read_text())
        model["session"]["sessionId"] = "host-check-a"
        model["session"]["startUtc"] = "2026-09-01T00:00:00Z"
        model["session"]["endUtc"] = "2026-10-01T00:00:00Z"
        model["samplingIntervalMs"] = 1000
        model_path.write_text(json.dumps(model))
        invoke("session", "start", database, str(model_path))
        invoke("session", "pause", database, "host-check-a")

        def start_host():
            process = subprocess.Popen(command + ["host", "run", database], stdout=subprocess.PIPE,
                                       stderr=subprocess.PIPE, text=True)
            try:
                # Inspect liveness through the read-only control endpoint. Only
                # this safe probe is retried; lifecycle mutations below run once.
                end = time.monotonic() + 10
                while time.monotonic() < end:
                    probe = subprocess.run(command + ["host", "status", database], capture_output=True,
                                           text=True, timeout=5)
                    if probe.returncode == 0:
                        return process
                    if process.poll() is not None:
                        break
                    time.sleep(0.05)
                raise RuntimeError("Host did not become ready within ten seconds.")
            except BaseException:
                if process.poll() is None:
                    process.kill()
                process.communicate(timeout=5)
                raise

        host = start_host()
        try:
            # Direct commands cannot bypass the live host's database ownership.
            refused = invoke("session", "status", database, "host-check-a", expected=1)
            require(refused["errors"][0]["code"] == "runtime.owner_unavailable", 'Direct inspection did not respect host ownership.')
            invoke("live", "resume", database, "host-check-a")
            paused = invoke("live", "pause", database, "host-check-a")["result"]["session"]
            require(0 < paused["nextSlot"] < paused["totalSlots"], 'Pause did not preserve an unfinished, progressing session.')
            model["session"]["sessionId"] = "host-check-b"
            model["session"]["endUtc"] = "2026-09-01T00:00:03Z"
            for tag, generator in zip(model["session"]["outputTags"], model["generators"]):
                tag["name"] = "Peer." + tag["name"]
                generator["tag"] = tag["name"]
            model_path.write_text(json.dumps(model))
            invoke("live", "start", database, str(model_path))
            after = invoke("live", "status", database, "host-check-a")["result"]["session"]
            require(after["nextSlot"] == paused["nextSlot"], 'Paused cursor changed while the peer was admitted.')
            peer = invoke("live", "status", database, "host-check-b")["result"]["session"]
            require(peer["status"] == "Complete", 'Disjoint peer did not complete.')
            batches = invoke("live", "batches", database, "host-check-b")["result"]["batches"]
            require(batches and all("payload" not in batch for batch in batches), 'Batch inspection returned no metadata or exposed payload.')
            invoke("host", "stop", database)
            stdout, _ = host.communicate(timeout=10)
            require(host.returncode == 0 and json.loads(stdout)["valid"], 'Control stop did not produce successful host exit.')
        finally:
            if host.poll() is None:
                host.kill()
                host.communicate(timeout=5)

        # Pause survives an actual process restart. Ctrl+C must exit gracefully.
        host = start_host()
        try:
            resumed = invoke("live", "status", database, "host-check-a")["result"]["session"]
            require(resumed["status"] == "Paused" and resumed["nextSlot"] == paused["nextSlot"], 'Pause state or cursor changed after host restart.')
            host.send_signal(signal.SIGINT)
            stdout, _ = host.communicate(timeout=10)
            require(host.returncode == 130 and json.loads(stdout)["valid"], 'Ctrl+C did not produce a graceful exit.')
        finally:
            if host.poll() is None:
                host.kill()
                host.communicate(timeout=5)
        final = invoke("session", "status", database, "host-check-a")["result"]["session"]
        require(final["status"] == "Paused" and final["nextSlot"] == paused["nextSlot"], 'State changed after graceful shutdown.')
        invoke("session", "cancel", database, "host-check-a", "discard-pending")
        invoke("session", "release", database, "host-check-a")
    print("Host verification passed: ownership, live controls, peer completion, pause persistence, process restart, graceful control stop and Ctrl+C.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    try:
        verify(args.dotnet)
    except RuntimeError as error:
        parser.exit(1, f"Host verification failed: {error}\n")
    except (OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired):
        parser.exit(1, "Host verification could not complete. Check the Release build, SDK path, local process/pipe permissions, and temporary storage. No mutation is automatically retried.\n")
