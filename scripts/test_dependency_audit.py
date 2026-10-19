import unittest
from check_dependency_audit import validate


class DependencyAuditTests(unittest.TestCase):
    def test_no_findings(self):
        validate({'version': 1, 'sources': ['source'], 'projects': [{'path': 'private'}]})

    def test_findings_feed_errors_and_missing_evidence_fail(self):
        for report in ({}, {'version': 1, 'projects': []},
                       {'version': 1, 'sources': ['source'], 'projects': [{'frameworks': [{'topLevelPackages': [{'vulnerabilities': [{'severity': 'High'}]}]}]}]},
                       {'version': 1, 'sources': ['source'], 'projects': [{}], 'problems': ['feed unavailable']}):
            with self.assertRaises(ValueError):
                validate(report)
