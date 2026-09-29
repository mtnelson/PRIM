window.ripdesk = {
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
        catch (e) { return false; }
    },
    print: function () { window.print(); }
};
