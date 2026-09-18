"""Read-only comparison of a saved preview with a saved Historian read-back.

Checks retained records and required value/quality transitions. It cannot prove
acceptance of omitted repeated samples and never authorizes retries or writes.
"""
import argparse
import json
import math
from datetime import datetime
from pathlib import Path


def timestamp(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("Timestamp lacks a timezone; provide UTC timestamps.")
    return parsed


def verify(preview, response, start, end):
    expected = preview["data"]
    rows = response["tl"]
    if not isinstance(rows, list) or not expected or end <= start:
        raise ValueError("Expected nonempty preview data, a read-back tl array, and start before end.")
    seen = set()
    checked = 0
    for item in rows:
        name = item["t"]["n"]
        if name not in expected or name in seen:
            raise ValueError("Read-back contains an unexpected or repeated tag; check the requested tag set.")
        seen.add(name)
        wanted = expected[name]
        wanted_times = [timestamp(point["t"]) for point in wanted]
        if wanted_times != sorted(set(wanted_times)):
            raise ValueError("Preview timestamps are repeated or unordered; regenerate the preview.")
        if any(not start <= time < end for time in wanted_times):
            raise ValueError("Preview extends beyond the selected range; supply the complete preview range.")
        index = dict(zip(wanted_times, wanted))
        required = set()
        previous = None
        for point, time in zip(wanted, wanted_times):
            if previous is None or (point["v"], point["q"]) != (previous["v"], previous["q"]):
                required.add(time)
            previous = point
        actual = [point for point in item["d"] if start <= timestamp(point["t"]) < end]
        times = [timestamp(point["t"]) for point in actual]
        if times != sorted(set(times)) or any(time not in index for time in times):
            raise ValueError("Read-back has duplicate, unordered, or off-grid timestamps; inspect the saved records.")
        if not required.issubset(times):
            raise ValueError("Read-back is missing a required transition; preserve the evidence and investigate without replay.")
        for point, time in zip(actual, times):
            sent = index[time]
            got, value = point["v"], sent["v"]
            if isinstance(value, bool):
                type_name = "System.Boolean"
                matches = isinstance(got, (bool, int, float)) and got in (0, 1) and bool(got) == value
            elif isinstance(value, (int, float)):
                type_name = "System.Double"
                matches = (isinstance(got, (int, float)) and not isinstance(got, bool)
                           and math.isfinite(got) and math.isclose(got, value, rel_tol=1e-12, abs_tol=1e-9))
            else:
                type_name = "System.String"
                matches = isinstance(value, str) and isinstance(got, str) and got == value
            if item["t"]["t"] != type_name or not matches or point["q"] != sent["q"]:
                raise ValueError("Stored type, value, or quality differs; inspect the saved records without replay.")
            checked += 1
    if seen != set(expected):
        raise ValueError("Read-back is missing a requested tag; check the query and saved response.")
    return checked


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("preview", type=Path)
    parser.add_argument("readback", type=Path)
    parser.add_argument("--start", required=True, help="Inclusive UTC range start.")
    parser.add_argument("--end", required=True, help="Exclusive UTC range end.")
    args = parser.parse_args()
    try:
        preview = json.loads(args.preview.read_text())
        response = json.loads(args.readback.read_text())
        checked = verify(preview, response, timestamp(args.start), timestamp(args.end))
    except (OSError, ValueError, KeyError, TypeError, AttributeError, OverflowError):
        # These artifacts can contain private data. Never echo raw parser errors.
        parser.exit(1, "Read-back verification failed. Check file access, JSON shape, UTC range, tag set, and timestamp/value/quality transitions. Preserve artifacts; do not replay missing points.\n")
    print(f"Verified {checked} retained records and required transitions; omitted repeats remain unverified.")


if __name__ == "__main__":
    main()
