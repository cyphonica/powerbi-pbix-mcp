# Microsoft Fabric REST API - Power BI automation surface

Source: learn.microsoft.com/rest/api/fabric. Base `https://api.fabric.microsoft.com/v1`. (Scanner + dataset-refresh stay on `api.powerbi.com/v1.0/myorg`.)

## THE code-authoring pattern (most important)
Every item type exposes `POST .../{itemId}/getDefinition` and `POST .../{itemId}/updateDefinition` (async LRO, POST not GET/PUT). Payload: `{definition:{format, parts:[{path, payload:<base64>, payloadType:"InlineBase64"}]}}`.
- Semantic model: `?format=TMDL` (default, folder of .tmdl) or `TMSL` (model.bim). definition.pbism always required.
- Report: `?format=PBIR` (default, folder) or `PBIR-Legacy` (report.json). definition.pbir always required + holds the model binding (datasetReference byPath/byConnection) -> editing it rebinds the report to a different model.
This is how you author models (TMDL/TMSL) and reports (PBIR) as code in the service.

## Core
- Workspaces: CRUD + role assignments + assign/unassign capacity + provision identity + domain/tags/firewall/networking.
- Items (generic): List/Create/Get/Update/Delete + getDefinition/updateDefinition + move/bulkMove + connections + bulk export/import (preview).
- Capacities: List/Get. Long Running Operations: Get Operation State/Result (poll every 202).
- OneLake: shortcuts CRUD + bulk + reset cache; data-access-security roles (preview).
- **Git integration** (GA): connect/disconnect, initializeConnection, commitToGit (All/Selective), updateFromGit (conflictResolution), getStatus, credentials. Azure DevOps or GitHub.
- **Deployment Pipelines** (GA): CRUD, stages, assign/unassign workspace, **deploy stage content** (source->target, items[] or all, LRO), operations.
- **Job Scheduler** (GA): Run On Demand Item Job (jobType RunNotebook / dataset refresh), get/cancel/list instances, create/get/list/update/delete schedules (Cron/Daily/Weekly/Monthly).
- Connections + Gateways (GA): create/get/update/delete connections (credentials that models/dataflows bind to), gateways + members + role assignments.

## Item-type APIs (uniform CRUD + getDefinition/updateDefinition)
Semantic Models (+ bindConnection), Reports, Notebooks, Lakehouses (+ tables/load/maintenance), Warehouses (no def), SQL Endpoints (refresh metadata), Dataflows Gen2 (+ discover params), Data Pipelines, KQL DB / Eventhouse / Eventstream, Mirrored Databases (+ mirroring control), Copy Jobs, Variable Libraries, Environments, Spark Job Defs, ML Models, Dashboards, Datamarts, GraphQL APIs, Reflex/Activator.

## Fabric Admin
Workspaces/Users/Items access details (preview), tenant settings + delegated overrides, Domains (CRUD + assign workspaces), Labels (bulk set/remove), Tags (CRUD), External Data Shares.

## Power BI scanner + refresh (still on api.powerbi.com)
Scanner: PostWorkspaceInfo (getInfo) -> GetScanStatus -> GetScanResult; GetModifiedWorkspaces (incremental). Datasets refresh: Refresh (enhanced async), Get Refresh History/Execution Details, Cancel, Update Refresh Schedule, datasources/gateway binding.

## GAP-RELEVANT for our engine
This is the SERVICE/cloud automation layer - mostly OUTSIDE our engine's current local-.pbix-editing scope. IF we want a "deploy/publish + service ops" capability: getDefinition/updateDefinition (push our locally-authored TMDL/PBIR to a workspace), git integration, deployment pipelines, scheduled refresh, scanner-based governance. Our engine could gain a "service" tool group fronting these (auth via SP). Decision point for the user - separate from the local Desktop-parity waves.
