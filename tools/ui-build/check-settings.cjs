// Node DOM-stub checks for preference state and app integration. No browser or DLL.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../../src/ExternalExecutor.App/Assets');
const preferenceKey = 'ryn.preferences.v1';
const workspaceKey = 'ryn.workspace.v1';

function harness(seed = {}, storageDenied = false, loadApp = false, native = false) {
    const storage = new Map(Object.entries(seed));
    const elements = new Map();
    const emitted = [];
    const documentEvents = new Map();
    const makeElement = () => ({
        value: '', checked: false, textContent: '', children: [], dataset: {}, events: {},
        scrollTop: 0, scrollHeight: 0, clientWidth: 0, selectionStart: 0, selectionEnd: 0,
        style: { setProperty(name, value) { this[name] = value; } },
        classList: { values: new Set(), toggle(name, value) { if (value) this.values.add(name); else this.values.delete(name); }, remove() {}, add() {} },
        addEventListener(name, listener) { this.events[name] = listener; },
        appendChild(child) { this.children.push(child); }, append(...children) { this.children.push(...children); }, add(child) { this.children.push(child); },
        replaceChildren(...children) { this.children = children; }, setAttribute() {}, focus() {},
        setRangeText(text, start, end) { this.value = this.value.slice(0, start) + text + this.value.slice(end); }
    });
    const element = id => {
        if (!elements.has(id)) elements.set(id, makeElement());
        return elements.get(id);
    };
    const document = {
        documentElement: makeElement(), body: makeElement(),
        getElementById: element, createElement: makeElement,
        createDocumentFragment: makeElement, querySelectorAll: () => [],
        addEventListener(name, listener) {
            if (!documentEvents.has(name)) documentEvents.set(name, []);
            documentEvents.get(name).push(listener);
        },
        dispatchEvent(event) {
            emitted.push(event);
            for (const listener of documentEvents.get(event.type) || []) listener(event);
        }
    };
    const window = { confirm: () => false };
    const calls = [];
    if (native) {
        const nativeListeners = [];
        window.chrome = { webview: {
            addEventListener(_type, listener) { nativeListeners.push(listener); },
            postMessage(message) {
                calls.push(message);
                queueMicrotask(() => {
                    const response = { id: message.id, ok: true, result: {} };
                    for (const listener of nativeListeners) listener({ data: response });
                });
            }
        } };
    }
    const context = {
        window, document, crypto, setTimeout, clearTimeout,
        localStorage: {
            getItem(key) { if (storageDenied) throw new Error('Storage denied'); return storage.get(key) ?? null; },
            setItem(key, value) { if (storageDenied) throw new Error('Storage denied'); storage.set(key, value); },
            removeItem(key) { if (storageDenied) throw new Error('Storage denied'); storage.delete(key); }
        },
        CustomEvent: class CustomEvent { constructor(type, options) { this.type = type; this.detail = options.detail; } }
    };
    vm.createContext(context);
    vm.runInContext(fs.readFileSync(path.join(root, 'settings.js'), 'utf8'), context);
    if (loadApp) vm.runInContext(fs.readFileSync(path.join(root, 'app.js'), 'utf8'), context);
    return { window, document, element, storage, emitted, calls, settings: window.RynSettings };
}

let count = 0;
function check(name, action) {
    action();
    console.log(`PASS ${name}`);
    count++;
}

