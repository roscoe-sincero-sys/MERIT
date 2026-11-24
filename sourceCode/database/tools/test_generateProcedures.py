import importlib
import re
import unittest
from pathlib import Path

import generateProcedures as gp
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

    def test_generator_matches_shipped_security_and_deidentification_procedures(self):
        loaded = {}
        for mod_name in gp.MODULES:
            for p in importlib.import_module(mod_name).PROCS:
                loaded[f"{p['schema']}.{p['name']}"] = p

        tracked = [
            "auth.uspConsumeTotpStep",
            "auth.uspCreateSessionScope",
            "auth.uspRecordSignInResult",
            "auth.uspRevokeSessionScope",
            "auth.uspSaveMfaEnrollment",
            "auth.uspUseRecoveryCode",
            "dbo.uspGetLabResult",
            "dbo.uspListLabResults",
            "dbo.uspGetExposureCase",
            "dbo.uspListExposureCases",
            "dbo.uspGetCaseMap",
            "dbo.uspListClusters",
            "dbo.uspGetCluster",
        ]
        for proc_name in tracked:
            with self.subTest(proc=proc_name):
                self.assertIn(proc_name, loaded, f"{proc_name} missing from generator definitions")
                sql_path = gp.OUT / f"{proc_name}.sql"
                self.assertTrue(sql_path.exists(), f"{sql_path} missing")
                sql_text = sql_path.read_text(encoding="utf-8")
                m = re.search(r"CREATE OR ALTER PROCEDURE\s+\S+(.*?)\bAS\b", sql_text, re.S | re.I)
                self.assertIsNotNone(m)
                sql_params = [name.lower() for name in re.findall(r"@(\w+)\s+[^\r\n,]+", m.group(1))]
                gen_params = [p["name"].lower() for p in manifest_entry(loaded[proc_name])["params"]]
                self.assertEqual(sql_params, gen_params, f"Parameter mismatch for {proc_name}")
                # Check key behavioral invariants stay synced in generator bodies too
                if proc_name == "auth.uspCreateSessionScope":
                    self.assertIn("UPDLOCK, HOLDLOCK", loaded[proc_name]["body"])
                    self.assertIn("hasIdentified", loaded[proc_name]["body"])
                elif proc_name == "auth.uspSaveMfaEnrollment":
                    self.assertIn("SELECT @newSecurityStamp AS securityStamp;", loaded[proc_name]["body"])
                elif proc_name == "dbo.uspGetCluster":
                    self.assertIn("N'Cluster alert'", loaded[proc_name]["body"])
                    self.assertIn("@canViewTasks = 1", loaded[proc_name]["body"])


if __name__ == "__main__":
    unittest.main()
