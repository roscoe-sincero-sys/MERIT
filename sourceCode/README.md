# MERIT

MERIT is a web application for heavy-metal exposure surveillance. It handles lab result intake, person matching,
case classification, investigations, work queues and registry extracts. It is built from the documents in
`docs/requirements`.

| Folder | Contents |
| --- | --- |
| `Merit.sln` | The solution to open in Visual Studio 2022. It includes the `Merit.slnLaunch` profile "Web + Worker". |
| `src/Merit.Domain` | Pure rules: population, normalization, thresholds, case classification, residence planning, extract formats. |
| `src/Merit.Application` | Services, validation, permission checks and the data access interfaces. |
| `src/Merit.Data.SqlServer` | Dapper repositories. They call stored procedures only. |
| `src/Merit.Web` | ASP.NET Core MVC with Razor and htmx 4 (server-rendered screens), the JSON API under `/api/v1`, and the Angular host under `/app/`. |
| `src/Merit.Web/ClientApp` | The Angular 22 client: work queues, held records, person matching review, results list, person timeline, case map, cluster review, reports. |
| `src/Merit.Worker` | A background service for intake batches, extract runs and cluster detection. |
| `tests/Merit.Tests` | Unit, architecture and endpoint-inventory tests (xUnit). |
| `tests/Merit.IntegrationTests` | Integration tests against a real SQL Server: every page (full and htmx partial), every API read, the runtime endpoint inventory, and the cross-tenant suite over every stored procedure. |
| `tests/e2e` | Playwright end-to-end and accessibility checks: keyboard only, tablet width, axe WCAG 2.1 AA. |
| `database` | `Deploy-MeritDatabase.ps1`, the generated scripts in `scripts/`, `procedureManifest.json`, and the Python generators in `tools/`. |
| `samples` | Synthetic lab CSVs for trying intake and the matching review. |

## Prerequisites

- .NET 9 SDK and Visual Studio 2022 (17.12 or later). The projects target `net9.0`; to move to .NET 10, change `Directory.Build.props`.
- SQL Server 2022 (the scripts assume an instance named `localhost\PORTAL`; pass `-ServerInstance` to use another) and `sqlcmd` on the PATH.
- Node.js 20 or later with npm, for the Angular screens. Without Node the build still succeeds with a warning, and every htmx screen works.
- A development HTTPS certificate: run `dotnet dev-certs https --trust`.

## 1. Deploy the database

Run the script from PowerShell in `sourceCode/database`. It is safe to run again:
- It creates missing schemas, tables, constraints, indexes and security policies.
- It replaces procedures and functions with `CREATE OR ALTER`.
- It re-runs the seed rows idempotently.

```powershell
cd sourceCode\database
$web    = Read-Host -AsSecureString 'meritWeb password'
$worker = Read-Host -AsSecureString 'meritWorker password'
.\Deploy-MeritDatabase.ps1 -ServerInstance 'localhost\PORTAL' -CreateLogins `
    -WebLoginPassword $web -WorkerLoginPassword $worker -IncludeDemoData
