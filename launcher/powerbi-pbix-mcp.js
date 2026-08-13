#!/usr/bin/env node
/*
 * Super BI MCP launcher (repo: cyphonica/powerbi-pbix-mcp).
 *
 * On first run it downloads the self-contained Windows engine (SuperBiMcp.exe) from the tagged
 * GitHub Release, verifies its SHA-256, and caches it under ~/.powerbi-pbix-mcp/<version>/. Every run
 * spawns that engine and forwards stdio - the engine IS the MCP server (JSON-RPC over stdin/stdout),
 * so an MCP client can launch this with `npx github:cyphonica/powerbi-pbix-mcp`.
 *
 * Windows only (the engine drives the local Power BI / Analysis Services stack). Set
 * SUPERBI_MCP_EXE to an existing SuperBiMcp.exe to skip the download (dev / offline / air-gapped).
 */
'use strict';

const fs = require('fs');
const os = require('os');
const path = require('path');
const https = require('https');
const crypto = require('crypto');
const { spawn } = require('child_process');

const VERSION = 'v0.1.0';
const ASSET = 'SuperBiMcp.exe';
const URL = `https://github.com/cyphonica/powerbi-pbix-mcp/releases/download/${VERSION}/${ASSET}`;
const SHA256 = 'f85c0d1601d54fd579e3e667123d4727d4aaabeb5be9a06449a643a55a7c75da';

function log(msg) { process.stderr.write(`[powerbi-pbix-mcp] ${msg}\n`); }

function sha256(file) {
  return new Promise((resolve, reject) => {
    const h = crypto.createHash('sha256');
    fs.createReadStream(file).on('data', d => h.update(d)).on('end', () => resolve(h.digest('hex'))).on('error', reject);
  });
}

function download(url, dest, redirects = 0) {
  return new Promise((resolve, reject) => {
    if (redirects > 8) return reject(new Error('too many redirects'));
    https.get(url, { headers: { 'User-Agent': 'powerbi-pbix-mcp-launcher' } }, res => {
      if ([301, 302, 303, 307, 308].includes(res.statusCode)) {
        res.resume();
        return resolve(download(res.headers.location, dest, redirects + 1));
      }
      if (res.statusCode !== 200) { res.resume(); return reject(new Error(`download failed: HTTP ${res.statusCode}`)); }
      const tmp = dest + '.downloading';
      const out = fs.createWriteStream(tmp);
      res.pipe(out);
      out.on('finish', () => out.close(() => { fs.renameSync(tmp, dest); resolve(); }));
      out.on('error', reject);
    }).on('error', reject);
  });
}

async function ensureExe() {
  const override = process.env.SUPERBI_MCP_EXE;
  if (override) {
    if (!fs.existsSync(override)) throw new Error(`SUPERBI_MCP_EXE points at a missing file: ${override}`);
    return override;
  }
  const dir = path.join(os.homedir(), '.powerbi-pbix-mcp', VERSION);
  const exe = path.join(dir, ASSET);
  if (fs.existsSync(exe) && (await sha256(exe)) === SHA256) return exe;

  fs.mkdirSync(dir, { recursive: true });
  log(`downloading engine ${VERSION} (~109 MB, one time) ...`);
  await download(URL, exe);
  const got = await sha256(exe);
  if (got !== SHA256) { try { fs.unlinkSync(exe); } catch (_) {} throw new Error(`checksum mismatch (expected ${SHA256}, got ${got})`); }
  log('engine ready.');
  return exe;
}

(async () => {
  if (process.platform !== 'win32') {
    log('Super BI MCP is Windows only - it drives the local Power BI / Analysis Services engine.');
    process.exit(1);
  }
  let exe;
  try { exe = await ensureExe(); }
  catch (e) { log('error: ' + e.message); process.exit(1); }

  const child = spawn(exe, process.argv.slice(2), { stdio: 'inherit' });
  child.on('exit', (code, signal) => process.exit(signal ? 1 : (code === null ? 0 : code)));
  child.on('error', e => { log('failed to launch engine: ' + e.message); process.exit(1); });
})();