check('defaults produce the expected editor and theme', () => {
    const h = harness();
    assert.equal(h.settings.get().theme, 'midnight');
    assert.equal(h.document.documentElement.dataset.theme, 'midnight');
    assert.equal(h.element('lua-code-input').wrap, 'off');
    assert.equal(h.element('lua-code-input').style.fontSize, '14px');
    assert.equal(h.element('line-numbers').style.lineHeight, h.element('lua-code-input').style.lineHeight);
});
check('valid saved preferences restore while malformed fields fall back', () => {
    const h = harness({ [preferenceKey]: JSON.stringify({ theme: 'paper', fontSize: 18, tabSize: 8, lineNumbers: false, autoRefresh: 'yes', surprise: true }) });
    assert.equal(h.settings.get().theme, 'paper');
    assert.equal(h.settings.get().fontSize, 18);
    assert.equal(h.settings.get().autoRefresh, true);
    assert.equal(h.element('line-numbers').style.display, 'none');
    assert.equal(h.settings.get().surprise, undefined);
});
check('invalid preferences and keys are rejected without changing state', () => {
    const h = harness();
    assert.equal(h.settings.update('theme', 'orange'), false);
    assert.equal(h.settings.update('fontSize', 40), false);
    assert.equal(h.settings.update('tabSize', 3), false);
    assert.equal(h.settings.update('lineNumbers', 'true'), false);
    assert.equal(h.settings.update('__proto__', {}), false);
    assert.equal(h.settings.get().fontSize, 14);
    assert.equal(Object.isFrozen(h.settings.get()), true);
});
check('slider input applies immediately and persists for a later visit', () => {
    const h = harness();
    h.element('setting-font-size').value = '22';
    h.element('setting-font-size').events.input();
    assert.equal(h.element('lua-code-input').style.fontSize, '22px');
    assert.equal(h.element('setting-font-size-value').textContent, '22 px');
    const reloaded = harness(Object.fromEntries(h.storage));
    assert.equal(reloaded.settings.get().fontSize, 22);
});
check('checkbox changes apply wrap, motion, and gutter visibility', () => {
    const h = harness();
    h.settings.update('wordWrap', true);
    h.settings.update('reduceMotion', true);
    h.settings.update('lineNumbers', false);
    assert.equal(h.element('lua-code-input').wrap, 'soft');
    assert.equal(h.element('lua-code-input').style.whiteSpace, 'pre-wrap');
    assert.equal(h.document.documentElement.classList.values.has('reduce-motion'), true);
    assert.equal(h.element('line-numbers').style.display, 'none');
    assert.equal(h.emitted.findLast(e => e.type === 'ryn-settings-change').detail.key, 'lineNumbers');
});
check('reset restores preferences and leaves saved scripts untouched', () => {
    const h = harness({ [workspaceKey]: 'keep this workspace' });
    h.settings.update('theme', 'graphite');
    h.settings.update('autoAttach', true);
    h.settings.reset();
    assert.equal(h.settings.get().theme, 'midnight');
    assert.equal(h.settings.get().autoAttach, false);
    assert.equal(h.storage.get(workspaceKey), 'keep this workspace');
});
check('storage denial preserves usable settings and reports the limit', () => {
    const h = harness({}, true);
    assert.equal(h.settings.update('theme', 'paper'), true);
    assert.equal(h.document.documentElement.dataset.theme, 'paper');
    assert.match(h.element('settings-save-status').textContent, /storage is unavailable/);
});
check('corrupt stored JSON does not prevent startup', () => {
    const h = harness({ [preferenceKey]: '{not json' });
    assert.equal(h.settings.get().theme, 'midnight');
    assert.equal(h.element('settings-save-status').textContent, 'Saved on this device');
});
check('workspace recovery can be disabled without clearing open scripts', () => {
    const savedWorkspace = { tabs: [{ id: 'saved', name: 'saved.lua', content: 'print("kept")', path: null, dirty: true }], activeTabId: 'saved', mode: 'v2' };
    const h = harness({ [workspaceKey]: JSON.stringify(savedWorkspace) }, false, true);
    assert.equal(h.element('lua-code-input').value, 'print("kept")');
    h.settings.update('restoreWorkspace', false);
    assert.equal(h.storage.has(workspaceKey), false);
    assert.equal(h.element('lua-code-input').value, 'print("kept")');
    h.element('lua-code-input').value = 'print("edited")';
    h.element('lua-code-input').events.input();
    assert.equal(h.storage.has(workspaceKey), false);
    h.settings.update('restoreWorkspace', true);
    assert.equal(JSON.parse(h.storage.get(workspaceKey)).tabs[0].content, 'print("edited")');
});
check('disabled recovery ignores old workspace on startup', () => {
    const h = harness({
        [preferenceKey]: JSON.stringify({ restoreWorkspace: false }),
        [workspaceKey]: JSON.stringify({ tabs: [{ id: 'old', name: 'old.lua', content: 'old', dirty: false }], activeTabId: 'old' })
    }, false, true);
    assert.notEqual(h.element('lua-code-input').value, 'old');
    assert.equal(h.storage.has(workspaceKey), false);
});
check('editor Tab respects the selected indentation width', () => {
    const h = harness({}, false, true);
    h.settings.update('tabSize', 8);
    const editor = h.element('lua-code-input');
    editor.value = 'x';
    editor.selectionStart = editor.selectionEnd = 1;
    editor.events.keydown({ key: 'Tab', target: editor, preventDefault() {} });
    assert.equal(editor.value, 'x        ');
});
check('native ready and configure synchronize both connection preferences', () => {
    const h = harness({}, false, true, true);
    const ready = h.calls.find(call => call.command === 'ready');
    assert.equal(ready.payload.autoAttach, false);
    assert.equal(ready.payload.autoRefresh, true);
    h.settings.update('autoRefresh', false);
    h.settings.update('autoAttach', true);
    const configured = h.calls.findLast(call => call.command === 'configure');
    assert.equal(configured.payload.autoAttach, true);
    assert.equal(configured.payload.autoRefresh, false);
    const configureCount = h.calls.filter(call => call.command === 'configure').length;
    h.settings.update('theme', 'paper');
    assert.equal(h.calls.filter(call => call.command === 'configure').length, configureCount);
    h.settings.reset();
    const reset = h.calls.findLast(call => call.command === 'configure');
    assert.equal(reset.payload.autoAttach, false);
    assert.equal(reset.payload.autoRefresh, true);
    assert.equal(h.settings.get().showOutput, undefined);
});
console.log(`${count} settings checks passed.`);
