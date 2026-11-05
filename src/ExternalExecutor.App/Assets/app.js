'use strict';

(() => {
    const $ = id => document.getElementById(id);
    const bridge = window.chrome?.webview;
    const nativeCommands = new Set(['ready', 'refresh', 'attach', 'detach', 'execute', 'openRoblox', 'updateApi', 'diagnostics']);
    const pending = new Map();
    const storageKey = 'ryn.workspace.v1';
    let requestId = 0;
    let toastTimeout;
    let persistenceWarning = false;
    let apiState = { clients: [], busy: false, moduleAvailable: false };
    let chosenPid = null;
    let tabs = [];
    let activeTabId;
    let mode = 'v2';
    let logCount = 0;

    const snippets = [
        { id: 'sb-1', title: 'Hoho Hub V3 | Auto Farm & Raid', game: 'Blox Fruits', author: 'acaye', script: 'loadstring(game:HttpGet("https://raw.githubusercontent.com/acaye/HohoHub/main/BloxFruits.lua"))()' },
        { id: 'sb-2', title: 'Redz Hub Official', game: 'Blox Fruits', author: 'redz9999', script: 'loadstring(game:HttpGet("https://raw.githubusercontent.com/REDZHUB/BloxFruits/main/redzhub.lua"))()' },
        { id: 'sb-3', title: 'Infinite Yield Admin', game: 'Universal', author: 'Edge', script: "loadstring(game:HttpGet('https://raw.githubusercontent.com/EdgeIY/infiniteyield/master/source'))()" }
    ];

    const currentTab = () => tabs.find(tab => tab.id === activeTabId);
    const isBusy = () => apiState.busy || [...pending.values()].some(item => nativeCommands.has(item.command));
    const selectedClients = () => chosenPid === null ? apiState.clients : apiState.clients.filter(client => client.processId === chosenPid);
    const isAttached = () => selectedClients().some(client => client.isAttached);
    const connectionPreferences = () => ({
        autoAttach: window.RynSettings?.get().autoAttach === true,
        autoRefresh: window.RynSettings?.get().autoRefresh !== false
    });

    function request(command, payload = {}) {
        if (!bridge) return Promise.reject(new Error('This action requires the RYN Windows desktop app.'));
        const id = String(++requestId);
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                pending.delete(id);
                renderState();
                reject(new Error('The desktop API has not responded. Check the Console before retrying.'));
            }, 120000);
            pending.set(id, { command, resolve, reject, timer });
            renderState();
            try { bridge.postMessage({ id, command, payload }); }
            catch (error) {
                clearTimeout(timer);
                pending.delete(id);
                renderState();
                reject(error);
            }
        });
    }

    async function run(command, payload, success) {
        try {
            const result = await request(command, payload);
            if (success) showToast(typeof success === 'function' ? success(result) : success);
            return result;
        } catch (error) {
            showToast(error.message || 'The action failed.', 'error');
            // The host also records native failures, while preview errors need a local entry.
            if (!bridge) addLog(error.message, 'error');
            return null;
        }
    }

    function addLog(message, level = 'info') {
        const line = document.createElement('div');
        line.className = level === 'error' ? 'text-neutral-400' : 'text-zinc-300';
        line.textContent = `[${new Date().toLocaleTimeString()}] [${level.toUpperCase()}] ${message}`;
        $('console-output').appendChild(line);
        while ($('console-output').children.length > 400) $('console-output').firstChild.remove();
        $('console-output').scrollTop = $('console-output').scrollHeight;
        $('console-badge').textContent = String(++logCount);
    }

    function showToast(message, level = 'info') {
        clearTimeout(toastTimeout);
        $('toast-msg').textContent = message;
        $('toast').classList.toggle('error', level === 'error');
        $('toast-icon').className = level === 'error' ? 'fa-solid fa-circle-exclamation' : 'fa-solid fa-circle-check text-neutral-400';
        $('toast').classList.remove('translate-y-16', 'opacity-0');
        toastTimeout = setTimeout(() => $('toast').classList.add('translate-y-16', 'opacity-0'), 4000);
    }

    function renderState() {
        const busy = isBusy();
        const attached = isAttached();
        let text = 'Not attached';
        let statusClass = 'status-idle';
        if (!bridge) text = 'Desktop preview';
        else if (busy) { text = 'Working…'; statusClass = 'status-busy'; }
        else if (!apiState.moduleAvailable) { text = 'API unavailable'; statusClass = 'status-error'; }
        else if (attached) { text = 'Attached'; statusClass = 'status-attached'; }
        $('top-status-text').textContent = text;
        $('footer-status-text').textContent = text;
        for (const id of ['top-status-dot', 'footer-status-dot']) $(id).className = `w-2 h-2 rounded-full ${statusClass}`;
        $('client-summary').textContent = bridge
            ? `${apiState.clients.length} client(s) · ${apiState.clients.filter(client => client.isAttached).length} attached`
            : 'Open the Windows app to connect';
        document.querySelectorAll('[data-native]').forEach(button => { button.disabled = !bridge || busy; });
        document.querySelectorAll('[data-window-action]').forEach(button => { button.disabled = !bridge; });
        $('inject-btn').disabled = !bridge || busy || !apiState.moduleAvailable || selectedClients().length === 0;
        $('inject-text').textContent = 'Attach RYN';
        $('detach-btn').disabled = !bridge || busy || !attached;
        $('execute-btn').disabled = !bridge || busy || !attached || !currentTab()?.content.trim();
        $('client-target').disabled = busy || !bridge;
    }

    function updateState(state) {
        apiState = {
            busy: state.busy === true,
            moduleAvailable: state.moduleAvailable === true,
            clients: Array.isArray(state.clients) ? state.clients.filter(client => Number.isSafeInteger(client.processId) && client.processId > 0).map(client => ({ processId: client.processId, isAttached: client.isAttached === true })) : []
        };
        const target = $('client-target');
        target.replaceChildren(new Option('All clients', 'all'));
        for (const client of apiState.clients)
            target.add(new Option(`PID ${client.processId} · ${client.isAttached ? 'Attached' : 'Not attached'}`, String(client.processId)));
        if (chosenPid !== null && !apiState.clients.some(client => client.processId === chosenPid))
            target.add(new Option(`PID ${chosenPid} · Unavailable`, String(chosenPid)));
        target.value = chosenPid === null ? 'all' : String(chosenPid);
        renderState();
    }

    function persist() {
        try {
            if (window.RynSettings?.get().restoreWorkspace === false) localStorage.removeItem(storageKey);
            else localStorage.setItem(storageKey, JSON.stringify({ tabs, activeTabId, mode }));
        }
        catch {
            if (!persistenceWarning) {
                persistenceWarning = true;
                addLog('Workspace recovery storage is full or unavailable. Save scripts to keep your changes.', 'error');
            }
        }
    }

    function makeTab(name, content = '', path = null, dirty = false) {
        return { id: crypto.randomUUID(), name, content, path, dirty };
    }

    function restoreWorkspace() {
        try {
            const recoveryEnabled = window.RynSettings?.get().restoreWorkspace !== false;
            if (!recoveryEnabled) localStorage.removeItem(storageKey);
            const saved = recoveryEnabled ? JSON.parse(localStorage.getItem(storageKey)) : null;
            if (saved && Array.isArray(saved.tabs)) {
                tabs = saved.tabs.filter(tab => typeof tab.id === 'string' && typeof tab.name === 'string' && typeof tab.content === 'string').map(tab => ({ id: tab.id, name: tab.name, content: tab.content, path: typeof tab.path === 'string' ? tab.path : null, dirty: tab.dirty === true }));
                activeTabId = tabs.some(tab => tab.id === saved.activeTabId) ? saved.activeTabId : tabs[0]?.id;
                mode = saved.mode === 'v1' ? 'v1' : 'v2';
            }
        } catch { /* An invalid recovery snapshot must not prevent startup. */ }
        if (tabs.length === 0) {
            tabs.push(makeTab('ryn_main.lua', '-- RYN Execution Workspace\nprint("[RYN] Hello from your workspace.")\n'));
            activeTabId = tabs[0].id;
        }
    }

    function renderTabs() {
        const container = $('script-tabs-container');
        container.replaceChildren();
        for (const tab of tabs) {
            const tabElement = document.createElement('button');
            tabElement.className = `script-tab flex items-center gap-2 px-3 py-1.5 rounded-t-md text-xs font-mono ${tab.id === activeTabId ? 'active' : ''}`;
            tabElement.title = tab.path || tab.name;
            tabElement.setAttribute('role', 'tab');
            tabElement.setAttribute('aria-selected', String(tab.id === activeTabId));
            const icon = document.createElement('i');
            icon.className = 'fa-regular fa-file-code';
            const name = document.createElement('span');
            name.textContent = tab.name + (tab.dirty ? ' •' : '');
            const close = document.createElement('span');
            close.className = 'tab-close';
            close.textContent = '×';
            close.title = 'Close tab';
            close.addEventListener('click', event => { event.stopPropagation(); closeTab(tab.id); });
            tabElement.append(icon, name, close);
            tabElement.addEventListener('click', () => activateScriptTab(tab.id));
            container.appendChild(tabElement);
        }
        const tab = currentTab();
        $('document-status').textContent = tab ? `${tab.dirty ? 'Unsaved changes' : 'UTF-8'} · ${tab.content.length.toLocaleString()} chars` : '';
        document.title = `${tab?.name || 'Workspace'}${tab?.dirty ? ' *' : ''} — RYN Executor Suite`;
        renderState();
    }

    function activateScriptTab(id) {
        activeTabId = id;
        $('lua-code-input').value = currentTab().content;
        $('lua-code-input').scrollTop = 0;
        $('line-numbers').scrollTop = 0;
        updateLineNumbers();
        renderTabs();
        persist();
    }

    function addNewTab(content = '', name = null) {
        const tab = makeTab(name || `script_${tabs.length + 1}.lua`, content, null, content.length > 0);
        tabs.push(tab);
        activateScriptTab(tab.id);
        switchTab('executor');
        $('lua-code-input').focus();
    }

    function closeTab(id) {
        const tab = tabs.find(item => item.id === id);
        if (tab.dirty && !window.confirm(`Discard unsaved changes to ${tab.name} and close this tab?`)) return;
        const index = tabs.findIndex(item => item.id === id);
        tabs.splice(index, 1);
        if (tabs.length === 0) tabs.push(makeTab('ryn_main.lua'));
        activateScriptTab(id === activeTabId ? tabs[Math.min(index, tabs.length - 1)].id : activeTabId);
    }

    function switchTab(tabId) {
        if (!['executor', 'scripthub', 'versions', 'console', 'settings'].includes(tabId)) return;
        document.querySelectorAll('.tab-content').forEach(element => element.classList.toggle('hidden', element.id !== `tab-${tabId}`));
        document.querySelectorAll('.nav-btn').forEach(button => button.classList.toggle('active', button.id === `nav-${tabId}`));
    }

    function onEditorInput() {
        const tab = currentTab();
        tab.content = $('lua-code-input').value;
        tab.dirty = true;
        updateLineNumbers();
        renderTabs();
        persist();
    }

    function updateLineNumbers() {
        const input = $('lua-code-input');
        const numbers = $('line-numbers');
        const lines = input.value.split('\n');
        const count = Math.max(lines.length, 8);
        if (window.RynSettings?.get().wordWrap && input.clientWidth > 0) {
            // Measure logical line heights with the editor's actual wrapping rules,
            // so gutter numbers stay aligned when a long line spans visual rows.
            const style = window.getComputedStyle(input);
            const measure = document.createElement('div');
            Object.assign(measure.style, {
                position: 'fixed', left: '-100000px', top: '0', visibility: 'hidden',
                width: `${input.clientWidth}px`, padding: '0', margin: '0', border: '0',
                whiteSpace: 'pre-wrap', overflowWrap: 'break-word', font: style.font,
                lineHeight: style.lineHeight, letterSpacing: style.letterSpacing,
                tabSize: style.tabSize
            });
            document.body.appendChild(measure);
            const rows = document.createDocumentFragment();
            for (let index = 0; index < count; index++) {
                measure.textContent = lines[index] || '\u200b';
                const row = document.createElement('div');
                row.textContent = String(index + 1);
                row.style.height = `${measure.getBoundingClientRect().height}px`;
                rows.appendChild(row);
            }
            measure.remove();
            numbers.replaceChildren(rows);
        } else {
            numbers.textContent = Array.from({ length: count }, (_, i) => i + 1).join('\n');
        }
        numbers.scrollTop = input.scrollTop;
    }

    async function openScript() {
        const result = await run('openFile');
        if (!result || result.cancelled) return;
        const existing = tabs.find(tab => tab.path === result.path);
        if (existing) { activateScriptTab(existing.id); switchTab('executor'); return; }
        const tab = makeTab(result.path.split(/[\\/]/).pop(), result.content || '', result.path);
        tabs.push(tab);
        activateScriptTab(tab.id);
        switchTab('executor');
    }

    async function saveScript(saveAs = false) {
        const tab = currentTab();
        const content = tab.content;
        const result = await run('saveFile', { content, path: tab.path, saveAs });
        if (!result || result.cancelled) return;
        tab.path = result.path;
        tab.name = result.path.split(/[\\/]/).pop();
        if (tab.content === content) tab.dirty = false;
        renderTabs();
        persist();
        showToast(`Saved ${tab.name}.`);
    }

    async function executeCode() {
        if (isBusy()) return;
        const script = currentTab().content;
        if (!script.trim()) return showToast('Editor is empty.', 'error');
        if (!isAttached()) return showToast('Attach the selected Roblox client before executing.', 'error');
        await run('execute', { pid: chosenPid, script }, 'Script dispatched. Check Roblox’s client log for output.');
    }

    async function toggleInject() {
        if (isBusy()) return;
        await run('attach', { pid: chosenPid }, 'Attach request succeeded. Client states have been refreshed.');
    }

    async function detachClient() {
        if (isBusy()) return;
        await run('detach', { pid: chosenPid }, 'Detach request completed.');
    }

    function clearEditor() {
        if (!$('lua-code-input').value) return;
        if (!window.confirm('Clear the current script?')) return;
        $('lua-code-input').value = '';
        onEditorInput();
        $('lua-code-input').focus();
    }

    async function copyEditorCode() {
        try { await navigator.clipboard.writeText($('lua-code-input').value); showToast('Copied script code.'); }
        catch { showToast('Clipboard unavailable. Select the script and use Ctrl+C.', 'error'); }
    }

    function renderScriptBloxCards(items) {
        const grid = $('scriptblox-grid');
        grid.replaceChildren();
        for (const item of items) {
            const card = document.createElement('div');
            card.className = 'bg-ryn-card border border-ryn-border rounded-xl p-3.5 flex flex-col justify-between';
            const details = document.createElement('div');
            details.className = 'space-y-1';
            const game = document.createElement('span');
            game.className = 'text-neutral-400 text-[10px] bg-neutral-500/10 px-2 py-0.5 rounded font-mono';
            game.textContent = item.game;
            const title = document.createElement('h3');
            title.className = 'font-bold text-zinc-100 text-sm mt-1';
            title.textContent = item.title;
            const author = document.createElement('p');
            author.className = 'text-xs text-zinc-500';
            author.textContent = `By ${item.author} · Supplied shortcut`;
            details.append(game, title, author);
            const load = document.createElement('button');
            load.className = 'mt-3 w-full py-1.5 bg-neutral-500 hover:bg-neutral-400 text-zinc-950 font-bold rounded-md text-xs flex items-center justify-center gap-1 shadow transition';
            load.textContent = 'Load into editor';
            load.addEventListener('click', () => { addNewTab(item.script, `${item.id}.lua`); showToast('Loaded into a new tab. Review the script before executing.'); });
            card.append(details, load);
            grid.appendChild(card);
        }
        if (items.length === 0) {
            const message = document.createElement('p');
            message.className = 'text-sm text-zinc-500 col-span-full';
            message.textContent = 'No included scripts match your search.';
            grid.appendChild(message);
        }
    }

    function filterScriptBlox() {
        const query = $('sb-search').value.trim().toLowerCase();
        renderScriptBloxCards(snippets.filter(item => `${item.title} ${item.game} ${item.author}`.toLowerCase().includes(query)));
    }

    function launchVersion(version, notify = true) {
        mode = version === 'v1' ? 'v1' : 'v2';
        document.documentElement.classList.toggle('classic', mode === 'v1');
        $('active-mode-badge').textContent = mode === 'v1' ? 'v1.0 Classic' : 'v2.0 Pro';
        $('mode-v1').textContent = mode === 'v1' ? 'Active layout' : 'Use Classic layout';
        $('mode-v2').textContent = mode === 'v2' ? 'Active layout' : 'Use Pro layout';
        $('mode-card-v1').classList.toggle('active', mode === 'v1');
        $('mode-card-v2').classList.toggle('active', mode === 'v2');
        persist();
        if (notify) { switchTab('executor'); showToast(`Using ${mode === 'v1' ? 'Classic' : 'Pro'} layout.`); }
    }

    function confirmClose() {
        persist();
        const dirty = tabs.filter(tab => tab.dirty);
        const recovery = window.RynSettings?.get().restoreWorkspace === false
            ? 'Workspace recovery is off. Use Save to keep script files.'
            : 'Your workspace is kept for recovery; use Save to write script files.';
        const allow = dirty.length === 0 || window.confirm(`${dirty.length} script tab(s) have unsaved changes. Close RYN? ${recovery}`);
        run('confirmClose', { allow });
    }

    Object.assign(window, {
        switchTab, addNewTab, activateScriptTab, executeCode, toggleInject, detachClient,
        clearEditor, copyEditorCode, updateLineNumbers, filterScriptBlox, launchVersion,
        openScript, saveScript,
        refreshClients: () => run('refresh', {}, 'Client list refreshed.'),
        openRoblox: () => run('openRoblox', {}, 'Roblox launch requested.'),
        updateApi: () => run('updateApi', {}, 'API updater completed. Restart RYN if the module was replaced.'),
        checkApi: async () => {
            switchTab('console');
            return run('diagnostics', {}, 'API file check complete. See Console.');
        },
        toggleMinimize: () => run('minimize'),
        toggleMaximize: () => run('maximize'),
        closeWindow: () => run('close')
    });

    restoreWorkspace();
    activateScriptTab(activeTabId);
    launchVersion(mode, false);
    renderScriptBloxCards(snippets);
    $('client-target').addEventListener('change', event => {
        chosenPid = event.target.value === 'all' ? null : Number(event.target.value);
        renderState();
    });
    $('lua-code-input').addEventListener('input', onEditorInput);
    $('lua-code-input').addEventListener('scroll', () => { $('line-numbers').scrollTop = $('lua-code-input').scrollTop; });
    $('lua-code-input').addEventListener('keydown', event => {
        if (event.key !== 'Tab') return;
        event.preventDefault();
        const input = event.target;
        input.setRangeText(' '.repeat(window.RynSettings?.get().tabSize || 4), input.selectionStart, input.selectionEnd, 'end');
        onEditorInput();
    });
    document.addEventListener('keydown', event => {
        const ctrl = event.ctrlKey || event.metaKey;
        if (ctrl && event.key.toLowerCase() === 'n') { event.preventDefault(); addNewTab(); }
        else if (ctrl && event.key.toLowerCase() === 'o') { event.preventDefault(); openScript(); }
        else if (ctrl && event.key.toLowerCase() === 's') { event.preventDefault(); saveScript(event.shiftKey); }
        else if (ctrl && event.key === 'Enter') { event.preventDefault(); executeCode(); }
        else if (event.key === 'F5') { event.preventDefault(); window.refreshClients(); }
    });
    document.addEventListener('ryn-settings-change', event => {
        if (event.detail.key === 'restoreWorkspace' || event.detail.key === 'reset') persist();
        if (bridge && ['autoAttach', 'autoRefresh', 'reset'].includes(event.detail.key))
            run('configure', connectionPreferences());
    });
    if (typeof ResizeObserver !== 'undefined') {
        const editorResizeObserver = new ResizeObserver(() => updateLineNumbers());
        editorResizeObserver.observe($('lua-code-input'));
    }
    document.fonts?.ready.then(updateLineNumbers);
    $('titlebar').addEventListener('mousedown', event => {
        if (!bridge || event.button !== 0 || event.target.closest('button,input,select,a')) return;
        run('drag');
    });
    $('titlebar').addEventListener('dblclick', event => {
        if (bridge && !event.target.closest('button,input,select,a')) run('maximize');
    });

    if (bridge) {
        document.documentElement.classList.add('native-desktop');
        bridge.addEventListener('message', event => {
            let message = event.data;
            if (typeof message === 'string') { try { message = JSON.parse(message); } catch { return; } }
            if (!message || typeof message !== 'object') return;
            if (message.type === 'state') { updateState(message); return; }
            if (message.type === 'log') { addLog(String(message.message || ''), message.level); return; }
            if (message.type === 'closing') { confirmClose(); return; }
            const item = pending.get(String(message.id));
            if (!item) return;
            clearTimeout(item.timer);
            pending.delete(String(message.id));
            renderState();
            if (message.ok) item.resolve(message.result || {});
            else item.reject(new Error(message.error || 'The desktop API returned an error.'));
        });
        run('ready', connectionPreferences());
    } else {
        addLog('Browser preview. Native actions are available in the Windows desktop app.');
        renderState();
    }
    addLog('Execute reports dispatch only. Script output appears in Roblox’s client log.');
})();
