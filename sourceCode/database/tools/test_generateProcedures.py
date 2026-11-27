import importlib
import re
import unittest

import generateProcedures as gp
import generateTables as gt
from generateProcedures import manifest_entry
from procs_setup import PROCS as SETUP_PROCS

BASE_COMMIT_HANDWRITTEN_PROCS = gp.HANDWRITTEN


def _normalize_sql(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    lines = []
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("--"):
            continue
        lines.append(re.sub(r"\s+", " ", stripped))
    return "\n".join(lines)


class ManifestTests(unittest.TestCase):
    def test_fresh_supervisor_matches_backfilled_waiver_permission(self):
        installer = next(p for p in SETUP_PROCS if p['name'] == 'uspInstallRootTenant')
        self.assertIn('workflow.waive', installer['body'])
        self.assertIn('@seedThresholds', installer['params'])
        self.assertNotIn("@effectiveFrom DATE = '2026-01-01'", installer['params'])

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

    def test_generator_matches_all_shipped_procedures(self):
        loaded = {}
        for mod_name in gp.MODULES:
            for p in importlib.import_module(mod_name).PROCS:
                loaded[f"{p['schema']}.{p['name']}"] = p

        shipped = {path.stem for path in gp.OUT.glob("*.sql")}
        self.assertEqual(
            set(loaded.keys()),
            shipped - BASE_COMMIT_HANDWRITTEN_PROCS,
            "Generator procedure set diverged from 400_procedures/*.sql",
        )

        drifted = []
        for proc_name, proc_def in sorted(loaded.items()):
            rendered = gp.write_proc(proc_def) if proc_def["kind"] == "write" else gp.read_proc(proc_def)
            disk = (gp.OUT / f"{proc_name}.sql").read_text(encoding="utf-8-sig")
            if _normalize_sql(rendered) != _normalize_sql(disk):
                drifted.append(proc_name)

        self.assertEqual(
            [],
            sorted(drifted),
            f"Shipped SQL procedures drifted from generateProcedures.py definitions: {sorted(drifted)}",
        )

    def test_table_generator_matches_all_shipped_tables(self):
        self.assertTrue(len(gt.TABLES) > 0)
        for i, table_def in enumerate(gt.TABLES, start=1):
            full_name = f"{table_def['schema']}.{table_def['name']}"
            with self.subTest(table=full_name):
                shipped_path = gt.ROOT / "100_tables" / f"{100 + i:03d}_{full_name}.sql"
                self.assertTrue(shipped_path.exists(), f"Missing {shipped_path}")
                rendered = gt.table_script(table_def)
                disk = shipped_path.read_text(encoding="utf-8-sig")
                self.assertEqual(
                    _normalize_sql(rendered),
                    _normalize_sql(disk),
                    f"Shipped table script {shipped_path.name} drifted from tableDefinitions.py / generateTables.py",
                )

    def test_cleanup_preserves_handwritten_procedures(self):
        loaded_names = set()
        for mod_name in gp.MODULES:
            for p in importlib.import_module(mod_name).PROCS:
                loaded_names.add(f"{p['schema']}.{p['name']}")
        stale = {p.stem for p in gp.stale_procedure_files(gp.OUT, loaded_names)}
        self.assertEqual(set(), stale & gp.HANDWRITTEN)
        for name in gp.HANDWRITTEN:
            self.assertTrue((gp.OUT / f"{name}.sql").exists(), f"Handwritten procedure {name}.sql missing")

    def test_key_parameters_follow_allowlisted_diagnostic_schema(self):
        # #338: Ensure no generated procedure logs disallowed sensitive/PHI parameters in @KeyParameters.
        violations = []
        for mod_name in gp.MODULES:
            for p in importlib.import_module(mod_name).PROCS:
                key_expr = p.get("key") or ""
                for disallowed in gp.DISALLOWED_DIAGNOSTIC_PARAMS:
                    if re.search(rf"{re.escape(disallowed)}\b", key_expr, flags=re.IGNORECASE):
                        violations.append(f"{p['schema']}.{p['name']} references {disallowed}")
        self.assertEqual([], violations, f"Disallowed diagnostic parameters in @KeyParameters: {violations}")


if __name__ == "__main__":
    unittest.main()
