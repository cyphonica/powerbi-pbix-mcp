# SuperBiMcp.Tests

The automated test suite for the Super BI MCP engine. It exercises the real ingest/staging engine - the
canonical-CSV plumbing every connector funnels data through - plus a set of
credential-gated LIVE connector smoke tests.

## Running

```sh
dotnet test tests/SuperBiMcp.Tests/SuperBiMcp.Tests.csproj -c Debug
```

With no environment variables set, every LIVE connector smoke test is **Skipped** (not failed), so the suite
is green on any box with the .NET 8 SDK and zero credentials.

## What is covered (offline, always runs)

- **CsvSink** - RFC-4180 field quoting, short-row padding, long-row truncation, the header-not-counted row
  tally, and the UTF-8 (no BOM) + CRLF byte contract.
- **ColumnTypeInference** - int64 / double / date / boolean / string and mixed-column widening; NZ
  `dd/MM/yyyy` and ISO dates; thousands separators; blanks ignored; all-blank / empty -> string. (Note: a
  pure-digit "leading-zero" code such as `007` parses as an integer and infers `int64` - the test documents
  this real behaviour; codes are protected upstream by being read as Excel inline strings, see below.)
- **ExcelService.XlsxToCsv** - a committed multi-sheet `fixtures/multi.xlsx` is streamed to CSV: shared
  strings + inline strings resolved, numbers passed through, a leading-zero **inline-string** code kept as
  text, first-sheet default, no-BOM output, and the missing-sheet / missing-file error paths.
- **CloudFileStaging** - `DetectExtension` (name wins, then content-type, then `.csv` default), CSV
  passthrough, xlsx -> one-CSV-per-sheet expansion, schema inference on register, `SafeTableName`
  sanitisation, and the don't-crash notes for missing / unsupported files and an empty CSV.
- **GraphClient** - relative-path -> absolute-URL building, `@odata.nextLink` absolute passthrough, and the
  `Str()` flattener (string / number / bool / nested object -> a single CSV cell, never dropped).
- **Graph value-matrix -> CSV** - first row promoted to header, ragged rows padded / truncated, nested cells
  flattened into one quoted field, nulls -> empty, and per-column type inference, through the same
  `GraphClient.Str` + `CsvSink` components the connectors compose.
- **FlatSpecBuilder** - the no-Solution auto-shape spec: one flat table per CSV, the `DataFolder`
  `{{DATA_FOLDER}}` parameter, the `Csv.Document |> PromoteHeaders |> TransformColumnTypes` M shape, spec-token
  -> M-type mapping, M-string quote escaping, and the structural guarantee that **no measures** are emitted
  (so a synthesised flat table can never collide a measure name with a column name). The generated spec is
  fed to the real `Headless.GenerateProject` to prove it scaffolds.
- **CappedReadStream** - reading under the cap is fine; the first read that crosses the cap throws (no silent
  truncation).
- **Headless scaffold** - `FlatSpecBuilder` specs are fed to the real `Headless.GenerateProject` to prove
  they scaffold to a PBIP.

## Fixtures

Committed under `fixtures/`:

- `sales.csv` - a tiny CSV with a quoted comma, an embedded quote, ISO-ish dates, decimals and booleans.
- `multi.xlsx` - a minimal valid two-sheet workbook (`People`, `Money`) with shared strings, inline strings,
  numbers and leading-zero text codes.

## LIVE connector smoke tests (credential-gated)

Each live test is a `[SkippableFact]`: it **skips** unless its credentials and source ids are present, and
only then runs a real one-round-trip-per-connector `FetchAsync` against the live source and asserts at least
one table's CSV landed with a data row. Tokens are read from the environment only - never committed, never
logged.

Set the relevant variables to opt a connector in. Tokens are short-lived OAuth bearer tokens (mint them from
your own provider / OAuth playground) unless noted.

### Microsoft Graph (one token covers all three)

| Variable | Required for | Meaning |
| --- | --- | --- |
| `DAXTEST_GRAPH_TOKEN` | Excel Online, SharePoint, Microsoft Lists | Microsoft Graph OAuth bearer token. |
| `DAXTEST_EXCEL_DRIVE_ID` | Excel Online | Drive id of the workbook's drive-item. |
| `DAXTEST_EXCEL_ITEM_ID` | Excel Online | The workbook's drive-item id. |
| `DAXTEST_EXCEL_WORKSHEET` | Excel Online (optional) | Limit to one worksheet's used range. |
| `DAXTEST_EXCEL_TABLE` | Excel Online (optional) | Limit to one named table (wins over worksheet). |
| `DAXTEST_SP_DRIVE_ID` | SharePoint | Drive id of the stored file's drive-item. |
| `DAXTEST_SP_ITEM_ID` | SharePoint | The file's drive-item id (a .csv or .xlsx). |
| `DAXTEST_LISTS_SITE_ID` | Microsoft Lists | SharePoint site id that owns the list. |
| `DAXTEST_LISTS_LIST_ID` | Microsoft Lists | The list id. |

### Google Sheets

| Variable | Required for | Meaning |
| --- | --- | --- |
| `DAXTEST_GOOGLE_TOKEN` | Google Sheets | Google OAuth bearer token (Sheets read scope). |
| `DAXTEST_SHEETS_SPREADSHEET_ID` | Google Sheets | The spreadsheet id. |
| `DAXTEST_SHEETS_RANGE` | Google Sheets (optional) | An A1 range / tab to read. |

### Xero

| Variable | Required for | Meaning |
| --- | --- | --- |
| `DAXTEST_XERO_TOKEN` | Xero | Xero OAuth bearer token. |
| `DAXTEST_XERO_TENANT_ID` | Xero | The Xero tenant (organisation) id. |
| `DAXTEST_XERO_FROM_DATE` | Xero (optional) | ISO from-date to bound the pull. |

### Shopify

| Variable | Required for | Meaning |
| --- | --- | --- |
| `DAXTEST_SHOPIFY_TOKEN` | Shopify | Shopify Admin API access token. |
| `DAXTEST_SHOPIFY_SHOP_DOMAIN` | Shopify | The shop domain (e.g. `my-store.myshopify.com`). |

### WooCommerce (no bearer token - key/secret pair)

| Variable | Required for | Meaning |
| --- | --- | --- |
| `DAXTEST_WOO_STORE_URL` | WooCommerce | The store base URL. |
| `DAXTEST_WOO_CONSUMER_KEY` | WooCommerce | WooCommerce REST consumer key. |
| `DAXTEST_WOO_CONSUMER_SECRET` | WooCommerce | WooCommerce REST consumer secret. |

### Example (PowerShell)

```powershell
$env:DAXTEST_GRAPH_TOKEN = "<graph-bearer>"
$env:DAXTEST_LISTS_SITE_ID = "<site-id>"
$env:DAXTEST_LISTS_LIST_ID = "<list-id>"
dotnet test tests/SuperBiMcp.Tests/SuperBiMcp.Tests.csproj -c Debug
```

Only the Microsoft Lists live smoke runs; the rest stay skipped.
