# Specialised automation surfaces

Source: learn.microsoft.com. Power BI host `api.powerbi.com/v1.0/myorg`; Fabric host `api.fabric.microsoft.com/v1`.

## Push / streaming / real-time datasets (creation cutoff 31 Oct 2027 -> Fabric RTI)
PostDataset (defaultMode Push/Streaming/PushStreaming), PostRows, DeleteRows, GetTables, PutTable. Push = stores data + full reports; Streaming = ~1h cache + custom tiles; PubNub = client-side. Limits: 10k rows/POST, 1M rows/hr, 75 cols/table. Push URL with rowkey authorizes without OAuth.

## Dataflows
- Gen1 (Power BI, GA): Get/Delete/Refresh/Update Dataflow, Get Dataflow (export model.json/CDM), transactions, refresh schedule, datasources, upstream; import via PostImport (model.json); Save Gen1-as-Gen2 (preview). Premium: compute engine, computed/linked entities, incremental refresh.
- Gen2 (Fabric, item CRUD GA): create/get/list/update/delete + getDefinition/updateDefinition (parts: queryMetadata.json + mashup.pq), Discover Parameters; run/publish/refresh via Job Scheduler (preview); CI/CD + git. SP auth not yet supported.

## Datamarts - RETIRED (1 Nov 2025 -> Fabric Warehouse). No CRUD API; metadata only via scanner; auto-model refreshable via Datasets API.

## Paginated reports (RDL, GA)
URL params: rp: (report params), rdl: (format/reportView/parameterPanel), rs:/rc: (SSRS). REST: ExportToFile (paginated formats PDF/PPTX/IMAGE/XLSX/DOCX/CSV/XML/MHTML/ACCESSIBLEPDF), Get/Update Datasources, Bind To Gateway, Clone, upload via PostImport (.rdl). Rebind NOT supported for paginated.

## Scanner / metadata API (GA, SP-capable, Tenant.Read.All)
GetModifiedWorkspaces (incremental, 30/hr) -> PostWorkspaceInfo (getInfo, 100 ws/scan, lineage/datasourceDetails/datasetSchema/datasetExpressions flags) -> GetScanStatus -> GetScanResult (24h). + GetGroupsAsAdmin ($expand), Get Activity Events (audit, 1 day/last 28d), InformationProtection set/remove labels.

## Export-to-file (GA, 6 endpoints, Power BI + paginated)
ExportToFile (POST ExportTo -> 202) -> GetExportToFileStatus (poll Retry-After/percentComplete) -> GetFileOfExportToFile. Power BI formats PDF/PPTX/PNG; body: pages[] (pageName/visualName/bookmark), reportLevelFilters (1 only), datasetToBind, identities (RLS). Premium/Embedded/Fabric only; 500 concurrent; 50 page/visual per job; 250 MB cap.

## Deployment pipelines
- Power BI Pipelines (GA, legacy but needed for dataflows + app updates): CRUD, stages, assign/unassign workspace, Deploy All, Selective Deploy (per-type arrays + options allowCreate/Overwrite/PurgeData/TakeOver), operations.
- Fabric Core Pipelines (GA, successor): CRUD (2-10 stages), assign/unassign, Deploy Stage Content (stage GUIDs + items[] or all, LRO), operations, role assignments. Dataflows NOT supported here. Deployment/parameter rules are UI-only (no REST).

## Gateways + credentials + dataset datasource/param update (GA)
Gateways: get gateways (publicKey), create/update/delete datasource (Update = sets credentials), datasource status/users. CredentialDetails: credentialType (Basic/Windows/Anonymous/OAuth2/Key/SAS/KeyPair), encryptedConnection, encryptionAlgorithm (RSA-OAEP on-prem). Dataset: Get/Update Datasources (exact-schema swap), Get/Update Parameters (UpdateMashupParametersRequest {name,newValue}, max 100), Bind To Gateway, Take Over.

## Subscriptions - UI-ONLY (no cloud create/update/delete API; admin read-only preview). Substitute: scheduled flow + ExportToFile + send email. (CRUD exists only in Report Server API.)

## GAP-RELEVANT
All SERVICE-tier (cloud). Most valuable to our engine IF we add a service tier: ExportToFile (render PDF/PPTX/PNG headless), scanner (governance), refresh + parameter/datasource update (operate published models), deployment pipelines + git (CI/CD). Push datasets / paginated / dataflows are nicher. Subscriptions + datamarts are dead ends (no API).
