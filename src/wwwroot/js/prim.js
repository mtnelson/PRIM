// PRIM client helpers. NOTE: callers reference these as prim.* — the global
// MUST stay named `prim` (a previous rename to `ripdesk` broke Copy/Export/Print).
window.prim = {
    download: function (filename, content, mime) {
        const blob = new Blob([content], { type: mime || 'text/plain' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url; a.download = filename;
        document.body.appendChild(a); a.click();
        setTimeout(() => { URL.revokeObjectURL(url); a.remove(); }, 500);
    },
    copyText: async function (text) {
        try { await navigator.clipboard.writeText(text); return true; }
        catch (e) {
            // Fallback for non-secure contexts / denied clipboard permission.
            try {
                const ta = document.createElement('textarea');
                ta.value = text;
                ta.style.position = 'fixed'; ta.style.opacity = '0';
                document.body.appendChild(ta);
                ta.select();
                const ok = document.execCommand('copy');
                ta.remove();
                return ok;
            } catch (e2) { return false; }
        }
    },
    print: function () { window.print(); },
    focus: function (selector) {
        const el = document.querySelector(selector);
        if (el) el.focus();
    },
    hotkeys: {
        _inited: false,
        init: function (dotNet) {
            if (window.prim.hotkeys._inited) return;
            window.prim.hotkeys._inited = true;
            document.addEventListener('keydown', function (e) {
                const tag = (e.target && e.target.tagName) || '';
                const editable = /^(INPUT|TEXTAREA|SELECT)$/.test(tag) ||
                    (e.target && e.target.isContentEditable);
                // Escape always goes to the app (close dialog / clear selection).
                if (e.key === 'Escape') {
                    dotNet.invokeMethodAsync('OnHotkey', 'Escape').then(h => { if (h) e.preventDefault(); });
                    return;
                }
                // Never fight text inputs: typing shortcuts belong to the browser.
                if (editable) return;
                const combo = window.prim.hotkeys.combo(e);
                if (!combo) return;
                dotNet.invokeMethodAsync('OnHotkey', combo).then(h => { if (h) e.preventDefault(); });
            });
        },
        combo: function (e) {
            const k = e.key;
            if (k === 'Control' || k === 'Shift' || k === 'Alt' || k === 'Meta') return null;
            const parts = [];
            if (e.ctrlKey || e.metaKey) parts.push('Ctrl');
            if (e.altKey) parts.push('Alt');
            if (e.shiftKey) parts.push('Shift');
            parts.push(k.length === 1 ? k.toUpperCase() : k);
            return parts.join('+');
        }
    }
};
