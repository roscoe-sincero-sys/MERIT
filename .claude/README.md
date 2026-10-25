# ponytail-sql-objects — Claude Code SQL Server conventions

A Claude Code skill and validation hook that make Claude write SQL Server tables, views, procedures, functions and
triggers to this project's mandatory conventions: audit columns, soft delete, constraint naming, header blocks,
`MS_Description` on everything, and re-runnable DDL. It exists so those rules are applied — and checked — on every
request, instead of being restated by hand each time and missed when they are not.

![SQL Server 2022+](https://img.shields.io/badge/SQL%20Server-2022%2B-CC2927)
![Python 3.9+](https://img.shields.io/badge/python-3.9%2B-3776AB)
![Claude Code](https://img.shields.io/badge/Claude%20Code-skill%20%2B%20hook-D97757)

---

## Prerequisites

- **Claude Code** (CLI, desktop, or IDE extension).
- **Python 3.9 or later** on `PATH` as `python` — the hook runs `python ".../validate-sql.py"`. Standard library only;
  nothing to `pip install`.
- **SQL Server 2022 or later** for the scripts Claude produces. 2022 is a hard floor (the templates use `LEAST ()`);
  SQL Server 2025-only features are deliberately not used.
- **`sqlcmd`**, or SSMS with *Query > SQLCMD Mode* on, to run the install scripts in `skills/ponytail-sql-objects/scripts/`.
- **`db_owner`** on the target database to run those scripts. One optional section of `permissions.sql` also needs
  `ALTER ANY LOGIN` on the server.

## Installation

The contents of this folder are laid out as a project's `.claude/` directory.

1. Copy `settings.json`, `hooks/` and `skills/` into `<your project>\.claude\`:

   ```
   <your project>\.claude\settings.json
   <your project>\.claude\hooks\validate-sql.py
   <your project>\.claude\skills\ponytail-sql-objects\SKILL.md   (plus templates\, scripts\, references\)
   ```

   If the project already has a `.claude\settings.json`, merge the `hooks.PostToolUse` entry into it rather than
   overwriting it.

2. Restart Claude Code in the project, then run `/skills` and confirm `ponytail-sql-objects` is listed.

3. Confirm the hook can run:

   ```
   python .claude\hooks\validate-sql.py .claude\skills\ponytail-sql-objects\templates
   ```

   It should end with `PASS  SQL conventions: ...`.

4. On each database the generated SQL will be deployed to, run the four install scripts once, in this order. Name the
   database **twice** — `-d` and `-v DbName=` must match, or the script stops before changing anything:

   ```
   sqlcmd -S <server> -d <database> -v DbName=<database> -I -C -i .claude\skills\ponytail-sql-objects\scripts\logExecutionLogging.sql
   sqlcmd -S <server> -d <database>                      -I -C -i .claude\skills\ponytail-sql-objects\scripts\checkDbChangeLogging.sql
   sqlcmd -S <server> -d <database> -v DbName=<database> -I -C -i .claude\skills\ponytail-sql-objects\scripts\logdBChanges.sql
   sqlcmd -S <server> -d <database> -v DbName=<database> -I -C -i .claude\skills\ponytail-sql-objects\scripts\permissions.sql
   ```

   `-I` is required (`sqlcmd` defaults `QUOTED_IDENTIFIER` off). Every script is re-runnable and never drops or deletes
   anything. See [the skill's README](skills/ponytail-sql-objects/README.md#4-the-sql-scripts-you-run) for what each
   one does and what its report means.

## Usage

### Ask Claude in plain language

The skill loads on its own whenever a request touches database objects. You can also invoke it explicitly: "use the
ponytail-sql-objects skill".

| Ask | You get |
|---|---|
| "Add a `Permit` table to `dbo`" | Guarded `CREATE TABLE` with the 7 audit columns, `DF_dbo_Permit_*` constraints, a filtered unique index, an `AFTER UPDATE` audit trigger, and descriptions on the table and every column |
| "Add a `PermitStatus` column to `dbo.Permit`" | A separate guarded `ALTER TABLE ... ADD` block |
| "Write a view over `dbo.Permit`" | `CREATE OR ALTER VIEW` with the header block and `WHERE isDeleted = 0` |
| "Write a procedure that merges the permit batch" | Header block, `TRY`/`CATCH`, `logs.ExecutionLog` instrumentation, bare `;THROW;` |
| "`Permit` needs row history" | System-versioned base table, view wrapper, and three `INSTEAD OF` triggers |
| "Does this database record its schema changes?" | The read-only check script and how to fix anything missing |

Claude writes scripts; it does not connect to your server. Say which database you mean.

### The validation hook

After every `Write` or `Edit`, the hook checks the file if it ends in `.sql`. When it finds violations it exits with
code 2 and sends the list back to Claude, which fixes them before carrying on. Example output (excerpt):

```
SQL convention violations in Permit.sql (11) - see .claude/skills/ponytail-sql-objects/SKILL.md:
  - Permit.sql:1: table dbo.Permit is missing required audit column 'createdBy'
  - Permit.sql:4: constraint 'DF_dbo_Facility_isDeleted' must be named 'DF_dbo_Permit_<fieldName>' for table dbo.Permit (default constraint names must be unique per database)
  - Permit.sql:7: hard DELETE is not permitted; set isDeleted = 1 instead
  - Permit.sql:1: unguarded CREATE TABLE; the script must be safe to run twice. Wrap it: IF OBJECT_ID (N'<schema>.<Table>', N'U') IS NULL
```

You can also run it by hand, or from a build step, over any files or folders:

```
python .claude\hooks\validate-sql.py database\
```

| Exit code | Meaning |
|---|---|
| `0` | `PASS` — no violations |
| `1` | `FAIL` — violations found (CLI mode); summary line gives the count |
| `2` | Bad argument, no `.sql` files found, or violations found (hook mode) |

## Configuration

| Setting | Where | Default | What it does |
|---|---|---|---|
| `RowHistory` | Configuration table in `skills/ponytail-sql-objects/SKILL.md` | `off` | `off`: plain tables (`templates/table.sql`). `on`: system-versioned tables with a view wrapper (`templates/table-temporal.sql`). To turn it on for one table, say so in the request instead of changing the default. |
| `DbName` | `sqlcmd -v DbName=<database>` | none, on purpose | The database the install scripts install into. It must match `-d`. There is no in-file `:setvar` default, because one would override the command line. |
| `PostToolUse` hook | `settings.json` → `hooks` | `Write\|Edit` → `validate-sql.py` | Runs the SQL validator after each file edit. Change the `matcher` to widen or narrow which tools trigger it. Delete the entry to turn validation off. |
| `applicationRole`, `readOnlyRole` | Database roles | must exist | The roles the procedure templates and `permissions.sql` grant to. The scripts don't create them, and a grant to a role that doesn't exist is skipped. See `references/external-dependencies.md`. |

For more detail (troubleshooting, what the skill will never produce, and the full file inventory), see
[skills/ponytail-sql-objects/README.md](skills/ponytail-sql-objects/README.md). `SKILL.md` is the authority on the
rules themselves.
