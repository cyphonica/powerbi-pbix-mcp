# Power BI REST API - capability inventory

Source: https://learn.microsoft.com/en-us/rest/api/power-bi/ (v1.0, base `https://api.powerbi.com/v1.0/myorg`).
Most operations have a My-workspace form and an "In Group" form (`/groups/{groupId}/...`). Admin ops under `/admin` need tenant-admin / service-principal scope.

20 operation groups (17 GA in TOC + 3 preview: Scorecards, Goals, GoalValues).

## Datasets (largest, ~58 ops incl. In-Group)
get/list datasets, delete, Execute Queries (DAX, JSON), Execute Dax Queries (Arrow), Refresh Dataset (sync + **enhanced async** -> 202 + Location, poll Get Refresh Execution Details), Cancel Refresh, Get Refresh History/Schedule, Update Refresh Schedule, Get/Update Direct Query Refresh Schedule, Get/Update Parameters, Bind To Gateway, Discover Gateways, Get Gateway Datasources, Update Datasources, Set All Dataset Connections, Take Over, Get/Post/Put Dataset Users, Get Datasources, Update Dataset (targetStorageMode), Query Scale Out (sync + status).

## Reports (~30 ops)
get/list, Clone, Delete, Export Report (.pbix/.rdl), **Export To File** (async PDF/PPTX/PNG; paginated also IMAGE/XLSX/DOCX/CSV/XML/MHTML/ACCESSIBLEPDF) + Get Export Status + Get File, Get Pages/Page, Rebind, Update Report Content, Update Datasources, Bind To Gateway, Take Over. ExportToFile supports RLS EffectiveIdentity, page/bookmark/visual selection, report filters, parameters, locale.

## Admin (~60 ops, all admin-scoped)
GetAsAdmin for apps/dashboards/dataflows/datasets/reports/groups/imports/capacities/pipelines/profiles (+ their users/datasources/subscriptions/tiles). Get Activity Events (audit). Encryption keys (add/get/rotate). InformationProtection set/remove labels. Groups admin (add/remove user, update, restore, unused artifacts). Capacities assign/unassign workspaces, refreshables. **Scanner API**: WorkspaceInfo GetModifiedWorkspaces -> PostWorkspaceInfo (getInfo) -> GetScanStatus -> GetScanResult. WidelySharedArtifacts (publishedToWeb, linksSharedToWholeOrganization).

## Groups / Workspaces (9)
Create/Delete/Get/Get list/Update Group; Add/Delete/Update Group User.

## Pipelines - Deployment Pipelines (16)
Create/Delete/Get/Update Pipeline, Get Pipelines, Assign/Unassign Workspace (stage), Deploy All, Selective Deploy, Get Stages, Get Stage Artifacts, Get Operation(s), users.

## Imports (8)
Post Import (upload .pbix/.rdl/.xlsx), Create Temporary Upload Location (1-10 GB), Get Import(s).

## Push Datasets (10)
PostDataset, GetTables, PutTable, PostRows, DeleteRows (real-time / streaming datasets).

## Dataflows (11)
Get/Delete/Update Dataflow, Get Dataflow (export JSON), Refresh, Get/Update Refresh Schedule, Transactions (get/cancel), Datasources, Upstream Dataflows, Save Gen1 as Gen2 (preview).

## Gateways (11)
Get gateway(s), Create/Get/Delete/Update Datasource, Datasource Status, Datasource Users (add/get/delete).

## Embed Token (6)
Generate Token (multi), Reports GenerateToken(InGroup / ForCreate), Datasets/Dashboards/Tiles GenerateToken.

## Capacities (11)
Get capacities, refreshables (per capacity + all), Workloads (get/patch), assign workspace(s) to capacity, assignment status.

## Dashboards (14)
Add, Delete, Get/list, Clone Tile, Get Tile(s). ## Apps (8): Get apps, dashboards, reports, tiles.
## Available Features (2), Dataflow Storage Accounts (2), Template Apps (1: Create Install Ticket), Users (1: Refresh User Permissions).

## Goals / Scorecards / GoalValues (preview, ~22)
Scorecards CRUD + MoveGoals + GetByReportId; Goals CRUD + refresh current/target value + refresh history + connections; GoalValues CRUD (check-ins, $expand=notes).

## Key cross-cutting capabilities
- **executeQueries** - run arbitrary DAX against a published dataset (JSON or Arrow).
- **ExportToFile** - server-side render to PDF/PPTX/PNG (+ paginated formats), async job.
- **Enhanced async refresh** - granular table/partition refresh with commitMode/maxParallelism/retryCount.
- **Scanner API** - tenant metadata governance scan.
- **Deployment pipelines** - dev/test/prod promotion.
- **Push/streaming** - real-time row push.
