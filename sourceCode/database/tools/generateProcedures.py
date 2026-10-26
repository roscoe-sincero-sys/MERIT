"""
Generates MERIT stored procedure scripts from the definitions in procs_*.py.

    python sourceCode/database/tools/generateProcedures.py

Each definition supplies only what the ponytail-sql-objects procedure templates say changes from one procedure to the
next -- the parameter list, @KeyParameters, the work between the two ===== banners, and @Comments -- plus the header
text and the roles granted EXECUTE. Everything else is the template boilerplate, emitted verbatim:

    kind "write"  templates/procedure.sql           full rule 8 instrumentation (start row, transaction, completion
                                                    UPDATE, row re-created in the CATCH, bare ;THROW;)
    kind "read"   templates/procedure-readonly.sql  error-only instrumentation (CATCH records with @ExecutionLogId NULL)

A definition may name a permission; the procedure then refuses with 51403 unless auth.udfHasPermission says the
session holds it. Rule 4 of row-level security in the specification: stored procedures check permissions too.

MERIT error numbers (THROW, never RAISERROR + RETURN):
    50000  argument validation          51403  permission denied
    51404  record not found / not visible  51409  concurrency conflict (row version changed)
    51422  business rule violated (message is shown to the user)

Output: sourceCode/database/scripts/400_procedures/<schema>.<name>.sql
"""
from __future__ import annotations

import importlib
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
OUT = HERE.parent / "scripts" / "400_procedures"
AUTHOR = "MERIT build"
CREATE_DATE = "2026-10-24"
MODULES = ["procs_auth", "procs_config", "procs_person", "procs_places", "procs_results", "procs_cases",
           "procs_work", "procs_intake", "procs_extracts", "procs_setup", "procs_review"]

PROLOGUE = """-- SET XACT_ABORT ON sits ABOVE the header block deliberately: sys.sql_modules stores only the batch containing
-- CREATE, so the header must be the last thing before CREATE with no GO between them.
SET XACT_ABORT ON;
-- sqlcmd defaults QUOTED_IDENTIFIER OFF and the setting is baked in at CREATE time; a module carrying it OFF cannot
-- run DML against a table with a filtered index (error 1934), which is every table here.
SET QUOTED_IDENTIFIER ON;
GO
"""


def indent(text: str, n: int) -> str:
    pad = " " * n
    return "\n".join((pad + line) if line.strip() else "" for line in text.strip("\n").splitlines())


def header(p: dict) -> str:
    deps = p.get("deps", "See the procedure body.")
    notes = p.get("notes", "")
    perm = p.get("permission")
    perm_note = (f"Requires permission {perm} (auth.udfHasPermission); refuses with 51403 otherwise.\n\n" if perm else "")
    instr = ("Rule 8 full instrumentation, generated from templates/procedure.sql by tools/generateProcedures.py."
             if p["kind"] == "write" else
             "Rule 8 error-only instrumentation (a procedure that only reads), generated from "
             "templates/procedure-readonly.sql by tools/generateProcedures.py.")
    return f"""/***********************************************************************************************************************
ObjectName:   {p['schema']}.{p['name']}
Author:       {AUTHOR}
CreateDate:   {CREATE_DATE}
========================================================================================================================
Description:

{p['desc'].strip()}

========================================================================================================================
Requirements and Key Dependencies:

{deps.strip()}

logs.uspStartExecutionLogging and logs.uspRecordExecutionError (scripts/000_infrastructure/030_logExecutionLogging.sql).

========================================================================================================================
Notes:

{perm_note}{notes.strip() + chr(10) + chr(10) if notes.strip() else ''}{instr} Row-level security applies to every table read
or written here: SESSION_CONTEXT (N'scopeKey') decides what the caller can see.

========================================================================================================================
Example Usage and Performance:

{p.get('example', 'exec ' + p['schema'] + '.' + p['name'] + ' ...').strip()}

========================================================================================================================
Modification History:

Date:		{CREATE_DATE}
Author:		{AUTHOR}
Ticket:		{p.get('ticket', 'T040-T052')}
Description:	Initial version.

-----------------------------------------------------------------------------------------------------------------------

***********************************************************************************************************************/"""


def execute_as(p: dict) -> str:
    """Core installation procedures run as their owner so they can seed a data root's shared rows that row-level
    security would hide from the caller. They are granted to no role: only a checked wrapper can reach them, through
    ownership chaining, and the wrapper does every authorization check first."""
    return "\nWITH EXECUTE AS OWNER" if p.get("execute_as_owner") else ""


def permission_check(p: dict, pad: int) -> str:
    perm = p.get("permission")
    if not perm:
        return ""
    perms = perm if isinstance(perm, list) else [perm]
    cond = " AND ".join(f"auth.udfHasPermission (N'{x}') = 0" for x in perms)
    return indent(f"""IF {cond}
BEGIN
    ;THROW 51403, N'You do not have permission to do this ({", ".join(perms)}).', 1;
END;
""", pad) + "\n\n"


