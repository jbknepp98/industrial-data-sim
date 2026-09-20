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
import time

from diagnostic_evidence import DiagnosticEvidence

from measure_host_capacity import measure


def profile(dotnet):
    if platform.system() != "Darwin" or not Path("/usr/bin/sample").exists():
        raise RuntimeError("profile.unsupported: This collector requires macOS /usr/bin/sample. Use the portable --diagnose measurement on other platforms.")
    root = Path(__file__).resolve().parents[1]
    folder = root / ".tools" / ("control-profile-" + uuid.uuid4().hex[:12])
    folder.mkdir(parents=True)
    evidence = DiagnosticEvidence(folder)
    collector = InventoryProfiler(evidence)
    result = None
    try:
        result = measure(dotnet, 80, 60, command_runner=collector.run,
                         host_started=collector.observe_host, evidence=evidence)
    except BaseException as error:
        # Also cover failures before measure's own evidence handler is entered.
        evidence.fail(error)
        raise
    finally:
        collector.finish()
        evidence.report["profilingPerturbsTiming"] = True
        if result is not None:
            evidence.report["measurement"] = result
        evidence.write()
    print(json.dumps({"outputDirectory": str(folder.relative_to(root)),
                      "profiles": evidence.report["profiles"], "latency": result["liveListLatency"]}, indent=2))


class InventoryProfiler:
    """Measure launch separately; bound capture count and use one command deadline."""
    def __init__(self, evidence):
        self.evidence = evidence
        self.samplers = []
        self.host_pid = None
        self.captures = 0
        evidence.report.update(inventoryProcesses=[], inventoryProcessesOmitted=0)

    def observe_host(self, process_id):
        self.host_pid = process_id

    def capture(self, client, record):
        if client.poll() is not None:
            record["captureStatus"] = "client-exited"
            return
        if self.captures >= 3:
            record["captureStatus"] = "limit-reached"
            return
        self.captures += 1
        record["captureStatus"] = "started"
        for label, process_id in (("client", client.pid), ("host", self.host_pid)):
            if process_id is None:
                continue
            trace = self.evidence.folder / f"{label}-{self.captures}.txt"
            try:
                sampler = subprocess.Popen(["/usr/bin/sample", str(process_id), "1", "-mayDie", "-file", str(trace)],
                                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                self.samplers.append((sampler, trace))
            except OSError:
                record["captureStatus"] = "sampling-failed"

    def run(self, command, timeout):
        if command[2:4] != ["live", "list"]:
            return subprocess.run(command, capture_output=True, text=True, timeout=timeout)
        started = time.monotonic()
        deadline = started + timeout
        record = {"outcome": "failed", "captureStatus": "not-needed"}
        try:
            with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as client:
                launch_seconds = time.monotonic() - started
                record.update(launchMs=launch_seconds * 1000,
                              launchCaptureUnavailable=launch_seconds >= .5)
                # Popen does not expose a usable child until it returns. A launch
                # over 500 ms is evidence even if the child immediately exits;
                # a later stack sample cannot reconstruct that missed interval.
                try:
                    if launch_seconds >= .5:
                        self.capture(client, record)
                    def remaining():
                        value = deadline - time.monotonic()
                        if value <= 0:
                            raise subprocess.TimeoutExpired(command, timeout)
                        return value
                    if launch_seconds < .5:
                        try:
                            stdout, stderr = client.communicate(timeout=min(.5 - launch_seconds, remaining()))
                        except subprocess.TimeoutExpired:
                            self.capture(client, record)
                            stdout, stderr = client.communicate(timeout=remaining())
                    else:
                        stdout, stderr = client.communicate(timeout=remaining())
                    record["outcome"] = "complete" if client.returncode == 0 else "nonzero-exit"
                    return subprocess.CompletedProcess(command, client.returncode, stdout, stderr)
                finally:
                    if client.poll() is None:
                        client.kill()
                        client.communicate(timeout=5)
        finally:
            record["totalMs"] = (time.monotonic() - started) * 1000
            records = self.evidence.report["inventoryProcesses"]
            records.append(record)
            if len(records) > 200:
                records.pop(0)
                self.evidence.report["inventoryProcessesOmitted"] += 1

    def finish(self):
        profiles = []
        for sampler, trace in self.samplers:
            cleanup_failed = False
            try:
                try:
                    sampler.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    sampler.kill()
                    sampler.wait(timeout=5)
            except (OSError, subprocess.TimeoutExpired):
                cleanup_failed = True
            profiles.append({"file": trace.name, "exitCode": sampler.returncode,
                             "written": trace.exists(), "cleanupFailed": cleanup_failed})
        self.evidence.report["profiles"] = profiles


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    try:
        profile(args.dotnet)
    except KeyboardInterrupt:
        parser.exit(130, "profile.interrupted: Synthetic profiling interrupted; inspect the retained report before rerunning. No mutation was retried.\n")
    except RuntimeError as error:
        parser.exit(1, f"{error}\n")
    except (OSError, ValueError, KeyError, TypeError, sqlite3.Error, subprocess.TimeoutExpired):
        parser.exit(1, "profile.environment: Profiling could not finish. Check the Release build, temporary storage and permission to sample the synthetic child process. No mutation is automatically retried.\n")
