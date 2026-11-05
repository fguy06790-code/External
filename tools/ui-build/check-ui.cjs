// Exercises the shipped interface in Chromium. The DLL is never loaded here.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const assets = path.join(root, 'src/ExternalExecutor.App/Assets');
const contentTypes = { '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript', '.woff2': 'font/woff2', '.ttf': 'font/ttf' };

async function mockDesktop() {
    const listeners = [];
    window.__nativeCalls = [];
    window.__mockState = { clients: [], busy: false, moduleAvailable: false };
    window.__emitNative = value => listeners.forEach(listener => listener({ data: value }));
    window.__replyNative = (id, result = {}, error = null) => window.__emitNative({ id, ok: error === null, result, error });
    window.chrome = window.chrome || {};
    window.chrome.webview = {
        addEventListener: (_, listener) => listeners.push(listener),
        postMessage(message) {
            window.__nativeCalls.push(structuredClone(message));
            queueMicrotask(() => {
                const { id, command, payload } = message;
                if (command === 'ready' || command === 'configure') {
                    if (typeof payload.autoAttach !== 'boolean' || typeof payload.autoRefresh !== 'boolean')
                        return window.__replyNative(id, {}, 'Connection preferences must contain both booleans.');
                    window.__connectionPreferences = structuredClone(payload);
                }
                if (command === 'ready' || command === 'refresh') {
                    window.__emitNative({ type: 'state', ...window.__mockState });
                } else if (command === 'attach' || command === 'detach') {
                    window.__mockState.clients.forEach(client => {
                        if (payload.pid === null || client.processId === payload.pid) client.isAttached = command === 'attach';
                    });
                    window.__emitNative({ type: 'state', ...window.__mockState });
                } else if (command === 'execute' && window.__failExecute) {
                    window.__emitNative({ type: 'log', level: 'error', message: 'Mock native dispatch failure' });
                    return window.__replyNative(id, {}, 'Mock native dispatch failure');
                } else if (command === 'diagnostics') {
                    window.__emitNative({ type: 'log', level: 'error', message: 'API file check: native module not found.' });
                    return window.__replyNative(id, {
                        modulePath: 'C:\\RYN\\QuorumAPI.dll', exists: false, isWindowsX64: false,
                        isCompatible: false, isSupportedHost: true, canLoadOnCurrentHost: false,
                        sha256: null, exports: [], missingExports: ['Attach', 'Execute'],
                        issues: ['Native module not found.']
                    });
                } else if (command === 'openFile') {
                    return window.__replyNative(id, window.__openResult || { cancelled: true });
                } else if (command === 'saveFile') {
                    if (window.__delaySave) { window.__pendingSave = message; return; }
                    return window.__replyNative(id, { cancelled: false, path: payload.path || 'C:\\Scripts\\saved.lua' });
                } else if (command === 'close') {
                    window.__emitNative({ type: 'closing' });
                }
                window.__replyNative(id, { message: `${command} completed` });
            });
        }
    };
}