def write_proc(p: dict) -> str:
    full = f"{p['schema']}.{p['name']}"
    lit = f"[{p['schema']}].[{p['name']}]"
    params = indent(p.get("params", ""), 6) if p.get("params") else ""
    validation = indent(p["validate"], 8) + "\n\n" if p.get("validate") else ""
    return f"""{PROLOGUE}
{header(p)}
CREATE OR ALTER PROCEDURE {full}
{params}{execute_as(p)}
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'{lit}')
          , @StartTimeUtc   DATETIME2 (3)  = SYSUTCDATETIME ()
          , @EndTimeUtc     DATETIME2 (3)  = NULL
          , @ExecutionId    BIGINT         = NULL
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @Comments       NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never a credential, a payload, or patient data.
    SET @KeyParameters = {p.get('key', "NULL")};

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

{permission_check(p, 8)}{validation}        BEGIN TRANSACTION;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

{indent(p['body'], 8)}

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        -- Completion. Deliberately after the COMMIT.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc          = @EndTimeUtc
                 , ElapsedMilliseconds = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                    , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful          = 1
                 , Comments            = @Comments
                 , modifiedBy          = ORIGINAL_LOGIN ()
                 , modifiedDateUtc     = @EndTimeUtc
             WHERE ExecutionLogId = @ExecutionId;
        END;

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        IF XACT_STATE () <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        BEGIN TRY
            IF @ExecutionId IS NULL
               OR NOT EXISTS (SELECT 1
                                FROM logs.ExecutionLog
                               WHERE ExecutionLogId = @ExecutionId)
            BEGIN
                EXEC logs.uspStartExecutionLogging
                      @ProcedureName          = @ProcName
                    , @KeyParameters          = @KeyParameters
                    , @StartDateUtc           = @StartTimeUtc
                    , @ReCreatedAfterRollback = 1
                    , @ExecutionLogId         = @ExecutionId OUTPUT;
            END;
        END TRY
        BEGIN CATCH
            SET @ExecutionId = NULL;
        END CATCH;

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = @ExecutionId
            , @ErrorMessage    = @ErrorMsg
            , @ErrorProcedure  = @ErrorProc
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = @DynamicSql
            , @ContextMessage  = @ContextMessage;

        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO
{footer(p)}"""


def read_proc(p: dict) -> str:
    full = f"{p['schema']}.{p['name']}"
    lit = f"[{p['schema']}].[{p['name']}]"
    params = indent(p.get("params", ""), 6) if p.get("params") else ""
    validation = indent(p["validate"], 8) + "\n\n" if p.get("validate") else ""
    return f"""{PROLOGUE}
{header(p)}
CREATE OR ALTER PROCEDURE {full}
{params}{execute_as(p)}
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- Rule 8 error-only instrumentation. Boilerplate: copy verbatim.
    -- =============================================================================================
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'{lit}')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- Identifiers and counts ONLY. Never free-text search terms or patient data.
    SET @KeyParameters = {p.get('key', "NULL")};

    SET @ContextMessage = N'Error-only instrumented read: no start row is opened, so @ExecutionLogId is NULL by design.';

    BEGIN TRY

{permission_check(p, 8)}{validation}        -- =========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==
        -- =========================================================================================

{indent(p['body'], 8)}

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

    END TRY
    BEGIN CATCH

        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = NULL
            , @ErrorMessage    = @ErrorMsg
            , @ErrorProcedure  = @ErrorProc
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = @DynamicSql
            , @ContextMessage  = @ContextMessage;

        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO
{footer(p)}"""


def footer(p: dict) -> str:
    full = f"{p['schema']}.{p['name']}"
    desc = p["desc"].strip().split("\n\n")[0].replace("\n", " ").replace("'", "''")
    out = [f"""
EXEC util.uspSetObjectDescription
      @SchemaName  = N'{p['schema']}'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'{p['name']}'
    , @Description = N'{desc}';
GO
"""]
    for role in p.get("grants", ["meritWebRole"]):
        out.append(f"""IF DATABASE_PRINCIPAL_ID (N'{role}') IS NOT NULL
BEGIN
    GRANT EXECUTE ON {full} TO {role};
END;
GO
""")
    return "\n".join(out)


def manifest_entry(p: dict) -> dict:
    """Name, kind, grants, permission and parameters (with whether each has a default) for the cross-tenant test suite."""
    params = []
    for line in (p.get("params") or "").splitlines():
        line = line.strip().lstrip(",").strip()
        if not line.startswith("@"):
            continue
        name, _, rest = line.partition(" ")
        declared, eq, _default = rest.partition("=")
        params.append({"name": name[1:], "type": " ".join(declared.split()).replace(" OUTPUT", ""),
                       "hasDefault": bool(eq), "isOutput": "OUTPUT" in declared})
    return {"name": f"{p['schema']}.{p['name']}", "kind": p["kind"], "grants": p.get("grants", []),
            "permission": p.get("permission"), "params": params}


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    names = set()
    total = 0
    manifest = []
    for mod_name in MODULES:
        mod = importlib.import_module(mod_name)
        for p in mod.PROCS:
            key = f"{p['schema']}.{p['name']}"
            if key in names:
                raise SystemExit(f"duplicate procedure {key}")
            names.add(key)
            text = write_proc(p) if p["kind"] == "write" else read_proc(p)
            (OUT / f"{key}.sql").write_text(text, encoding="utf-8")
            manifest.append(manifest_entry(p))
            total += 1
    # remove stale generated files
    for f in OUT.glob("*.sql"):
        if f.stem not in names:
            f.unlink()
    (OUT.parent.parent / "procedureManifest.json").write_text(
        json.dumps(sorted(manifest, key=lambda m: m["name"]), indent=1), encoding="utf-8")
    print(f"Generated {total} procedures under {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
