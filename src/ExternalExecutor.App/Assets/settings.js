'use strict';

(() => {
    const storageKey = 'ryn.preferences.v1';
    const defaults = Object.freeze({
        theme: 'midnight', reduceMotion: false, fontSize: 14, lineNumbers: true,
        wordWrap: false, tabSize: 4, restoreWorkspace: true, autoRefresh: true, autoAttach: false
    });
    const controls = {
        theme: 'setting-theme', reduceMotion: 'setting-reduce-motion', fontSize: 'setting-font-size',
        lineNumbers: 'setting-line-numbers', wordWrap: 'setting-word-wrap', tabSize: 'setting-tab-size',
        restoreWorkspace: 'setting-restore-workspace', autoRefresh: 'setting-auto-refresh', autoAttach: 'setting-auto-attach'
    };
    const $ = id => document.getElementById(id);
    const valid = (key, value) => {
        if (key === 'theme') return ['midnight', 'graphite', 'paper'].includes(value);
        if (key === 'fontSize') return Number.isInteger(value) && value >= 12 && value <= 22;
        if (key === 'tabSize') return [2, 4, 8].includes(value);
        return Object.hasOwn(defaults, key) && typeof value === 'boolean';
    };
    let settings = { ...defaults };
    let storageAvailable = true;
    try {
        const raw = localStorage.getItem(storageKey);
        let saved;
        try { saved = JSON.parse(raw); } catch { saved = null; }
        if (saved && typeof saved === 'object' && !Array.isArray(saved))
            for (const key of Object.keys(defaults))
                if (Object.hasOwn(saved, key) && valid(key, saved[key])) settings[key] = saved[key];
    } catch { storageAvailable = false; }

    const snapshot = () => Object.freeze({ ...settings });
    function showStorageStatus() {
        const status = $('settings-save-status');
        if (status) status.textContent = storageAvailable
            ? 'Saved on this device'
            : 'Applied for this visit. Device storage is unavailable.';
    }
    function save() {
        try {
            localStorage.setItem(storageKey, JSON.stringify(settings));
            storageAvailable = true;
        } catch { storageAvailable = false; }
        showStorageStatus();
    }
    function apply() {
        const root = document.documentElement;
        root.dataset.theme = settings.theme;
        root.classList.toggle('reduce-motion', settings.reduceMotion);
        root.style.setProperty('--editor-font-size', `${settings.fontSize}px`);
        const editor = $('lua-code-input');
        const lineNumbers = $('line-numbers');
        const lineHeight = `${Math.round(settings.fontSize * 1.7)}px`;
        if (editor) {
            editor.style.fontSize = `${settings.fontSize}px`;
            editor.style.lineHeight = lineHeight;
            editor.style.tabSize = String(settings.tabSize);
            editor.style.whiteSpace = settings.wordWrap ? 'pre-wrap' : 'pre';
            editor.style.overflowWrap = settings.wordWrap ? 'break-word' : 'normal';
            editor.wrap = settings.wordWrap ? 'soft' : 'off';
        }
        if (lineNumbers) {
            lineNumbers.style.display = settings.lineNumbers ? '' : 'none';
            lineNumbers.style.fontSize = `${settings.fontSize}px`;
            lineNumbers.style.lineHeight = lineHeight;
        }
        for (const [key, id] of Object.entries(controls)) {
            const control = $(id);
            if (!control) continue;
            if (typeof defaults[key] === 'boolean') control.checked = settings[key];
            else control.value = String(settings[key]);
        }
        if ($('setting-font-size-value')) $('setting-font-size-value').textContent = `${settings.fontSize} px`;
        window.updateLineNumbers?.();
        showStorageStatus();
    }
    function changed(key, value) {
        document.dispatchEvent(new CustomEvent('ryn-settings-change', {
            detail: { key, value, settings: snapshot() }
        }));
    }
    function update(key, value) {
        if (!Object.hasOwn(defaults, key) || !valid(key, value)) return false;
        if (settings[key] === value) return true;
        settings[key] = value;
        apply();
        save();
        changed(key, value);
        return true;
    }
    function reset() {
        settings = { ...defaults };
        apply();
        save();
        changed('reset', null);
    }

    window.RynSettings = Object.freeze({ get: snapshot, update, reset });
    for (const [key, id] of Object.entries(controls)) {
        const control = $(id);
        if (!control) continue;
        control.addEventListener(key === 'fontSize' ? 'input' : 'change', () => {
            const value = typeof defaults[key] === 'boolean' ? control.checked
                : typeof defaults[key] === 'number' ? Number(control.value) : control.value;
            update(key, value);
        });
    }
    $('settings-reset')?.addEventListener('click', reset);
    apply();
})();
