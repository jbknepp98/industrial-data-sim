"""Check percentile semantics and isolated fixture construction without a host."""
import unittest
from unittest.mock import patch
import sqlite3
import tempfile
from pathlib import Path

from measure_host_capacity import audit_snapshot, control_timing_sample, make_model, summarize_latency
from profile_control_macos import profile


class CapacityMeasurementTests(unittest.TestCase):
    def test_native_collector_refuses_unsupported_platform_with_alternative(self):
        with patch("profile_control_macos.platform.system", return_value="Linux"):
            with self.assertRaisesRegex(RuntimeError, "profile.unsupported:.*portable --diagnose"):
                profile("dotnet")

    def test_phase_timings_preserve_outlier_and_separate_process_overhead(self):
        timing = {"setupMs": 2, "connectMs": 1, "writeMs": 1, "replyMs": 4900, "clientMs": 4905}
        sample = control_timing_sample(timing, 4930)
        self.assertEqual(sample["replyMs"], 4900)
        self.assertEqual(sample["processOverheadMs"], 25)
        for broken in ({}, timing | {"replyMs": float("nan")}, timing | {"clientMs": 1}):
            with self.assertRaisesRegex(RuntimeError, "capacity.invalid_timing:.*Rebuild"):
                control_timing_sample(broken, 4930)

    def test_snapshot_reads_checkpointed_state_without_creating_sidecars(self):
        with tempfile.TemporaryDirectory() as folder:
            database = Path(folder) / "state.db"
            connection = sqlite3.connect(database)
            connection.executescript("CREATE TABLE batches(point_count INTEGER, payload TEXT); CREATE TABLE attempts(batch_id INTEGER); INSERT INTO batches VALUES(3,NULL); INSERT INTO attempts VALUES(1);")
            connection.close()
            before = database.read_bytes()
            snapshot = audit_snapshot(database)
            self.assertEqual(snapshot["points"], 3)
            self.assertEqual(snapshot["retainedPayloadBatches"], 0)
            self.assertEqual(database.read_bytes(), before)
            self.assertEqual(len(list(Path(folder).iterdir())), 1)
            Path(str(database) + "-wal").write_bytes(b"outstanding")
            with self.assertRaisesRegex(RuntimeError, "capacity.uncheckpointed_state:.*do not delete"):
                audit_snapshot(database)

    def test_nearest_rank_retains_slow_outlier(self):
        samples = [1] * 19 + [100]
        self.assertEqual(summarize_latency(samples),
                         {"samples": 20, "p50Ms": 1, "p95Ms": 1, "maximumMs": 100})
        self.assertEqual(summarize_latency([4])["p95Ms"], 4)

    def test_empty_measurements_fail_with_guidance(self):
        with self.assertRaisesRegex(RuntimeError, "capacity.no_samples:.*Rerun"):
            summarize_latency([])

    def test_models_preserve_input_and_reserve_disjoint_tags(self):
        template = {"session": {"outputTags": [{"name": "Value"}]},
                    "generators": [{"tag": "Value", "kind": "constant", "value": 1}]}
        first = make_model(template, "first", True)
        second = make_model(template, "second", False)
        self.assertEqual(template["session"]["outputTags"][0]["name"], "Value")
        self.assertEqual(first["generators"][0]["tag"], "first.Value")
        self.assertEqual(second["generators"][0]["tag"], "second.Value")
        self.assertNotEqual(first["session"]["endUtc"], second["session"]["endUtc"])


if __name__ == "__main__":
    unittest.main()
