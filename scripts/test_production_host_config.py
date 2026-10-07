import json
from pathlib import Path
import tempfile
import unittest
from run_production_host import load_config


class HostConfigTests(unittest.TestCase):
    def test_paths_resolve_against_config_and_restarts_are_explicit(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            (folder / 'state.db').touch()
            config = folder / 'host.json'
            config.write_text(json.dumps({'database': 'state.db', 'environmentFile': '.env', 'dotnet': 'dotnet'}))
            result = load_config(config)
            self.assertEqual(0, result['maximumRestarts'])
            self.assertEqual(str((folder / '.env').resolve()), result['environmentFile'])

    def test_unknown_fields_and_unbounded_restarts_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            (folder / 'state.db').touch()
            for extra in [{'secret': 'PRIVATE'}, {'maximumRestarts': True}, {'maximumRestarts': 4}]:
                config = folder / 'host.json'
                config.write_text(json.dumps({'database': 'state.db', 'environmentFile': '.env', 'dotnet': 'dotnet', **extra}))
                with self.assertRaises(ValueError) as raised:
                    load_config(config)
                self.assertNotIn('PRIVATE', str(raised.exception))
