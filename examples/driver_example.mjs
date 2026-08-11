// ─────────────────────────────────────────────────────────────────────────────
// Super-BI-MCP — example driver (generic, no client data).
//
// Shows how to drive the MCP server over stdio JSON-RPC. Copy this harness, swap
// in your own table/column/measure names, and run:
//     node driver_example.mjs <path-to>\SuperBiMcp.dll <port>
//
// THE TWO RULES:
//   1. MODEL tools (connect_model, add_measure, set_partition_m, create_csv_table…)
//      need the .pbix OPEN in Power BI Desktop. REPORT tools (open_report, add_page,
//      apply_report_theme, style_page, save_report…) need it CLOSED.
//   2. Big files: stage to CSV first (stage_excel_to_csv / unpivot_weekly_csv) then
//      load with Csv.Document — never Excel.Workbook / in-mashup unpivot on big data.
// ─────────────────────────────────────────────────────────────────────────────
import { spawn } from 'node:child_process';

const [dll, portArg] = process.argv.slice(2);
const srv = spawn('dotnet', [dll], { stdio: ['pipe', 'pipe', 'ignore'] });

// --- minimal JSON-RPC-over-stdio harness ---
let buf = ''; const pending = new Map(); let idc = 0;
srv.stdout.on('data', d => {
  buf += d.toString();
  let i; while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1);
    if (!line) continue;
    let m; try { m = JSON.parse(line); } catch { continue; }
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
  }
});
const rpc = (method, params) => new Promise(res => { const id = ++idc; pending.set(id, res); srv.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n'); });
const notify = (method, params) => srv.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
// call a tool; returns the parsed result object (server returns JSON text in content[0])
const tool = async (name, args) => {
  const r = await rpc('tools/call', { name, arguments: args });
  if (r.error) return { ok: false, error: r.error };
  return JSON.parse(r.result.content[0].text);
};

(async () => {
  await rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'example', version: '1.0' } });
  notify('notifications/initialized', {});

  // ============ MODEL phase (pbix OPEN in Desktop) ============
  const port = portArg ? { port: parseInt(portArg, 10) } : {};   // omit port → newest open model
  const c = await tool('connect_model', port);
  console.log('connected:', c.tables, 'tables on port', c.port);

  // read data back (run_dax returns rows; validate_dax only returns pass/fail)
  const v = await tool('run_dax', { sessionId: c.sessionId, dax: 'ROW("rows", COUNTROWS(\'YourFact\'))' });
  console.log('rowcount:', JSON.stringify(v.rows ?? v));

  // one-call import table from a staged CSV (infers types, declares all columns, refreshes):
  //   await tool('create_csv_table', { sessionId: c.sessionId, table: 'YourFact',
  //     csvPath: 'C:\\data\\fact.csv', pathExpression: 'DataFolder & "fact.csv"' });

  // add a measure:
  //   await tool('add_measure', { sessionId: c.sessionId, table: 'YourFact',
  //     name: 'Total Sales', dax: 'SUM(YourFact[Sales])', formatString: '\\$#,0' });

  // ============ REPORT phase (pbix CLOSED) ============
  // const open = await tool('open_report', { pbixPath: 'C:\\path\\Report.pbix' });
  // const rs = open.reportSessionId;
  // await tool('apply_report_theme', { reportSessionId: rs, preset: 'executive' });
  // const p = (await tool('add_page', { reportSessionId: rs, displayName: 'Overview' })).pageName;
  // await tool('set_page_background', { reportSessionId: rs, pageName: p, color: '#F6F8FB' });
  // await tool('add_textbox', { reportSessionId: rs, pageName: p, text: 'Overview', x: 36, y: 22, width: 900, height: 52, fontSize: 24, bold: true, color: '#16365C' });
  // await tool('add_slicer', { reportSessionId: rs, pageName: p, table: 'YourDim', field: 'Category', title: 'Category', mode: 'Dropdown', x: 36, y: 114, width: 224, height: 66 });
  // await tool('add_card',   { reportSessionId: rs, pageName: p, table: 'YourFact', measure: 'Total Sales', x: 36, y: 196, width: 286, height: 116, title: 'Sales' });
  // await tool('add_chart',  { reportSessionId: rs, pageName: p, chartType: 'lineChart', categoryTable: 'Calendar', categoryField: 'Date', valueTable: 'YourFact', valueMeasure: 'Total Sales', x: 36, y: 324, width: 606, height: 372, title: 'Trend' });
  // await tool('style_page', { reportSessionId: rs, pageName: p });   // white cards, rounded corners, drop shadow
  // await tool('save_report', { reportSessionId: rs });

  srv.stdin.end();
  setTimeout(() => process.exit(0), 400);
})().catch(e => { console.error('ERR', e); process.exit(1); });
