#!/usr/bin/env python3
"""Regression checks for the release inventory; uses synthetic files only."""
import base64
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import sys

sys.dont_write_bytecode = True

spec = importlib.util.spec_from_file_location("generate_sbom", Path(__file__).with_name("generate-sbom.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class SbomTests(unittest.TestCase):
    def test_nuget_hash_is_hex_and_project_reference_is_not_an_unknown_component(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            obj = root / 'source/Walis.Api/obj'
            obj.mkdir(parents=True)
            digest = bytes(range(64))
            (obj / 'project.assets.json').write_text(json.dumps({
                'libraries': {'Example/1.0.0': {'type': 'package', 'sha512': base64.b64encode(digest).decode()},
                              'Walis.Core/1.0.0': {'type': 'project'}},
                'targets': {'net10.0': {'Example/1.0.0': {'dependencies': {'Walis.Core': '1.0.0'}}}}
            }))
            sbom = module.Sbom()
            module.add_nuget(sbom, root)
            component = sbom.components['pkg:nuget/Example@1.0.0']
            self.assertEqual(digest.hex(), component['hashes'][0]['content'])
            self.assertTrue(all(ref in sbom.components for ref in sbom.deps))

    def test_runtime_inventory_comes_only_from_explicit_current_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            old = root / 'dist/old'
            stage = root / 'current'
            old.mkdir(parents=True)
            stage.mkdir()
            def runtime(path, version):
                path.write_text(json.dumps({'runtimeOptions': {'includedFrameworks': [
                    {'name': 'Microsoft.NETCore.App', 'version': version}]}}))
            runtime(old / 'Walis.Api.runtimeconfig.json', '10.0.10')
            runtime(stage / 'Walis.Api.runtimeconfig.json', '10.0.13')
            runtime(stage / 'walis-runner.runtimeconfig.json', '10.0.14')
            sbom = module.Sbom()
            module.add_runtime(sbom, stage)
            self.assertEqual({'10.0.13', '10.0.14'}, {v['version'] for v in sbom.components.values()})
            self.assertEqual([], module.add_runtime(module.Sbom()))

    def test_missing_or_framework_dependent_runtime_fails_release_inventory(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                module.add_runtime(module.Sbom(), directory)
            (Path(directory) / 'Walis.Api.runtimeconfig.json').write_text('{"runtimeOptions":{}}')
            with self.assertRaises(ValueError):
                module.add_runtime(module.Sbom(), directory)

if __name__ == '__main__':
    unittest.main()
