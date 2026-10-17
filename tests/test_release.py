"""Regression guard for metadata that previously crashed VRCFT's UI."""
import copy
import hashlib
import http.client
import io
import json
from pathlib import Path
import sys
import unittest
import tempfile
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from build import PAGE, download, validate_manifest


class ReleaseMetadataTests(unittest.TestCase):
    def setUp(self):
        self.manifest = json.loads((ROOT / 'module.json').read_text('utf-8'))
        self.version = ET.parse(ROOT / 'aGlassVRCFT.csproj').findtext('./PropertyGroup/Version')

    def test_current_manifest(self):
        validate_manifest(self.manifest, self.version)
        self.assertEqual(self.manifest['ModulePageUrl'], PAGE)

    def test_bad_page_urls(self):
        for url in (None, '', ' ', '/relative', 'https://', 'https://bad url', 'file:///C:/module', 'https://example.com'):
            with self.subTest(url=url):
                changed = dict(self.manifest, ModulePageUrl=url)
                with self.assertRaises(ValueError):
                    validate_manifest(changed, self.version)
        changed = copy.deepcopy(self.manifest)
        del changed['ModulePageUrl']
        with self.assertRaises(ValueError):
            validate_manifest(changed, self.version)

    def test_release_identity(self):
        for key, value in (('Version', '0.0.0'), ('DllFileName', 'wrong.dll'), ('ModuleId', '00000000-0000-0000-0000-000000000000')):
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_manifest(dict(self.manifest, **{key: value}), self.version)


class DependencyDownloadTests(unittest.TestCase):
    def test_interrupted_transfer_retries_without_partial_cache(self):
        content = b'verified payload'
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'package.zip'
            with patch('build.urllib.request.urlopen', side_effect=[http.client.IncompleteRead(b'partial'), io.BytesIO(content)]) as request, patch('build.time.sleep'):
                download('https://example.com/package.zip', target, hashlib.sha256(content).hexdigest())
            self.assertEqual(request.call_count, 2)
            self.assertEqual(target.read_bytes(), content)
            self.assertFalse(target.with_suffix('.zip.partial').exists())

    def test_failed_transfer_never_becomes_cache(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'package.zip'
            with patch('build.urllib.request.urlopen', side_effect=http.client.IncompleteRead(b'partial')) as request, patch('build.time.sleep'):
                with self.assertRaises(RuntimeError):
                    download('https://example.com/package.zip', target, '0' * 64)
            self.assertEqual(request.call_count, 3)
            self.assertFalse(target.exists())
            self.assertFalse(target.with_suffix('.zip.partial').exists())

    def test_corrupt_download_and_cache_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / 'package.zip'
            with patch('build.urllib.request.urlopen', return_value=io.BytesIO(b'wrong')):
                with self.assertRaises(RuntimeError):
                    download('https://example.com/package.zip', target, '0' * 64)
            self.assertFalse(target.exists())
            self.assertFalse(target.with_suffix('.zip.partial').exists())
            target.write_bytes(b'wrong')
            with patch('build.urllib.request.urlopen') as request, self.assertRaises(RuntimeError):
                download('https://example.com/package.zip', target, '0' * 64)
            request.assert_not_called()


if __name__ == '__main__':
    unittest.main()
