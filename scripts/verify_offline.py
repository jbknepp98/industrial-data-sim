"""Check schemas and gate clocks independently of the C# implementation.

Run after a Release build. No network requests or Historian writes are made.
"""
import argparse
import copy
import json
import random
import subprocess
import tempfile
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parent.parent


def require(condition, message):
    # Unlike assert, verification must remain active under python -O.
    if not condition:
        raise ValueError(message)


def run_cli(dotnet, model):
    assembly = ROOT / "src/IndustrialDataSim.Cli/bin/Release/net10.0/IndustrialDataSim.Cli.dll"
    with tempfile.TemporaryDirectory() as directory:
        config = Path(directory) / "simulation.json"
        config.write_text(json.dumps(model), encoding="utf-8")
        result = subprocess.run(
            [dotnet, str(assembly), "dry-run", str(config)],
            capture_output=True, text=True, timeout=30, check=False,
        )
    require(result.returncode in (0, 1), "CLI did not complete normally; check the SDK and Release build.")
    return result.returncode, json.loads(result.stdout), result.stdout


def validator(filename):
    folder = ROOT / "schemas"
    registry = Registry().with_resources(
        (path.as_uri(), Resource.from_contents(json.loads(path.read_text())))
        for path in folder.glob("*.json")
    )
    schema = json.loads((folder / filename).read_text())
    # Resolve relative references from this checkout, never over the network.
    schema["$id"] = (folder / filename).as_uri()
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, registry=registry, format_checker=FormatChecker())


def check_examples(dotnet):
    simulation_validator = validator("simulation-v1.schema.json")
    count = 0
    for path in sorted((ROOT / "examples").glob("*simulation.json")):
        model = json.loads(path.read_text())
        simulation_validator.validate(model)
        code, result, _ = run_cli(dotnet, model)
        require(code == 0 and result["valid"], f"Example failed: {path.name}")
        count += 1
    validator("session-header-v1.schema.json").validate(
        json.loads((ROOT / "examples/session-header.json").read_text())
    )
    validator("constant-simulation-v1.schema.json").validate(
        json.loads((ROOT / "examples/constant-simulation.json").read_text())
    )
    base = json.loads((ROOT / "examples/boolean-gate-simulation.json").read_text())
    mutations = [
        ("missing mode", lambda gate: gate.pop("whenFalse")),
        ("invalid mode", lambda gate: gate.update(whenFalse="null")),
        ("null pattern", lambda gate: gate.update(pattern=None)),
        ("child tag", lambda gate: gate["pattern"].update(tag="Other")),
        ("nested gate", lambda gate: gate.update(pattern={"kind": "booleanGate"})),
        ("unknown field", lambda gate: gate.update(buffer=True)),
    ]
    for label, mutate in mutations:
        model = copy.deepcopy(base)
        mutate(model["generators"][1])
        require(not simulation_validator.is_valid(model), f"Schema accepted {label}.")
        code, result, _ = run_cli(dotnet, model)
        require(code == 1 and not result["valid"] and "data" not in result,
                f"CLI accepted {label} or emitted partial data.")
    first = run_cli(dotnet, base)
    require(first[2] == run_cli(dotnet, base)[2], "Repeated gate preview changed.")
    require(first[1]["pointCount"] == 42, "Gate example should emit 42 points.")
    print(f"{count} simulation examples plus header/subset schemas passed; six invalid shapes rejected.")


def check_gate_clocks(dotnet):
    base = json.loads((ROOT / "examples/boolean-gate-simulation.json").read_text())
    random_source = random.Random(98123)
    checked = 0
    for scenario in range(100):
        model = copy.deepcopy(base)
        model["session"].update(startUtc="2026-09-01T00:00:00Z", endUtc="2026-09-01T00:00:00.040Z")
        interval = random_source.choice([1, 3, 5, 7])
        model["samplingIntervalMs"] = interval
        steps = [
            {"value": random_source.choice([False, True]), "durationMs": random_source.randint(1, 7)}
            for _ in range(random_source.randint(1, 9))
        ]
        model["generators"][0]["steps"] = steps
        for generator in model["generators"][1:]:
            generator["pattern"]["ratePerSecond"] = 1000
        if scenario % 2:
            model["generators"].reverse()
        code, result, _ = run_cli(dotnet, model)
        require(code == 0, f"Gate scenario {scenario} failed to generate.")

        # Independent 1 ms event simulation: accumulate counters instead of
        # reproducing the engine's interval lookup and prefix-time arithmetic.
        states = []
        for step in steps:
            states.extend([step["value"]] * step["durationMs"])
        states.extend([steps[-1]["value"]] * max(0, 40 - len(states)))
        active_time = 0
        first_open = None
        expected = [[], [], []]
        for time_ms, state in enumerate(states[:40]):
            if state and first_open is None:
                first_open = time_ms
            if time_ms % interval == 0:
                expected[0].append((time_ms, state))
                if state:
                    expected[1].append((time_ms, active_time))
                    expected[2].append((time_ms, time_ms - first_open))
            if state:
                active_time += 1
        for tag, wanted in zip(model["session"]["outputTags"], expected):
            actual = result["data"][tag["name"]]
            require(len(actual) == len(wanted), f"Gate scenario {scenario}: incorrect point count.")
            for point, (time_ms, value) in zip(actual, wanted):
                milliseconds = round(float(point["t"].split(":")[-1][:-1]) * 1000)
                require(milliseconds == time_ms and point["v"] == value and point["q"] == 192,
                        f"Gate scenario {scenario}: timestamp, value, or quality mismatch.")
                checked += 1
        require(result["pointCount"] == sum(map(len, expected)), "Incorrect emitted count.")
    print(f"100 independent gate scenarios passed; {checked} emitted samples checked.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet", help="Path to the .NET 10 executable.")
    args = parser.parse_args()
    try:
        check_examples(args.dotnet)
        check_gate_clocks(args.dotnet)
    except (OSError, ValueError, subprocess.TimeoutExpired) as error:
        parser.exit(1, f"Offline verification failed: {error}\nCheck prerequisites and rebuild Release before rerunning.\n")


if __name__ == "__main__":
    main()
