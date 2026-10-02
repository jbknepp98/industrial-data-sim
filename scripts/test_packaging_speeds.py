"""Check additive controls preserve source timing without modifying its model."""
import copy
from datetime import datetime, timezone
import unittest
from prepare_packaging_demo import build_model as packaging
from prepare_packaging_speeds import build_model


class PackagingSpeedTests(unittest.TestCase):
    def test_controls_follow_readiness_and_sku_at_every_minute(self):
        source = packaging(datetime(2026, 10, 1, tzinfo=timezone.utc), 'Demo', 'original', days=10)
        before = copy.deepcopy(source)
        model = build_model(source, 'Demo', 'speeds')
        self.assertEqual(source, before)
        self.assertTrue(set(t['name'] for t in source['session']['outputTags']).isdisjoint(
            t['name'] for t in model['session']['outputTags']))
        for cell in ('A', 'B'):
            control = next(g for g in model['generators'] if g['tag'] == 'Demo.Packaging' + cell + '.SpeedControl.Boolean')
            actual = [step['value'] for step in control['steps'] for _ in range(step['durationMs'] // 60000)]
            expected = [((minute // 360) % 2 == (0 if cell == 'A' else 1)) and
                        (15 <= minute % 360 < 105 or minute % 360 >= 135)
                        for minute in range(10 * 1440)]
            self.assertEqual(actual, expected)

    def test_different_schedule_lengths_explain_failure(self):
        source = packaging(datetime(2026, 10, 1, tzinfo=timezone.utc), 'Demo', 'original', days=1)
        source['generators'][1]['steps'][-1]['durationMs'] -= 60000
        with self.assertRaisesRegex(ValueError, 'speed.schedule_length'):
            build_model(source, 'Demo', 'speeds')