```

- `-CreateLogins` creates or updates the `meritWeb` and `meritWorker` SQL logins and maps them to `meritWebRole` and
  `meritWorkerRole`. Those roles have EXECUTE on their own procedures and no table access.
- `-IncludeDemoData` loads a synthetic Maryland tenant tree with users, people, results and cases. Never use it in production.
- `-SqlCredential` lets you deploy with SQL authentication instead of Windows authentication.
- `-PlatformAdminUserName` and `-PlatformAdminPassword` create the first account, the platform administrator, if it
  doesn't exist yet. Only the password hash reaches SQL Server, and the account enrolls MFA at first sign-in.
- Upgrades are in place. `000_infrastructure/060_authModelMigration.sql` moves older databases to the current access
  model: roles, profiles, the platform tenant and per-tenant access levels.

The scripts in `database/scripts` are generated. To change a table or procedure, edit `database/tools/*.py` and run
`python tools/generateTables.py`, `python tools/generateProcedures.py` and `python tools/generateDemo.py`.

## 2. Configure the connection strings

`appsettings.json` contains placeholders only. For local development, set the passwords you chose in step 1:

```powershell
cd sourceCode
dotnet user-secrets --project src/Merit.Web    set "ConnectionStrings:Merit" "Server=localhost\PORTAL;Database=MERIT;User ID=meritWeb;Password=<web password>;Encrypt=True;TrustServerCertificate=True;Application Name=Merit.Web"
dotnet user-secrets --project src/Merit.Worker set "ConnectionStrings:Merit" "Server=localhost\PORTAL;Database=MERIT;User ID=meritWorker;Password=<worker password>;Encrypt=True;TrustServerCertificate=True;Application Name=Merit.Worker"
```

You can also edit `appsettings.Development.json` in each project, which is what this working copy does.

## 3. Build and run

**In Visual Studio:**
1. Open `Merit.sln`.
2. Pick the **Web + Worker** launch profile on the toolbar.
3. Press F5. The site opens at https://localhost:7243, and the worker listens on http://localhost:5280 (`/health` and `/health/queues`).

**From the command line:**

```powershell
cd sourceCode
dotnet build Merit.sln
dotnet test Merit.sln
dotnet run --project src/Merit.Worker      # in one terminal
dotnet run --project src/Merit.Web         # in another
```

The first build runs `npm install` and `ng build` in `src/Merit.Web/ClientApp`, which writes to `wwwroot/app`. Later
Debug builds skip that step until the output is deleted; Release builds always rebuild the client. To skip it, pass
`-p:SkipAngularBuild=true`. To work on the client alone, run `npm run build` in `ClientApp`.

## 4. Sign in

Every demo user has the password `MeritDemo!2026`. The first sign-in enrolls an authenticator app: scan the QR code
or type the key, then keep the recovery codes.

| User | Works for | Access |
| --- | --- | --- |
| `platform` | MERIT platform | Platform administrator: accounts and roles only, no records, one profile |
| `admin` | Maryland (state) | State administrator: users and configuration, plus supervisor work, statewide |
| `supervisor` | Maryland | State surveillance supervisor |
| `intake` | Maryland | Intake specialist (uploads files, works held records) |
| `cityadmin` | Baltimore City | Jurisdiction administrator (accounts and access in Baltimore City only) and case manager |
| `countyadmin` | Baltimore County | Jurisdiction administrator for Baltimore County and case manager |
| `cmbalt` | Baltimore City | Case manager, Baltimore City |
| `cmbaltco` | Baltimore County | Case manager, Baltimore County |
| `cmboth` | Baltimore City | Case manager in Baltimore City and Baltimore County |
| `occinv` | Occupational program | Occupational investigator |
| `epi` | Maryland | Epidemiologist (read-only analysis) |

Everyone except `platform` can make extra profiles and switch between them under **Profile** in the header, and
administrators can view MERIT as the people they manage. `docs/MERIT User Guide - Access and Profiles.pdf` explains
the model for users.

To try intake, sign in as `intake` and upload `samples/chesapeake_lab_sample.csv` on the Intake screen.
`samples/README.md` describes what each row should do. To try the person matching review, also upload
`samples/chesapeake_match_review_sample.csv`, then open **Match review**.

## 5. Tests

| Layer | Command | Needs |
| --- | --- | --- |
| Unit, architecture, endpoint inventory | `dotnet test tests/Merit.Tests` | Nothing |
| Integration and cross-tenant | `dotnet test tests/Merit.IntegrationTests` | SQL Server (see below) |
| End-to-end and accessibility | `cd tests/e2e`, `npm install`, `npx playwright test` | MERIT running with the demo data; Microsoft Edge |

`dotnet test Merit.sln` runs the first two layers.

**Integration tests.**
- The suite deploys its own database, `MERIT_IT`, with the demo data, using `Deploy-MeritDatabase.ps1`. Every test is
  reported as skipped when the server can't be reached.
- Settings:
  - `MERIT_IT_SERVER`: the SQL Server instance. The default is `localhost\PORTAL`. A SQL Server 2022 container works too.
  - `MERIT_IT_DATABASE`: the database name.
  - `MERIT_IT_WEB_PASSWORD`: the meritWeb password. By default it is read from `src/Merit.Web/appsettings.Development.json`.
  - `MERIT_IT_SKIP_DEPLOY=1`: reuse the deployed database.
- What it covers:
  - Every page in both its full and its htmx partial shape.
  - Every API read.
  - That every action refuses an anonymous caller.
  - The cross-tenant suite. It calls every stored procedure the web login can run, as a Baltimore County user, with
    every id pointed at another tenant's records. It fails if any out-of-scope id comes back or any out-of-scope row
    changes. A self-check proves the scan would notice a leak.

**End-to-end tests.**
- Settings: `MERIT_E2E_URL`, `MERIT_E2E_SERVER` and `MERIT_E2E_DATABASE`.
- They reset MFA for the demo users `supervisor` and `cmbalt`, sign them in with the keyboard alone, then check two
  things at tablet width (768 × 1024):
  - Every key screen of both front ends meets WCAG 2.1 AA, checked with axe.
  - The keyboard-only flows work: skip link, search, the queue bulk select, match review J/K, the report measure
    picker, and the map zoom, pan and point focus.

## Security model

- Every request authenticates with the session cookie. Every action names a permission policy, and the endpoint
  inventory test fails if one does not. Antiforgery tokens are validated on every unsafe method.
- Each database connection sets a read-only `SESSION_CONTEXT`, and row-level security filters every tenant-owned
  table by it. A missing context returns no rows.
- **Tenant tree.** The platform tenant sits at the top. Below it are data roots (a region, state, tribe or
  stand-alone jurisdiction), then programs and jurisdictions. Each state or tribe keeps its own reference data
  (addresses, organizations, settings, thresholds).
- **Roles, grants and profiles.** A grant gives a role at a tenant, optionally with everything below it, at an access
  level: read, write, or full (write plus soft delete). A user's grants together are their *master profile*, which
  only administrators change. Users make extra profiles that are subsets of the master. A profile is clipped to the
  master every time it is used.
- **Session scope.** `auth.uspCreateSessionScope` resolves the active profile, or a view-as, into four things: the
  tenants with an access level each, the data roots, the administration scope, and the permissions.
- **Write predicates.** Write and soft-delete rights are enforced by row-level security block predicates
  (`auth.tvf*WritePredicate`), so a read-only profile is read-only whatever procedure is called.
- **Administration.** It is scoped to the tree. Administrators manage accounts whose home tenant they administer, and
  grant only at tenants they administer and only rights they hold there; the platform administrator grants any.
  Nobody can change their own master profile.
- **View as.** An administrator can see another person's access, clipped to the tenants the administrator manages and
  to the administrator's own rights. It is audited under the administrator's name.
- **Platform administrators** hold no record permissions, so their sessions see no data. They have one profile and
  cannot switch or view as anyone.
- `tests/Merit.IntegrationTests/AuthorizationScenarioTests.cs` checks all of this on an EPA Region 3 tree: West
  Virginia, Pennsylvania, Delaware, a tribe, and Jefferson and Berkeley counties.
- The Content Security Policy uses a per-request nonce, and there are no inline scripts or styles. htmx and USWDS CSS are served from
  MERIT's own origin.
