"""Shared fragments for procedure definitions."""

# A new application-style key generated inside a procedure: random bytes 0-9, milliseconds since 1970 big-endian in
# bytes 10-15, which is where SQL Server sorts a UNIQUEIDENTIFIER first. Same layout as Merit.Domain.Ids.TimeOrderedId.
# NEWID () is evaluated per row, so this is safe in a set-based INSERT ... SELECT.
NEWID = ("CAST (CAST (NEWID () AS BINARY (10)) + CAST (DATEDIFF_BIG (MILLISECOND, '19700101', SYSUTCDATETIME ()) "
         "AS BINARY (6)) AS UNIQUEIDENTIFIER)")

WEB = ["meritWebRole"]
WORKER = ["meritWorkerRole"]
BOTH = ["meritWebRole", "meritWorkerRole"]

# Rejects a missing or stale row version on an UPDATE. Use after the UPDATE: @@ROWCOUNT = 0 means the row changed or is
# not visible.
CONCURRENCY = """IF @@ROWCOUNT = 0
BEGIN
    ;THROW 51409, N'This record was changed by someone else, or is no longer available. Reload it and try again.', 1;
END;"""


def not_found(var: str, table: str, key: str, what: str) -> str:
    return f"""IF NOT EXISTS (SELECT 1 FROM {table} WHERE {key} = {var} AND isDeleted = 0)
BEGIN
    ;THROW 51404, N'{what} was not found, or you do not have access to it.', 1;
END;"""
