const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../../src/ExternalExecutor.App/Assets');
let checked = 0;
for (const name of fs.readdirSync(root).filter(name => /\.(css|html|js)$/.test(name))) {
    const source = fs.readFileSync(path.join(root, name), 'utf8');
    for (const match of source.matchAll(/#[0-9a-f]{3,8}\b/gi)) {
        let hex = match[0].slice(1);
        if (hex.length === 3 || hex.length === 4) hex = [...hex].map(c => c + c).join('');
        if (hex.length !== 6 && hex.length !== 8) continue;
        const channels = [0, 2, 4].map(offset => parseInt(hex.slice(offset, offset + 2), 16));
        assert(channels.every(channel => channel === channels[0]), `${name}: non-grayscale color ${match[0]}`);
        checked++;
    }
    for (const match of source.matchAll(/\brgba?\(\s*([\d.]+)(?:\s*,\s*|\s+)([\d.]+)(?:\s*,\s*|\s+)([\d.]+)/gi)) {
        assert(Number(match[1]) === Number(match[2]) && Number(match[2]) === Number(match[3]), `${name}: non-grayscale RGB ${match[0]}`);
        checked++;
    }
}
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
for (const id of ['nav-settings', 'tab-settings', 'setting-theme', 'setting-font-size', 'setting-reduce-motion', 'setting-line-numbers', 'setting-word-wrap', 'setting-tab-size', 'setting-restore-workspace', 'setting-auto-refresh', 'setting-auto-attach', 'settings-reset']) {
    assert(html.includes(`id="${id}"`), `Missing settings element: ${id}`);
}
assert(!/demo-bridge|demo-banner|demo-restore|setting-show-output/.test(html), 'Desktop must not load or expose the simulation UI');
assert(html.includes('>RYN SDK / Quorum</span>'), 'Desktop footer must identify the SDK and native Quorum API');
const app = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
assert(app.includes("const storageKey = 'ryn.workspace.v1'"), 'Keep legacy desktop workspace recovery key');
assert(!/Demo dispatch|simulated client|showOutput/.test(app), 'Native controls must retain desktop semantics');
console.log(`Grayscale palette and desktop settings markup checks passed (${checked} color values).`);
