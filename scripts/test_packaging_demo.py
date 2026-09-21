"""Independent checks of the agent-authored demonstration schedule."""
from datetime import datetime, timezone
import unittest
import tempfile
from pathlib import Path
from local_production import environment
from prepare_packaging_demo import build_model, HOUR, MINUTE


class PackagingDemoTests(unittest.TestCase):
    def test_local_environment_is_literal_and_ignores_unapproved_keys(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "local.env"
            path.write_text("TIMEBASE_CLIENT_SECRET='$(do-not-run)'\nUNAPPROVED_DEMO_KEY=ignored\n")
            loaded = environment(path)
            self.assertEqual('$(do-not-run)', loaded['TIMEBASE_CLIENT_SECRET'])
            self.assertNotIn('UNAPPROVED_DEMO_KEY', loaded)
            path.write_text('x' * 65537)
            with self.assertRaisesRegex(ValueError, 'production.environment_limit'):
                environment(path)

    def test_ten_day_schedule_has_disjoint_routes_and_matches_output_types(self):
        model = build_model(datetime(2026, 9, 1, tzinfo=timezone.utc), 'Example', 'example-packaging')
        self.assertEqual('2026-09-11T00:00:00Z', model['session']['endUtc'])
        self.assertEqual(9, len(model['session']['outputTags']))
        by_name = {g['tag']: g for g in model['generators']}
        self.assertEqual(set(by_name), {t['name'] for t in model['session']['outputTags']})
        sku = by_name['Example.Production.SKU.String']['steps']
        readiness = by_name['Example.Mixer.Ready.Boolean']['steps']
        self.assertEqual(240 * HOUR, sum(s['durationMs'] for s in sku))
        self.assertEqual(240 * HOUR, sum(s['durationMs'] for s in readiness))
        for minute in range(10 * 24 * 60):
            # Independent process expectation at every grid timestamp.
            expected_sku = 'SKU-A' if (minute // 360) % 2 == 0 else 'SKU-B'
            ready = 15 <= minute % 360 < 105 or minute % 360 >= 135
            routes = [g for g in by_name.values() if g['kind'] == 'skuRoute']
            active = [g for g in routes if g['sku'] == expected_sku and ready]
            self.assertEqual(1 if ready else 0, len(active))
        for cell in ('A', 'B'):
            pause = by_name['Example.Packaging' + cell + '.Speed.PauseClock']
            continuing = by_name['Example.Packaging' + cell + '.Speed.ContinueClock']
            self.assertEqual(pause['pattern'], continuing['pattern'])
            self.assertEqual('pauseAndSuppress', pause['whenFalse'])
            self.assertEqual('continueAndSuppress', continuing['whenFalse'])
            self.assertGreaterEqual(len(pause['pattern']['steps']), 80)


if __name__ == '__main__':
    unittest.main()
