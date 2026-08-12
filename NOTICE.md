# Notices and attributions

Super BI MCP bundles or derives from the following third-party material:

- **Power BI report theme JSON schema** (`src/Resources/reportThemeSchema-2.155.json`) -
  published by Microsoft in
  [microsoft/powerbi-desktop-samples](https://github.com/microsoft/powerbi-desktop-samples)
  (folder "Report Theme JSON Schema") under the MIT License. Used as an embedded resource to
  drive the visual-property registry. Redistributed here under that license with the required
  notice preserved:

  > MIT License
  >
  > Copyright (c) Microsoft Corporation. All rights reserved.
  >
  > Permission is hereby granted, free of charge, to any person obtaining a copy of this
  > software and associated documentation files (the "Software"), to deal in the Software
  > without restriction, including without limitation the rights to use, copy, modify, merge,
  > publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
  > to whom the Software is furnished to do so, subject to the following conditions:
  > The above copyright notice and this permission notice shall be included in all copies or
  > substantial portions of the Software.
  > THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
  > INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
  > PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
  > FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
  > OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
  > DEALINGS IN THE SOFTWARE.

- **Best Practice Analyzer rules** (`src/Services/BpaRules.cs`) - the rule catalogue includes
  rules derived from the Tabular Editor community
  [BestPracticeRules](https://github.com/TabularEditor/BestPracticeRules) collection and from
  Microsoft's [semantic-link-labs](https://github.com/microsoft/semantic-link-labs) BPA rules,
  with matching rule IDs so existing suppressions carry over. Both published under MIT.
- **Base report theme** (`src/Resources/SuperBiBase.json`) and the six palette themes are
  original works authored for this project - no third-party material.
- **NuGet dependencies** are restored at build time under their own licenses, including the
  Microsoft Analysis Services client libraries (Microsoft EULA) and System.Drawing.Common (MIT).
