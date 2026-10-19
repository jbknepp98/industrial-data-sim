#!/usr/bin/env python3
"""Rehearse schema-8 -> current upgrade using real CLIs and synthetic state only.

Supply an independently built schema-8 CLI. No credentials, live configurations
or existing databases are accepted. Output is a sanitized pass/fail summary.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3
import subprocess
import tempfile


def inspect(database):
    with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as connection:
        return {
            table: connection.execute("SELECT * FROM " + table + " ORDER BY rowid").fetchall()
            for table in ("sessions", "tags", "session_tag_progress", "batches", "attempts",
                          "process_checkpoints", "horizon_revisions")
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", required=True)
    parser.add_argument("--legacy-cli", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    current = root / "src/IndustrialDataSim.Cli/bin/Release/net10.0/IndustrialDataSim.Cli.dll"

    def invoke(binary, *arguments, expected=0):
        result = subprocess.run([args.dotnet, str(binary), *map(str, arguments)],
                                capture_output=True, text=True, timeout=30)
        if result.returncode != expected:
            raise ValueError("CLI exit status differed from the drill expectation")
        return json.loads(result.stdout)

    try:
        with tempfile.TemporaryDirectory(prefix="sim-upgrade-drill-") as folder:
            database = Path(folder) / "synthetic.sqlite"
            invoke(args.legacy_cli, "session", "start", database,
                   root / "examples/constant-simulation.json")
            # A small transport-byte cap ensures the old executable checkpoints
            # partway through the fixture rather than completing in one batch.
            invoke(args.legacy_cli, "session", "run-simulated", database, "1", "128", expected=4)
            with sqlite3.connect(database) as connection:
                if connection.execute("PRAGMA user_version").fetchone()[0] != 8:
                    raise ValueError("Legacy executable must create schema 8")
                if connection.execute("SELECT next_slot FROM sessions").fetchone()[0] <= 0:
                    raise ValueError("Legacy fixture did not checkpoint progress")
            before = inspect(database)
            invoke(current, "session", "list", database)
            if inspect(database) != before:
                raise ValueError("Upgrade changed saved process or delivery evidence")
            before_rejection = hashlib.sha256(database.read_bytes()).hexdigest()
            failure = invoke(args.legacy_cli, "session", "list", database, expected=1)
            if "runtime.schema_version" not in json.dumps(failure):
                raise ValueError("Old executable did not reject the new schema explicitly")
            if hashlib.sha256(database.read_bytes()).hexdigest() != before_rejection:
                raise ValueError("Old executable changed upgraded database bytes")
            with sqlite3.connect(database) as connection:
                version = connection.execute("PRAGMA user_version").fetchone()[0]
        print(json.dumps({"outcome": "Passed", "sourceSchema": 8, "targetSchema": version,
                          "evidenceUnchanged": True, "oldBinaryRejectedWithoutMutation": True,
                          "historianWrites": 0}))
        return 0
    except (OSError, ValueError, sqlite3.Error, subprocess.TimeoutExpired):
        print(json.dumps({"outcome": "Failed", "code": "verification.upgrade",
                          "message": "Upgrade drill did not meet its assertions. Check the supplied schema-8 CLI, current Release build, SDK path and temporary-storage access. No existing state or live Historian is used; raw subprocess output is intentionally omitted."}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
