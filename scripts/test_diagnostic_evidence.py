"""Regression coverage for launch blind spots and failure-evidence loss."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

from diagnostic_evidence import DiagnosticEvidence
from measure_host_capacity import measure
from profile_control_macos import InventoryProfiler


class DiagnosticEvidenceTests(unittest.TestCase):
    def test_timed_out_client_is_killed_and_record_survives(self):
        with tempfile.TemporaryDirectory() as folder:
            evidence = DiagnosticEvidence(Path(folder))
            collector = InventoryProfiler(evidence)
            client = Mock(pid=123, returncode=None)
            client.__enter__ = Mock(return_value=client)
            client.__exit__ = Mock(return_value=False)
            client.poll.return_value = None
            client.communicate.return_value = ('', '')
            # A launch longer than the entire deadline must not receive a fresh
            # full timeout. Optional sampler failure must not prevent cleanup.
            with patch('profile_control_macos.subprocess.Popen', side_effect=[client, OSError('private')]), \
                 patch('profile_control_macos.time.monotonic', side_effect=[0, 2, 2, 2.1]):
                with self.assertRaises(subprocess.TimeoutExpired):
                    collector.run(['dotnet', 'synthetic.dll', 'live', 'list', 'unused'], 1)
            client.kill.assert_called_once()
            record = evidence.report['inventoryProcesses'][0]
            self.assertEqual(record['outcome'], 'failed')
            self.assertEqual(record['captureStatus'], 'sampling-failed')

    def test_launch_delay_is_reported_and_charged_to_deadline(self):
        with tempfile.TemporaryDirectory() as folder:
            evidence = DiagnosticEvidence(Path(folder))
            collector = InventoryProfiler(evidence)
            client = Mock(pid=123, returncode=0)
            client.__enter__ = Mock(return_value=client)
            client.__exit__ = Mock(return_value=False)
            client.poll.return_value = 0  # Already exited; cannot capture old launch frames.
            client.communicate.return_value = ('{}', '')
            with patch('profile_control_macos.subprocess.Popen', return_value=client), \
                 patch('profile_control_macos.time.monotonic', side_effect=[0, .65, .65, .66]):
                collector.run(['dotnet', 'synthetic.dll', 'live', 'list', 'unused'], 1)
            record = evidence.report['inventoryProcesses'][0]
            self.assertEqual(record['launchMs'], 650)
            self.assertTrue(record['launchCaptureUnavailable'])
            self.assertEqual(record['captureStatus'], 'client-exited')
            self.assertAlmostEqual(client.communicate.call_args.kwargs['timeout'], .35)
            self.assertEqual(record['outcome'], 'complete')

    def test_timeout_keeps_report_and_selected_logs_before_temp_cleanup(self):
        with tempfile.TemporaryDirectory() as output:
            evidence = DiagnosticEvidence(Path(output))
            def failing_measure(*args):
                evidence.report['stage'] = 'live.list'
                evidence.operation('live.list', 40000, 'failed')
                with tempfile.TemporaryDirectory() as state, evidence.retain_logs(state):
                    logs = Path(state) / 'logs' / 'state.db'
                    logs.mkdir(parents=True)
                    (logs / 'runtime.jsonl').write_text(json.dumps({
                        'eventCode': 'host.slow_operation', 'timestampUtc': '2026-09-20T12:00:00Z',
                        'message': 'Host control execution took 1200 ms.',
                        'payload': 'PRIVATE_INPUT', 'action': 'PRIVATE_INPUT'}) + '\n')
                    raise subprocess.TimeoutExpired('PRIVATE_INPUT', 40, output='PRIVATE_INPUT')
            with patch('measure_host_capacity._measure', side_effect=failing_measure):
                with self.assertRaises(subprocess.TimeoutExpired):
                    measure('dotnet', 0, 5, evidence=evidence)
            text = (Path(output) / 'report.json').read_text()
            report = json.loads(text)
            self.assertEqual(report['status'], 'failed')
            self.assertEqual(report['failure']['code'], 'capacity.command_timeout')
            self.assertEqual(report['stage'], 'live.list')
            self.assertEqual(report['operations'][0]['elapsedMs'], 40000)
            self.assertEqual(report['hostEvents'][0]['elapsedMs'], 1200)
            self.assertEqual(report['logFilesRead'], 1)
            self.assertNotIn('PRIVATE_INPUT', text)

    def test_evidence_failure_does_not_replace_original_failure(self):
        evidence = DiagnosticEvidence(Path('/unused'))
        with patch('measure_host_capacity._measure', side_effect=KeyboardInterrupt), \
             patch.object(Path, 'mkdir', side_effect=PermissionError('PRIVATE_INPUT')):
            with self.assertRaises(KeyboardInterrupt):
                measure('dotnet', 0, 5, evidence=evidence)
        self.assertEqual(evidence.report['failure']['code'], 'capacity.interrupted')

    def test_records_are_bounded_with_explicit_omission_counts(self):
        evidence = DiagnosticEvidence()
        for index in range(205):
            evidence.operation('live.list', index, 'returned')
        self.assertEqual(len(evidence.report['operations']), 200)
        self.assertEqual(evidence.report['operationsOmitted'], 5)
        self.assertEqual(evidence.report['operations'][0]['elapsedMs'], 5)

    def test_profile_failure_still_finalizes_sampler_manifest(self):
        # Exercise the entry point rather than only the generic measurement wrapper.
        import profile_control_macos as module
        with tempfile.TemporaryDirectory() as output:
            evidence = DiagnosticEvidence(Path(output))
            collector = Mock()
            def finish():
                evidence.report['profiles'] = [{'file': 'client-1.txt', 'written': False, 'exitCode': 1}]
            collector.finish.side_effect = finish
            with patch.object(module.platform, 'system', return_value='Darwin'), \
                 patch.object(module.Path, 'exists', return_value=True), \
                 patch.object(module.Path, 'mkdir'), \
                 patch.object(module, 'DiagnosticEvidence', return_value=evidence), \
                 patch.object(module, 'InventoryProfiler', return_value=collector), \
                 patch.object(module, 'measure', side_effect=subprocess.TimeoutExpired('PRIVATE_INPUT', 40)):
                with self.assertRaises(subprocess.TimeoutExpired):
                    module.profile('dotnet')
            report = json.loads((Path(output) / 'report.json').read_text())
            self.assertEqual(report['status'], 'failed')
            self.assertEqual(report['profiles'][0]['exitCode'], 1)
            collector.finish.assert_called_once()


if __name__ == '__main__':
    unittest.main()
