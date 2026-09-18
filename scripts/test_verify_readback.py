"""Synthetic checks for the saved-evidence verifier; no credentials required."""
import copy
import unittest
from datetime import datetime, timezone
from verify_readback import verify


class ReadbackTests(unittest.TestCase):
    def setUp(self):
        self.start = datetime(2026, 9, 1, tzinfo=timezone.utc)
        self.end = datetime(2026, 9, 2, tzinfo=timezone.utc)
        points = [
            {"t": f"2026-09-01T00:00:0{i}Z", "v": value, "q": 192}
            for i, value in enumerate([False, False, True, True])
        ]
        self.preview = {"data": {"Example.Trigger": points}}
        self.response = {"tl": [{"t": {"n": "Example.Trigger", "t": "System.Boolean"},
                                  "d": copy.deepcopy([points[0], points[2]])}]}

    def check(self):
        return verify(self.preview, self.response, self.start, self.end)

    def test_omitted_repeats_are_allowed(self):
        self.assertEqual(2, self.check())

    def test_boolean_numeric_representation(self):
        for point in self.response["tl"][0]["d"]:
            point["v"] = int(point["v"])
        self.assertEqual(2, self.check())

    def test_missing_transition_fails(self):
        self.response["tl"][0]["d"].pop()
        with self.assertRaisesRegex(ValueError, "required transition"):
            self.check()

    def test_quality_mismatch_fails(self):
        self.response["tl"][0]["d"][0]["q"] = 193
        with self.assertRaisesRegex(ValueError, "quality differs"):
            self.check()

    def test_duplicate_timestamp_fails(self):
        self.response["tl"][0]["d"].append(self.response["tl"][0]["d"][-1])
        with self.assertRaisesRegex(ValueError, "duplicate"):
            self.check()

    def test_missing_tag_fails(self):
        self.response["tl"] = []
        with self.assertRaisesRegex(ValueError, "missing a requested tag"):
            self.check()

    def test_value_mismatch_fails(self):
        self.response["tl"][0]["d"][0]["v"] = True
        with self.assertRaisesRegex(ValueError, "value"):
            self.check()

    def test_storage_type_mismatch_fails(self):
        self.response["tl"][0]["t"]["t"] = "System.Double"
        with self.assertRaisesRegex(ValueError, "Stored type"):
            self.check()


if __name__ == "__main__":
    unittest.main()
