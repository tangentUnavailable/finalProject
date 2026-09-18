// CvHub client helpers (thin interop layer over ready-made libraries).

// ---------- Theme + language ----------
window.cvTheme = {
    apply(t) {
        document.documentElement.classList.toggle('dark', t === 'dark');
    }
};

// ---------- Markdown rendering (marked + DOMPurify) ----------
window.cvMarkdown = {
    render(md) {
        if (!md) return '';
        const html = marked.parse(md, { breaks: true, gfm: true });
        return DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });
    }
};

// ---------- Live discussions (2s polling, appended chronologically) ----------
window.cvLive = {
    timers: {},
    start(id, dotnetRef, intervalMs) {
        this.stop(id);
        this.timers[id] = setInterval(async () => {
            try { await dotnetRef.invokeMethodAsync('Poll'); } catch { /* circuit gone */ }
        }, intervalMs || 2000);
    },
    stop(id) {
        if (this.timers[id]) { clearInterval(this.timers[id]); delete this.timers[id]; }
    }
};

// ---------- Autosave heartbeat indicator + debounce utilities ----------
window.cvUtil = {
    debounce(fn, ms) {
        let t;
        return (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };
    }
};

// ---------- Theme + language preference toggles ----------
// These links point at /prefs/* endpoints that set a cookie and redirect. Blazor's
// enhanced-form navigation mangles that round-trip inside a circuit (DOM wipe -> blank
// page), so we intercept the clicks ourselves:
//   * theme: swap the <html> class + POST the cookie immediately — instant, no reload;
//   * language: persist the cookie, then a full (non-enhanced) reload re-renders strings.
window.cvPrefs = {
    init() {
        document.addEventListener('click', e => {
            const a = e.target.closest('a[href^="/prefs/theme/"]');
            if (a) {
                e.preventDefault();
                e.stopImmediatePropagation();
                const theme = a.getAttribute('href').endsWith('/dark') ? 'dark' : 'light';
                document.documentElement.classList.toggle('dark', theme === 'dark');
                document.cookie = `cv_theme=${theme}; path=/; max-age=31536000; samesite=lax`;
                return;
            }
            const lang = e.target.closest('a[href^="/prefs/lang/"]');
            if (lang) {
                e.preventDefault();
                e.stopImmediatePropagation();
                const l = lang.getAttribute('href').endsWith('/es') ? 'es' : 'en';
                document.cookie = `cv_lang=${l}; path=/; max-age=31536000; samesite=lax`;
                window.location.reload(); // full load: server re-renders all UI strings
            }
        }, true); // capture phase: runs before Blazor's enhanced-navigation handlers
    }
};
cvPrefs.init();
