# Contributing

Thanks for your interest. A few ground rules keep this simple for everyone:

- **License of contributions:** by submitting a contribution (pull request, patch, or
  suggestion incorporated into the code) you license it under **Apache 2.0**. No CLA to
  sign; this one line is the whole arrangement.
- This repo is a curated public mirror of a private development tree. Small fixes are
  usually merged as-is; larger changes may be re-applied on the private tree and land
  here in the next versioned drop, with credit in the commit message.
- Before opening a PR: `dotnet build src/SuperBiMcp.csproj -c Release` and
  `dotnet test tests/SuperBiMcp.Tests/SuperBiMcp.Tests.csproj -c Release` must both pass,
  and `SuperBiMcp capability-map --check` must be clean (regenerate the tool index with
  `SuperBiMcp capability-map` if you added or changed a tool).
- New tools follow the existing patterns: a `[McpServerTool]` method in the matching `src/Tools/*.cs`
  class delegating to a service, plus fault-sensitive tests.

Bug reports with a failing test or a minimal `.pbix`/PBIP reproduction are gold.