async function main() {
    const server = http.createServer(async (request, response) => {
        try {
            const pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
            if (pathname === '/favicon.ico') { response.writeHead(204).end(); return; }
            const filename = path.resolve(assets, '.' + (pathname === '/' ? '/index.html' : pathname));
            if (!filename.startsWith(assets + path.sep)) { response.writeHead(403).end(); return; }
            const contents = await fs.readFile(filename);
            response.writeHead(200, { 'Content-Type': contentTypes[path.extname(filename)] || 'application/octet-stream' });
            response.end(contents);
        } catch { response.writeHead(404).end(); }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const url = `http://127.0.0.1:${server.address().port}`;
    let browser;
    const failures = [];
    let checks = 0;
    const check = (condition, message) => { assert.ok(condition, message); checks++; };
    try {
        browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || '/usr/bin/chromium', headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
        const preview = await browser.newContext({ viewport: { width: 1180, height: 780 } });
        const previewPage = await preview.newPage();
        previewPage.on('pageerror', error => failures.push(error.message));
        await previewPage.goto(url);
        check(await previewPage.locator('#execute-btn').isDisabled(), 'Browser preview must disable Execute');
        check(await previewPage.locator('#inject-btn').isDisabled(), 'Browser preview must disable Attach');
        check(await previewPage.locator('#api-diagnostics').isDisabled(), 'Browser preview must disable native API file checks');
        check(await previewPage.locator('#top-status-text').innerText() === 'Desktop preview', 'Browser preview must disclose native unavailable');
        await previewPage.getByRole('button', { name: 'New script tab' }).click();
        check(await previewPage.locator('.script-tab').count() === 2, 'Editor tabs work in browser preview');
        await preview.close();

        const context = await browser.newContext({ viewport: { width: 1180, height: 780 } });
        await context.addInitScript(mockDesktop);
        const page = await context.newPage();
        page.on('pageerror', error => failures.push(error.message));
        page.on('console', message => { if (message.type() === 'error' && !message.text().includes('favicon.ico')) failures.push(message.text()); });
        let acceptDialog = false;
        page.on('dialog', dialog => acceptDialog ? dialog.accept() : dialog.dismiss());
        await page.goto(url);
        await page.waitForFunction(() => document.getElementById('top-status-text').textContent === 'API unavailable');
        const initialReady = await page.evaluate(() => window.__nativeCalls.find(call => call.command === 'ready'));
        check(initialReady.payload.autoAttach === false && initialReady.payload.autoRefresh === true, 'Ready must synchronize default native connection settings');
        check(await page.locator('#inject-btn').isDisabled(), 'Missing API must disable Attach');
        check(await page.locator('#execute-btn').isDisabled(), 'Missing API must disable Execute');

        await page.locator('#nav-settings').click();
        check(await page.locator('#api-diagnostics').isEnabled(), 'API file checks remain available when the module is missing');
        await page.locator('#api-diagnostics').click();
        await page.waitForFunction(() => window.__nativeCalls.some(call => call.command === 'diagnostics'));
        await page.waitForFunction(() => document.getElementById('console-output').textContent.includes('API file check: native module not found.'));
        check(await page.locator('#tab-console').isVisible(), 'API file check opens Console for native diagnostic logs');
        check(await page.evaluate(() => !window.__nativeCalls.some(call => call.command === 'attach' || call.command === 'execute')), 'API file checks must not attach or execute scripts');
        await page.locator('#nav-executor').click();

        await page.evaluate(() => {
            window.__mockState = { clients: [{ processId: 1234, isAttached: false }, { processId: 5678, isAttached: true }], busy: false, moduleAvailable: true };
            window.__emitNative({ type: 'state', ...window.__mockState });
        });
        await page.locator('#client-target').selectOption('1234');
        check(await page.locator('#execute-btn').isDisabled(), 'An unattached selected client must block Execute');
        await page.locator('#inject-btn').click();
        await page.waitForFunction(() => !document.getElementById('execute-btn').disabled);
        const attach = await page.evaluate(() => window.__nativeCalls.findLast(call => call.command === 'attach'));
        check(attach.payload.pid === 1234, 'Attach must use the selected PID');

        const quotedScript = 'print("quote: \' and newline\\n")\n-- exact contents';
        await page.locator('#lua-code-input').fill(quotedScript);
        await page.locator('#execute-btn').click();
        await page.waitForFunction(() => document.getElementById('toast-msg').textContent.startsWith('Script dispatched.'));
        const execute = await page.evaluate(() => window.__nativeCalls.findLast(call => call.command === 'execute'));
        check(execute.payload.pid === 1234 && execute.payload.script === quotedScript, 'Execute must preserve selected PID and exact quoted script');

        await page.evaluate(() => { window.__failExecute = true; });
        await page.locator('#execute-btn').click();
        await page.waitForFunction(() => document.getElementById('toast-msg').textContent === 'Mock native dispatch failure');
        check(await page.locator('#toast').evaluate(element => element.classList.contains('error')), 'Native failure must display an error toast');
        check(!(await page.locator('#toast-msg').innerText()).includes('dispatched'), 'Native failure must not claim successful dispatch');
        await page.evaluate(() => { window.__failExecute = false; });

        await page.evaluate(() => {
            window.__mockState.clients = [{ processId: 5678, isAttached: true }];
            window.__emitNative({ type: 'state', ...window.__mockState });
        });
        check(await page.locator('#client-target').inputValue() === '1234', 'A vanished single-client target must remain selected');
        check((await page.locator('#client-target option:checked').innerText()).includes('Unavailable'), 'Vanished target must be marked unavailable');
        check(await page.locator('#execute-btn').isDisabled(), 'Vanished target must block execution despite another attached client');
        await page.locator('#client-target').selectOption('all');
        check(await page.locator('#execute-btn').isEnabled(), 'All-clients target can execute when one client is attached');

        await page.keyboard.press('Control+n');
        await page.locator('#lua-code-input').fill('print("second tab")');
        const tabCount = await page.locator('.script-tab').count();
        await page.reload();
        await page.waitForFunction(() => document.getElementById('top-status-text').textContent === 'API unavailable');
        check(await page.locator('.script-tab').count() === tabCount, 'Script tabs must persist on reload');
        check(await page.locator('#lua-code-input').inputValue() === 'print("second tab")', 'Active tab contents must recover on reload');

        // Save a tab while the user continues editing a different one.
        await page.evaluate(() => { window.__delaySave = true; });
        await page.keyboard.press('Control+s');
        await page.waitForFunction(() => !!window.__pendingSave);
        await page.keyboard.press('Control+n');
        await page.locator('#lua-code-input').fill('print("third unsaved tab")');
        await page.evaluate(() => {
            window.__replyNative(window.__pendingSave.id, { cancelled: false, path: 'C:\\Scripts\\second.lua' });
            window.__delaySave = false;
        });
        await page.waitForFunction(() => [...document.querySelectorAll('.script-tab')].some(tab => tab.textContent.includes('second.lua')));
        check(await page.locator('#lua-code-input').inputValue() === 'print("third unsaved tab")', 'Saving a background tab must not replace the current document');
        const saved = page.locator('.script-tab').filter({ hasText: 'second.lua' });
        check(!(await saved.innerText()).includes('•'), 'Only the saved tab becomes clean');
        check((await page.locator('.script-tab.active').innerText()).includes('•'), 'The unsaved current tab must remain dirty');

        await page.evaluate(() => { window.__openResult = { cancelled: false, path: 'C:\\Scripts\\opened.lua', content: "print('opened')" }; });
        await page.keyboard.press('Control+o');
        await page.waitForFunction(() => document.getElementById('lua-code-input').value === "print('opened')");
        check((await page.locator('.script-tab.active').innerText()).includes('opened.lua'), 'Open shortcut must activate a new file tab');

        await page.locator('#nav-scripthub').click();
        await page.locator('#sb-search').fill('Infinite Yield');
        check(await page.locator('#scriptblox-grid > div').count() === 1, 'Hub search must filter included entries');
        await page.getByRole('button', { name: 'Load into editor' }).click();
        check((await page.locator('#lua-code-input').inputValue()).includes("game:HttpGet('https://raw.githubusercontent.com/EdgeIY"), 'Hub must safely load the supplied single-quoted snippet');
        check(await page.locator('#tab-executor').isVisible(), 'Loading a hub script must activate the editor');

        await page.locator('#nav-versions').click();
        await page.locator('#mode-v1').click();
        check(await page.locator('html').evaluate(element => element.classList.contains('classic')), 'Classic mode must change the layout');
        check((await page.locator('#active-mode-badge').innerText()).includes('Classic'), 'Layout badge must match the active mode');
        await page.getByRole('button', { name: 'Switch RYN Mode' }).click();
        await page.locator('#mode-v2').click();
        check(!await page.locator('html').evaluate(element => element.classList.contains('classic')), 'Pro mode must restore the full layout');

        await page.locator('#nav-settings').click();
        check(await page.locator('#tab-settings').isVisible(), 'Settings navigation opens the preferences page');
        check(await page.locator('#setting-show-output').count() === 0, 'Desktop settings must omit simulated print output');
        await page.locator('#setting-theme').selectOption('paper');
        check(await page.locator('html').getAttribute('data-theme') === 'paper', 'Paper theme applies immediately');
        check(await page.locator('main').evaluate(element => getComputedStyle(element).backgroundColor) === 'rgb(238, 238, 238)', 'Paper theme must apply to the workspace background');
        await page.locator('#setting-font-size').evaluate(element => {
            element.value = '20';
            element.dispatchEvent(new Event('input', { bubbles: true }));
        });
        await page.locator('#setting-word-wrap').check();
        await page.locator('#setting-line-numbers').uncheck();
        await page.locator('#setting-reduce-motion').check();
        await page.locator('#setting-tab-size').selectOption('8');
        check(await page.locator('#lua-code-input').evaluate(element => element.style.fontSize) === '20px', 'Font size changes apply to the editor');
        check(await page.locator('#lua-code-input').getAttribute('wrap') === 'soft', 'Word wrap enables textarea wrapping');
        check(await page.locator('#line-numbers').evaluate(element => element.style.display) === 'none', 'Line number setting hides the gutter');
        check(await page.locator('html').evaluate(element => element.classList.contains('reduce-motion')), 'Reduce motion setting applies to the workspace');
        await page.locator('#setting-auto-refresh').uncheck();
        await page.locator('#setting-auto-attach').check();
        await page.waitForFunction(() => window.__connectionPreferences?.autoAttach === true && window.__connectionPreferences?.autoRefresh === false);
        check(true, 'Connection toggles send configure with both current booleans');
        await page.locator('#nav-executor').click();
        await page.locator('#lua-code-input').fill('x');
        await page.locator('#lua-code-input').press('End');
        await page.locator('#lua-code-input').press('Tab');
        check(await page.locator('#lua-code-input').inputValue() === 'x        ', 'Editor Tab uses the selected indentation width');
        await page.reload();
        await page.waitForFunction(() => document.getElementById('top-status-text').textContent === 'API unavailable');
        check(await page.locator('html').getAttribute('data-theme') === 'paper', 'Theme preference survives reload');
        const restoredReady = await page.evaluate(() => window.__nativeCalls.find(call => call.command === 'ready'));
        check(restoredReady.payload.autoAttach === true && restoredReady.payload.autoRefresh === false, 'Ready must restore saved connection preferences');
        await page.locator('#nav-settings').click();
        const beforeReset = await page.locator('#lua-code-input').inputValue();
        await page.locator('#settings-reset').click();
        await page.waitForFunction(() => window.__connectionPreferences?.autoAttach === false && window.__connectionPreferences?.autoRefresh === true);
        check(await page.locator('html').getAttribute('data-theme') === 'midnight', 'Reset restores the default monochrome theme');
        check(await page.locator('#lua-code-input').inputValue() === beforeReset, 'Reset settings preserves the open script');
        check(await page.locator('#setting-auto-refresh').isChecked() && !(await page.locator('#setting-auto-attach').isChecked()), 'Reset synchronizes native connection defaults');
        await page.locator('#nav-executor').click();

        await page.evaluate(() => window.__emitNative({ type: 'closing' }));
        await page.waitForFunction(() => window.__nativeCalls.some(call => call.command === 'confirmClose'));
        check(await page.evaluate(() => window.__nativeCalls.findLast(call => call.command === 'confirmClose').payload.allow) === false, 'Cancelling close must preserve unsaved workspace');
        acceptDialog = true;
        await page.evaluate(() => window.__emitNative({ type: 'closing' }));
        await page.waitForFunction(() => window.__nativeCalls.findLast(call => call.command === 'confirmClose')?.payload.allow === true);
        check(true, 'Accepting close must notify the host');

        await page.keyboard.press('F5');
        await page.waitForFunction(() => window.__nativeCalls.some(call => call.command === 'refresh'));
        check(true, 'F5 refreshes native clients instead of reloading the page');
        await page.evaluate(() => {
            window.__mockState = { clients: [{ processId: 1234, isAttached: true }, { processId: 5678, isAttached: false }], busy: false, moduleAvailable: true };
            window.__emitNative({ type: 'state', ...window.__mockState });
            launchVersion('v2', false);
            switchTab('executor');
        });
        await page.locator('#client-target').selectOption('1234');
        await page.locator('.script-tab').first().click();
        await fs.mkdir(path.join(root, 'artifacts'), { recursive: true });
        await page.screenshot({ path: path.join(root, 'artifacts/ryn-preview.png') });
        await page.locator('#nav-settings').click();
        await page.locator('#tab-settings').evaluate(element => { element.scrollTop = 0; });
        await page.screenshot({ path: path.join(root, 'artifacts/ryn-settings.png') });
        check(failures.length === 0, `No browser runtime errors: ${failures.join('; ')}`);
        console.log(`${checks} browser checks passed. Screenshots: artifacts/ryn-preview.png, artifacts/ryn-settings.png`);
        await context.close();
    } finally {
        if (browser) await browser.close();
        await new Promise(resolve => server.close(resolve));
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
