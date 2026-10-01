// RIM client helpers. Callers (via RimJs) reference these as rim.* — the
// global MUST stay named `rim` and the two sides must be renamed together
// (a past one-sided rename broke Copy/Export/Print).
window.rim = {
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
    // Dark/light mode (v0.12.0). MudBlazor's MudThemeProvider handles its
    // own components; the body.dark class covers RIM's custom CSS rules.
    theme: {
        prefersDark: function () {
            return !!(window.matchMedia &&
                window.matchMedia('(prefers-color-scheme: dark)').matches);
        },
        setDark: function (on) {
            document.body.classList.toggle('dark', !!on);
        }
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
            window.rim.hotkeys._combos = s;
        },
        init: function (dotNet) {
            if (window.rim.hotkeys._inited) return;
            window.rim.hotkeys._inited = true;
            document.addEventListener('keydown', function (e) {
                const tag = (e.target && e.target.tagName) || '';
                const editable = /^(INPUT|TEXTAREA|SELECT)$/.test(tag) ||
                    (e.target && e.target.isContentEditable);
                // Escape always goes to the app (close dialog / clear selection).
                if (e.key === 'Escape') {
                    if (window.rim.hotkeys._combos['Escape']) e.preventDefault();
                    dotNet.invokeMethodAsync('OnHotkey', 'Escape');
                    return;
                }
                // In text inputs, typing and text-editing shortcuts belong to
                // the browser — except registered app combos with no
                // text-editing role (e.g. Ctrl+N): those must be intercepted
                // here too, or the browser's native action (new window,
                // save-page dialog, ...) fires instead of the app handler.
                if (editable) {
                    const ec = window.rim.hotkeys.combo(e);
                    if (ec && window.rim.hotkeys._combos[ec] &&
                        !/^(Ctrl\+(C|X|V|Z|Y|A)|Delete|Backspace)$/.test(ec)) {
                        e.preventDefault();
                        dotNet.invokeMethodAsync('OnHotkey', ec);
                    }
                    return;
                }
                const combo = window.rim.hotkeys.combo(e);
                if (!combo) return;
                // If the user selected actual text, Ctrl+C / Ctrl+A belong to
                // the browser (copy/select that text), not to the grid.
                const sel = window.getSelection && window.getSelection();
                const hasTextSel = sel && !sel.isCollapsed;
                if (hasTextSel && (combo === 'Ctrl+C' || combo === 'Ctrl+A')) return;
                if (window.rim.hotkeys._combos[combo]) e.preventDefault();
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
    }
};
