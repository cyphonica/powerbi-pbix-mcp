# Notices and attributions

Super BI MCP bundles or derives from the following third-party material:

- **Power BI report theme JSON schema** (`src/Resources/reportThemeSchema-2.155.json`) -
  published by Microsoft in the Power BI community samples. Used as an embedded resource to
  drive the visual-property registry. Copyright Microsoft Corporation.
- **Base report theme** (`src/Resources/CY24SU06.json`) - a stock base theme shipped with
  Power BI Desktop, embedded so generated reports render with a current theme.
  Copyright Microsoft Corporation.
- **Best Practice Analyzer rules** (`src/Services/BpaRules.cs`) - the rule catalogue includes
  rules derived from the Tabular Editor community
  [BestPracticeRules](https://github.com/TabularEditor/BestPracticeRules) collection and from
  Microsoft's [semantic-link-labs](https://github.com/microsoft/semantic-link-labs) BPA rules,
  with matching rule IDs so existing suppressions carry over. Both published under MIT.
- **NuGet dependencies** are restored at build time under their own licenses, including the
  Microsoft Analysis Services client libraries (Microsoft EULA) and SixLabors.ImageSharp
  (Six Labors Split License).
