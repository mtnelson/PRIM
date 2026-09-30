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
        _combos: {},
        // Combos the app handles (synced from .NET). The browser default for
        // these must be prevented SYNCHRONOUSLY in the keydown handler — doing
        // it after the .NET round-trip is too late and the native action
        // (copy cell text, select-all page text, find bar, print dialog)
        // fires anyway.
        setCombos: function (arr) {
            const s = {};
            (arr || []).forEach(c => { s[c] = 1; });
            window.prim.hotkeys._combos = s;
        },
        init: function (dotNet) {
            if (window.prim.hotkeys._inited) return;
            window.prim.hotkeys._inited = true;
            document.addEventListener('keydown', function (e) {
                const tag = (e.target && e.target.tagName) || '';
                const editable = /^(INPUT|TEXTAREA|SELECT)$/.test(tag) ||
                    (e.target && e.target.isContentEditable);
                // Escape always goes to the app (close dialog / clear selection).
                if (e.key === 'Escape') {
                    if (window.prim.hotkeys._combos['Escape']) e.preventDefault();
                    dotNet.invokeMethodAsync('OnHotkey', 'Escape');
                    return;
                }
                // Never fight text inputs: typing shortcuts belong to the browser.
                if (editable) return;
                const combo = window.prim.hotkeys.combo(e);
                if (!combo) return;
                // If the user selected actual text, Ctrl+C / Ctrl+A belong to
                // the browser (copy/select that text), not to the grid.
                const sel = window.getSelection && window.getSelection();
                const hasTextSel = sel && !sel.isCollapsed;
                if (hasTextSel && (combo === 'Ctrl+C' || combo === 'Ctrl+A')) return;
                if (window.prim.hotkeys._combos[combo]) e.preventDefault();
                dotNet.invokeMethodAsync('OnHotkey', combo);
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
    },
    // Infinite-scroll sentinel: watches the bottom marker of a grid and asks
    // Blazor for the next chunk when it scrolls into view (with a prefetch
    // margin so loading starts before the user hits the bottom).
    observeSentinel: function (el, dotNet) {
        if (!el) return;
        window.prim.unobserveSentinel(el);
        const obs = new IntersectionObserver(function (entries) {
            if (entries.some(function (e) { return e.isIntersecting; }))
                dotNet.invokeMethodAsync('OnSentinelVisible');
        }, { rootMargin: '800px' });
        obs.observe(el);
        el._primSentinelObs = obs;
    },
    unobserveSentinel: function (el) {
        if (el && el._primSentinelObs) {
            el._primSentinelObs.disconnect();
            el._primSentinelObs = null;
        }
    }
};
