(() => {
    'use strict';
    const register = document.getElementById('litigation-register');
    if (!register) return;
    const feedback = document.getElementById('litigation-list-feedback');
    const analysisButton = document.getElementById('litigation-run-analysis');
    let availabilityStarted = false;
    async function checkAnalysisAvailability() {
        if (availabilityStarted || !analysisButton?.dataset.analysisAvailability) return;
        availabilityStarted = true;
        const endpoint = new URL(analysisButton.dataset.analysisAvailability, location.origin);
        const generation = register.querySelector('#litigation-case-list')?.dataset.generation;
        if (generation) endpoint.searchParams.set('generation', generation);
        try {
            const response = await fetch(endpoint, { headers: { 'Accept': 'application/json' }, cache: 'no-store' });
            if (!response.ok) throw new Error();
            const state = await response.json();
            analysisButton.disabled = !state.needsAnalysis;
            analysisButton.title = state.needsAnalysis ? 'Analyse new or changed cases' : 'All cases are already analysed and nothing has changed';
        } catch {
            analysisButton.title = 'Analysis availability could not be checked. Reload the page to try again.';
            const message = document.getElementById('litigation-action-message');
            if (message) { message.textContent = analysisButton.title; message.classList.remove('d-none'); }
        }
    }
    // The expensive evidence check must not block page rendering or run while another domain is being read.
    document.getElementById('tab-button-litigation')?.addEventListener('shown.bs.tab', checkAnalysisAvailability);
    if (document.getElementById('tab-litigation')?.classList.contains('active')) checkAnalysisAvailability();
    let pending;
    let sequence = 0;
    async function load(url, push = true) {
        const list = register.querySelector('#litigation-case-list');
        if (!list) return;
        const ticket = ++sequence;
        pending?.abort();
        pending = new AbortController();
        register.setAttribute('aria-busy', 'true');
        feedback.textContent = 'Loading cases…';
        const endpoint = new URL(`/Requests/${list.dataset.requestId}/Litigation/CaseList`, location.origin);
        endpoint.search = url.search;
        if (list.dataset.snapshot) endpoint.searchParams.set('snapshot', list.dataset.snapshot);
        if (list.dataset.generation) endpoint.searchParams.set('generation', list.dataset.generation);
        try {
            const response = await fetch(endpoint, { signal: pending.signal, headers: { 'Accept': 'text/html' }, cache: 'no-store' });
            if (ticket !== sequence) return;
            if (!response.ok) throw new Error(response.status === 409 ? 'The report changed. Reload to see the latest cases.' : 'Cases could not be loaded. Try again or open the full page.');
            const html = await response.text();
            if (ticket !== sequence) return;
            const doc = new DOMParser().parseFromString(html, 'text/html');
            const replacement = doc.getElementById('litigation-case-list');
            if (!replacement || replacement.dataset.requestId !== list.dataset.requestId) throw new Error('Your session may have ended. Open the full page to continue.');
            // Preserve the canonical clamped page rather than a stale/out-of-range input.
            url.searchParams.set('page', replacement.dataset.page);
            url.hash = 'tab-litigation';
            list.replaceWith(replacement);
            if (push) history.pushState({ litigation: true }, '', url);
            feedback.textContent = `${replacement.querySelector('.litigation-list-caption').textContent.trim()}`;
            replacement.focus({ preventScroll: true });
        } catch (error) {
            if (error.name === 'AbortError' || ticket !== sequence) return;
            feedback.textContent = error.message + ' ';
            const fallback = document.createElement('a');
            fallback.href = url.href;
            fallback.textContent = 'Open full page';
            feedback.append(fallback);
        } finally {
            if (ticket === sequence) register.removeAttribute('aria-busy');
        }
    }
    register.addEventListener('submit', event => {
        const form = event.target.closest('[data-litigation-filter]');
        if (!form) return;
        event.preventDefault();
        const url = new URL(form.action, location.origin);
        url.search = new URLSearchParams(new FormData(form)).toString();
        load(url);
    });
    register.addEventListener('click', event => {
        const link = event.target.closest('a[data-litigation-page]');
        if (!link || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || event.button !== 0) return;
        event.preventDefault();
        load(new URL(link.href));
    });
    window.addEventListener('popstate', () => {
        const url = new URL(location.href);
        const list = register.querySelector('#litigation-case-list');
        if (url.pathname === `/Requests/${list?.dataset.requestId}` && url.hash === '#tab-litigation') load(url, false);
        else {
            ++sequence;
            pending?.abort();
            register.removeAttribute('aria-busy');
            feedback.textContent = '';
        }
    });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape') document.querySelectorAll('details.workspace-menu[open]').forEach(menu => {
            menu.open = false;
            menu.querySelector('summary').focus();
        });
    });
    document.addEventListener('click', event => document.querySelectorAll('details.workspace-menu[open]').forEach(menu => {
        if (!menu.contains(event.target)) menu.open = false;
    }));
})();
