import unittest

from generateProcedures import manifest_entry
from procs_setup import PROCS as SETUP_PROCS


class ManifestTests(unittest.TestCase):
    def test_fresh_supervisor_matches_backfilled_waiver_permission(self):
        installer=next(p for p in SETUP_PROCS if p['name']=='uspInstallRootTenant')
        self.assertIn('workflow.waive',installer['body'])
        self.assertIn('@seedThresholds',installer['params'])
        self.assertNotIn("@effectiveFrom DATE = '2026-01-01'",installer['params'])

    def test_inline_and_multiline_parameters_preserve_types_and_defaults(self):
        declarations = "@id UNIQUEIDENTIFIER, @amount DECIMAL (18, 4) = 0, @text NVARCHAR (100) = N'a, @notAParameter ''quoted''', @result INT OUTPUT"
        expected = [
            {"name": "id", "type": "UNIQUEIDENTIFIER", "hasDefault": False, "isOutput": False},
            {"name": "amount", "type": "DECIMAL (18, 4)", "hasDefault": True, "isOutput": False},
            {"name": "text", "type": "NVARCHAR (100)", "hasDefault": True, "isOutput": False},
            {"name": "result", "type": "INT", "hasDefault": False, "isOutput": True},
        ]
        for value in (declarations, declarations.replace(", @amount", "\n, @amount").replace(", @result", ",\n @result")):
            with self.subTest(value=value):
                entry = manifest_entry({"schema": "dbo", "name": "test", "kind": "read", "params": value})
                self.assertEqual(expected, entry["params"])

    def test_no_parameters(self):
        self.assertEqual([], manifest_entry({"schema": "dbo", "name": "test", "kind": "read"})["params"])


if __name__ == "__main__":
    unittest.main()
