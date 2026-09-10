'use strict';
// Development-only runner. Each suite uses an isolated browser profile and synthetic native bridge.
const path = require('node:path');
const fs = require('node:fs');
const {spawnSync} = require('node:child_process');

const repository = path.resolve(__dirname, '..');
const suites = new Map([
  ['plugins', 'Test-PluginSettings.cjs'],
  ['settings', 'Test-OnlineSettings.cjs'],
  ['collections', 'Test-OnlinePluginPages.cjs'],
  ['media', 'Test-MediaHub.cjs'],
  ['playback', 'Test-PlaybackComponents.cjs'],
  ['transport', 'Test-MediaTransportComponents.cjs'],
]);
const requested = process.argv.slice(2);
if (requested.includes('--help')) {
  console.log(`Usage: npm run test:ui -- [${[...suites.keys()].join(' ')}]\nOmit names to run all suites. Requires installed Microsoft Edge.\nAURALIS_UI_ROOT optionally selects a published wwwroot; no real accounts or plugins are used.`);
  process.exit(0);
}
const selected = requested.length ? requested : [...suites.keys()];
if (selected.some(name => !suites.has(name)) || new Set(selected).size !== selected.length) {
  console.error('Unknown or repeated suite. Use npm run test:ui -- --help.');
  process.exit(2);
}
// Do not silently use a personal NODE_PATH dependency: validate the lock-installed local package.
const localPackage = path.join(repository, 'node_modules/playwright/package.json');
const expectedVersion = require('../package.json').devDependencies.playwright;
if (!fs.existsSync(localPackage) || JSON.parse(fs.readFileSync(localPackage, 'utf8')).version !== expectedVersion) {
  console.error(`Run npm ci --ignore-scripts in the repository first (Playwright ${expectedVersion}).`);
  process.exit(2);
}
const uiRoot = path.resolve(process.env.AURALIS_UI_ROOT || path.join(repository, 'Auralis/wwwroot'));
if (!fs.existsSync(path.join(uiRoot, 'index.html'))) {
  console.error('AURALIS_UI_ROOT must point to a complete source or published wwwroot directory.');
  process.exit(2);
}
const environment = {...process.env, AURALIS_UI_ROOT: uiRoot};
delete environment.NODE_PATH;
for (const name of selected) {
  console.log(`\nAuralis UI suite: ${name}`);
  const child = spawnSync(process.execPath, [path.join(__dirname, suites.get(name))], {
    cwd: repository, env: environment, stdio: 'inherit', windowsHide: true,
  });
  if (child.error || child.status !== 0) {
    console.error(`UI suite failed: ${name}${child.error ? ` (${child.error.code})` : ''}`);
    process.exit(child.status || 1);
  }
}
console.log(`PASS ${selected.length} UI suites. Synthetic UI checks are not native playback/account acceptance.`);
