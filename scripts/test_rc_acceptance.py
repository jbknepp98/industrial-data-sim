from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import tempfile
import unittest
from prepare_rc_acceptance import prepare
from preview_rc_acceptance import check_all

ANCHOR = datetime(2026, 11, 21, 15, 0, tzinfo=timezone.utc)
NS = 'RC1-20261121T1500'


def series(*points):
    return [(ANCHOR + timedelta(minutes=minute), value, 192) for minute, value in points]


def healthy_data(target=6500):
    """Small hand-built preview data that satisfies every acceptance check."""
    cell = f'{NS}.ProcessCell.'
    line = f'{NS}.PackagingLine.'
    return {
        'processcell': {
            cell + 'SKU': series((0, 'SKU-A'), (10, 'SKU-B')),
            cell + 'State': series((0, 'Running'), (10, 'Running')),
            cell + 'A.Speed': series((0, 115), (10, 0)),
            cell + 'B.Speed': series((0, 0), (10, 90)),
            cell + 'Temperature': series((0, 75), (10, 75)),
            cell + 'TimeTotal': series((0, 0), (10, 15000)),
            cell + 'BatchTotal': series((0, 0), (10, 600)),
        },
        'packaging': {
            line + 'LineReady': series((0, True), (10, False), (20, True)),
            line + 'Filler.RateBpm': series((0, 400), (20, 401)),
            line + 'Filler.CountTotal': series((0, 0), (20, 3900)),
        },
        'batchreactor': {f'{NS}.BatchReactor.ProducedKg': series((0, 0), (60, 3000), (90, target))},
    }


class PrepareTests(unittest.TestCase):
    def test_workload_has_32_distinct_tags_and_fixed_identity(self):
        with tempfile.TemporaryDirectory() as folder:
            manifest = prepare(ANCHOR, timedelta(hours=2), 'local-development', 1, 6500, Path(folder) / 'w', NS)
            tags = [tag for session in manifest['sessions'] for tag in session['tags']]
            self.assertEqual(32, len(set(tags)))
            self.assertTrue(all(tag.startswith(NS + '.') for tag in tags))
            self.assertEqual(0, manifest['historianWrites'])
            reactor = json.loads((Path(folder) / 'w' / 'batchreactor.json').read_text())
            # Target model keeps the one-second clock with a short backfill.
            self.assertEqual(1000, reactor['manufacturing']['tickMs'])
            self.assertEqual('2026-11-21T09:00:00Z', reactor['session']['startUtc'])

    def test_existing_output_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(FileExistsError):
                prepare(ANCHOR, timedelta(hours=2), 'local-development', 1, 6500, Path(folder), NS)

    def test_invalid_profile_is_refused_before_writing(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / 'w'
            with self.assertRaisesRegex(ValueError, 'rc.profile'):
                prepare(ANCHOR, timedelta(hours=2), 'bad profile!', 1, 6500, target, NS)
            self.assertFalse(target.exists())


class PreviewCheckTests(unittest.TestCase):
    manifest = {'anchorUtc': '2026-11-21T15:00:00Z', 'batchReactorTarget': 6500}

    def test_healthy_data_passes(self):
        failures, _ = check_all(healthy_data(), self.manifest)
        self.assertEqual([], failures)

    def test_both_cells_running_is_reported(self):
        data = healthy_data()
        data['processcell'][f'{NS}.ProcessCell.B.Speed'] = series((0, 90), (10, 90))
        failures, _ = check_all(data, self.manifest)
        self.assertTrue(any(f.startswith('routing') for f in failures))

    def test_gate_leak_is_reported(self):
        data = healthy_data()
        data['packaging'][f'{NS}.PackagingLine.Filler.RateBpm'] = series((0, 400), (15, 400), (20, 401))
        failures, _ = check_all(data, self.manifest)
        self.assertTrue(any(f.startswith('gate') for f in failures))

    def test_missed_target_and_backward_time_are_reported(self):
        data = healthy_data(target=6400)
        data['processcell'][f'{NS}.ProcessCell.TimeTotal'] = series((10, 0), (5, 1))
        failures, _ = check_all(data, self.manifest)
        self.assertTrue(any(f.startswith('target') for f in failures))
        self.assertTrue(any(f.startswith('order') for f in failures))


class RunHelperTests(unittest.TestCase):
    def test_live_lag_ignores_gated_tags_and_requires_all_published(self):
        from run_rc_acceptance import live_lag_seconds
        now = datetime(2026, 11, 21, 15, 0, 30, tzinfo=timezone.utc)
        progress = [{'tag': 'X.PackagingLine.LineReady', 'publishedUtc': '2026-11-21T15:00:25Z'},
                    {'tag': 'X.PackagingLine.Filler.RateBpm', 'publishedUtc': '2026-11-21T14:00:00Z'},
                    {'tag': 'X.PackagingLine.Line.Phase', 'publishedUtc': '2026-11-21T15:00:20Z'}]
        self.assertEqual(10.0, live_lag_seconds(progress, now))
        progress.append({'tag': 'X.PackagingLine.Other', 'publishedUtc': None})
        self.assertIsNone(live_lag_seconds(progress, now))

    def test_minute_ceiling_and_percentile(self):
        from run_rc_acceptance import minute_ceiling, percentile
        self.assertEqual(datetime(2026, 1, 1, 0, 2, tzinfo=timezone.utc), minute_ceiling(datetime(2026, 1, 1, 0, 1, 1, tzinfo=timezone.utc)))
        self.assertEqual(datetime(2026, 1, 1, 0, 1, tzinfo=timezone.utc), minute_ceiling(datetime(2026, 1, 1, 0, 1, tzinfo=timezone.utc)))
        self.assertEqual(95, percentile(list(range(1, 101))))
        self.assertIsNone(percentile([]))

    def test_environment_must_target_test(self):
        from run_rc_acceptance import load_environment
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'env'
            path.write_text('TIMEBASE_DATASET=PencilFactory\n')
            with self.assertRaisesRegex(ValueError, 'rc.dataset_guard'):
                load_environment(path)
