# Super BI MCP

[![Release](https://img.shields.io/github/v/release/cyphonica/powerbi-pbix-mcp?color=2E6EA6)](https://github.com/cyphonica/powerbi-pbix-mcp/releases)
[![License: FSL-1.1-ALv2](https://img.shields.io/badge/license-FSL--1.1--ALv2-blue)](LICENSE.md)
![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D6)
![Tools: 490](https://img.shields.io/badge/tools-490-2F9D68)

An MCP server that gives AI agents **full local Power BI authoring** - the semantic model, the report, and Power Query M - by editing `.pbix` and PBIP/PBIR files directly. **490 tools**, backed by ~1,800 automated tests.

Point Claude (or any MCP client) at it and the agent can build a model, write the DAX, lay out the report pages and visuals, transform the Power Query, lint and auto-fix best-practice violations, prove the RLS actually filters, rename a column across the model *and* every report binding in one atomic call, screenshot the rendered result out of a running Power BI Desktop, **evaluate DAX and read table rows out of a closed `.pbix` with no Fabric**, **edit a closed `.pbit` template's model with no Desktop at all**, and hand you back a finished `.pbix` - all on your machine.

## Why this instead of the other Power BI MCP servers

| | Super BI MCP | Microsoft Power BI Modeling MCP + agent skills | Community servers |
|---|---|---|---|
| Semantic model authoring (TOM) | Yes, with write transactions + rollback | Yes | Partial |
| **Report authoring** (pages, visuals, filters, bookmarks) | Yes - legacy Layout **and** PBIR, deterministic tools | Skill-guided raw JSON editing by the LLM (separate skill pack) | Report-only or preview |
| **Direct `.pbix` editing** | Yes | No - PBIP files only | Rare |
| Power Query M authoring | Yes - 60+ transform/generator tools | Analyze/refactor only | Rare |
| **Propagating renames** (model rename rewrites DAX, M, and every report binding, atomically) | Yes | No | One server, PBIP-only |
| **Model-to-report cross-analysis** (usage, unused fields incl. conditional-formatting bindings, broken-reference repair) | Yes | No | Partial |
| RLS **execution** testing (run any query as any role, per-role matrix) | Yes | No | Partial |
| DAX benchmark + Analysis Services trace (FE/SE split, cache hits) | Yes | Benchmark yes | Partial |
| Offline DAX linter (incl. catching hallucinated function names before they hit the engine) | Yes | No | One server |
| Visual formatting | Deterministic property registry for 52 visual types, validated against the theme schema | n/a | Raw JSON edits |
| Best Practice Analyzer | 89 rules, 28 with automatic fixes, Tabular Editor ruleset import | Loose | Rare |
| Desktop feedback loop (page screenshots + hot-reload from a running Desktop) | Yes - via the Desktop Bridge | Yes - same bridge, separate CLI | No |
| Layout, star-schema, theme-compliance, naming and file-integrity **audits** | Yes | No | Partial |
| DAX generators (time-intelligence, ranking, segmentation, calc groups, dynamic RLS, custom calendars) | Yes, live or straight to TMDL offline | n/a | Partial |
| Works offline, no Fabric account | Yes | Yes (local mode) | Varies |
| Ships as | **One integrated server** | A server + a skills pack + two CLIs | Single-purpose servers |

The short version: Microsoft's official server stops at the semantic model, and its report story is a separate skill pack that has the LLM hand-write layout JSON. This is **one server** that authors the whole artifact - model, report, and Power Query - with deterministic, validated tools, treats model and report as one cross-referenced thing (renames propagate, broken bindings are found and fixed, unused fields are provable), and works on the `.pbix` files you actually have, not just PBIP folders.

## Requirements

- Windows x64 (the engine drives the Analysis Services client libraries and, for live-model work, Power BI Desktop)
- Power BI Desktop - needed for live model editing, bulk refresh, bake, and the engine-backed offline `.pbix` tools; report-layer and file-level tools work without it
- Node.js 16+ to install via `npx` (below), **or** the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source

## Install

**One command (recommended)** - no .NET SDK needed. Add this to your MCP client's config (Claude Code / Claude Desktop `.mcp.json`, Cursor, etc.):

```json
{
  "mcpServers": {
    "super-bi": {
      "command": "npx",
      "args": ["-y", "github:cyphonica/powerbi-pbix-mcp"]
    }
  }
}
```

The first launch downloads the self-contained engine `SuperBiMcp.exe` (~109 MB) from the [latest release](https://github.com/cyphonica/powerbi-pbix-mcp/releases), verifies its SHA-256, and caches it under `~/.powerbi-pbix-mcp/`; later launches reuse the cache. Set `SUPERBI_MCP_EXE` to an existing `SuperBiMcp.exe` to skip the download.

Prefer a plain binary? Download `SuperBiMcp-win-x64.zip` from the [release](https://github.com/cyphonica/powerbi-pbix-mcp/releases), extract, and point your client's `command` straight at `SuperBiMcp.exe`.

**Build from source** (needs the .NET 8 SDK):

```bash
git clone https://github.com/cyphonica/powerbi-pbix-mcp
cd powerbi-pbix-mcp
dotnet build src/SuperBiMcp.csproj -c Release
```

then register the built DLL with `"command": "dotnet"`, `"args": ["<absolute path>/src/bin/Release/net8.0/SuperBiMcp.dll"]`.

Then ask your agent to open a `.pbix` and get to work. `docs/automation-capabilities/GENERATED-TOOL-INDEX.md` lists every tool; regenerate it any time with `SuperBiMcp capability-map`.

## CLI modes

The same binary is also a headless factory:

| Command | What it does |
|---|---|
| `SuperBiMcp build job.json` | Headless report build from a declarative manifest (single file or batch/glob) |
| `SuperBiMcp refresh <path-or-glob>` | Bulk-refresh `.pbix` files through Power BI Desktop, one at a time, saving each |
| `SuperBiMcp scaffold spec.json out/` | In-memory model build to a complete PBIP (TMDL) - no Desktop needed |
| `SuperBiMcp bake pbip/ out/` | Deploy + refresh a scaffolded model and produce the populated binary DataModel |
| `SuperBiMcp materialize <solutionId>` | Turn a solution spec + sample data into a starter `.pbix` (bring your own `solutions/` catalog - not bundled) |
| `SuperBiMcp sentinel-diff before.json after.json` | CI integrity gate over two model snapshots |
| `SuperBiMcp capability-map --check` | Docs drift gate - fails when the committed tool index no longer matches the code |
| `SuperBiMcp jobs list` | Inspect the durable job queue used to serialise heavy Desktop work |
| `SuperBiMcp templates` | Dump the 300 built-in starting-point templates |

## Testing

```bash
dotnet test tests/SuperBiMcp.Tests/SuperBiMcp.Tests.csproj -c Release
```

A handful of live-connector smoke tests skip unless you provide credentials via `DAXTEST_*` environment variables. Set `SUPERBI_TEST_SCRATCH` to move test scratch space off your system drive.

## Environment variables

| Variable | Purpose |
|---|---|
| `SUPERBI_PROTECTED_PATHS` | `;`-separated directories the DataMashup writer refuses to touch (fence off live folders) |
| `SUPERBI_TEST_SCRATCH` | Scratch root for the test suite (defaults to the system temp dir) |
| `SUPERBI_PBIX_SAVER` | Command template that turns a refreshed PBIP into a populated `.pbix` (see `src/Materialize.cs`) |
| `SUPERBI_AS_SERVER` | Analysis Services instance for `bake` (defaults to an ephemeral private engine) |
| `DAXOPS_PBI_TOKEN` / `DAXOPS_XMLA_ENDPOINT` / `DAXOPS_XMLA_CATALOG` | Power BI Service / XMLA connectivity for the service-side tools |

## License

Source-available under the **Functional Source License, FSL-1.1-ALv2** (see `LICENSE.md`).

Use it freely for anything internal, personal, educational, or for client/consulting work. You may not offer it, or a product substantially built on it, as a competing commercial product or hosted service. Each release automatically becomes **Apache 2.0 two years after publication**.

This is a curated public mirror of a private development tree; history starts at the first public release and updates land as versioned drops.

## Acknowledgements

See `NOTICE.md` for bundled third-party assets and rule-set attributions.
