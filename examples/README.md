# Examples

Generic reference drivers for Super-BI-MCP — **no client data**.

- **`driver_example.mjs`** — the JSON-RPC-over-stdio harness plus a commented
  walkthrough of the model phase (connect / run_dax / create_csv_table / add_measure)
  and the report phase (theme / page / slicer / card / chart / style_page / save).

Copy the harness, swap in your own table/column/measure names, and run:

```
node examples/driver_example.mjs <path-to>\bin\Release\net8.0\SuperBiMcp.dll <port>
```

Keep your own per-job driver scripts (the ones that build a specific report) out of
version control — they tend to accumulate details of the reports they touch.
