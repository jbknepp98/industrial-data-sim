#!/usr/bin/env python3
"""Sample slow synthetic inventory clients and their host on macOS; no deployment access.

Sampling perturbs timing. Use these traces for diagnosis, not throughput claims.
Native stacks may not resolve managed method names. Output stays in ignored .tools.
"""
import argparse
import json
from pathlib import Path
import platform
import sqlite3
import subprocess
import uuid

from measure_host_capacity import measure


def profile(dotnet):
    if platform.system() != "Darwin" or not Path("/usr/bin/sample").exists():
        raise RuntimeError("profile.unsupported: This collector requires macOS /usr/bin/sample. Use the portable --diagnose measurement on other platforms.")
    root = Path(__file__).resolve().parents[1]
    folder = root / ".tools" / ("control-profile-" + uuid.uuid4().hex[:12])
    folder.mkdir(parents=True)
    samplers = []
    host_pid = None
    captures = 0

    def observe_host(process_id):
        nonlocal host_pid
        host_pid = process_id

    def run(command, timeout):
        nonlocal captures
        if command[2:4] != ["live", "list"]:
            return subprocess.run(command, capture_output=True, text=True, timeout=timeout)
        with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as client:
            try:
                try:
                    stdout, stderr = client.communicate(timeout=.5)
                except subprocess.TimeoutExpired:
                    if captures < 3:
                        # Only children created for this synthetic case are sampled.
                        # The sample process is reaped after measurement so waiting
                        # for its exit is not charged to the control operation.
                        captures += 1
                        for label, process_id in (("client", client.pid), ("host", host_pid)):
                            if process_id is None:
                                continue
                            trace = folder / f"{label}-{captures}.txt"
                            sampler = subprocess.Popen(["/usr/bin/sample", str(process_id), "1", "-mayDie", "-file", str(trace)],
                                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                            samplers.append((sampler, trace))
                    stdout, stderr = client.communicate(timeout=timeout - .5)
                return subprocess.CompletedProcess(command, client.returncode, stdout, stderr)
            finally:
                if client.poll() is None:
                    client.kill()
                    client.communicate(timeout=5)

    try:
        result = measure(dotnet, 80, 60, command_runner=run, host_started=observe_host)
    finally:
        for sampler, _ in samplers:
            try:
                sampler.wait(timeout=5)
            except subprocess.TimeoutExpired:
                sampler.kill()
                sampler.wait(timeout=5)
    result["profilingPerturbsTiming"] = True
    result["profiles"] = [{"file": trace.name, "exitCode": sampler.returncode,
                           "written": trace.exists()} for sampler, trace in samplers]
    (folder / "report.json").write_text(json.dumps(result, indent=2))
    print(json.dumps({"outputDirectory": str(folder.relative_to(root)),
                      "profiles": result["profiles"], "latency": result["liveListLatency"]}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    try:
        profile(args.dotnet)
    except RuntimeError as error:
        parser.exit(1, f"{error}\n")
    except (OSError, ValueError, KeyError, TypeError, sqlite3.Error, subprocess.TimeoutExpired):
        parser.exit(1, "profile.environment: Profiling could not finish. Check the Release build, temporary storage and permission to sample the synthetic child process. No mutation is automatically retried.\n")
