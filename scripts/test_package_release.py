import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile
from package_release import digest, build
from verify_package import verify


class PackageIntegrityTests(unittest.TestCase):
    def test_hash_streams_more_than_one_chunk(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'data'
            data = b'x' * (1024 * 1024 + 17)
            path.write_bytes(data)
            self.assertEqual(hashlib.sha256(data).hexdigest(), digest(path))

    def test_bad_version_cannot_create_output(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / 'candidate'
            with self.assertRaises(ValueError):
                build('unused', 'osx-arm64', '1.0.0', target)
            self.assertFalse(target.exists())

    def test_corrupt_archive_is_refused_before_execution(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'candidate.zip'
            path.write_bytes(b'changed')
            path.with_name(path.name + '.sha256').write_text('0' * 64)
            with self.assertRaisesRegex(ValueError, 'checksum'):
                verify(path)

    def test_escape_duplicate_and_extra_entries_are_refused(self):
        for name in ('../escape', '/escape', 'C:/escape', 'extra'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as folder:
                path = Path(folder) / 'candidate.zip'
                with zipfile.ZipFile(path, 'w') as output:
                    output.writestr('manifest.json', json.dumps({'files': {}, 'dirty': False}))
                    output.writestr(name, 'private')
                path.with_name(path.name + '.sha256').write_text(digest(path))
                with self.assertRaises(ValueError):
                    verify(path)
