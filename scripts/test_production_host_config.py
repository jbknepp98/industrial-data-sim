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


class HostSupervisorTests(unittest.TestCase):
    def test_restart_reopens_same_database_without_readmission(self):
        from unittest.mock import MagicMock, patch
        from run_production_host import run
        first, second = MagicMock(), MagicMock()
        first.wait.return_value = 1
        second.wait.return_value = 0
        with patch('run_production_host.environment', return_value={}), \
             patch('run_production_host.signal.signal'), \
             patch('run_production_host.subprocess.Popen', side_effect=[first, second]) as launch, \
             patch('run_production_host.threading.Event') as event:
            event.return_value.is_set.return_value = False
            event.return_value.wait.return_value = False
            self.assertEqual(0, run({'database': 'retained.db', 'environmentFile': '.env', 'dotnet': 'dotnet', 'maximumRestarts': 1}))
            self.assertEqual(2, launch.call_count)
            self.assertEqual(launch.call_args_list[0].args, launch.call_args_list[1].args)
            self.assertEqual(['production-host', 'run', 'retained.db'], launch.call_args.args[0][-3:])
            event.return_value.wait.assert_called_once_with(1)

    def test_graceful_signal_does_not_restart_child(self):
        from unittest.mock import MagicMock, patch
        import signal
        from run_production_host import run
        handlers = {}
        child = MagicMock()
        child.poll.return_value = None
        def finish():
            handlers[signal.SIGTERM]()
            return 130
        child.wait.side_effect = finish
        with patch('run_production_host.environment', return_value={}), \
             patch('run_production_host.signal.signal', side_effect=lambda kind, handler: handlers.update({kind: handler})), \
             patch('run_production_host.subprocess.Popen', return_value=child) as launch:
            self.assertEqual(130, run({'database': 'retained.db', 'environmentFile': '.env', 'dotnet': 'dotnet', 'maximumRestarts': 3}))
            launch.assert_called_once()
            child.send_signal.assert_called_once()
