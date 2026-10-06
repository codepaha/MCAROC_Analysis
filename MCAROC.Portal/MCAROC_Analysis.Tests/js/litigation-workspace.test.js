const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../MCAROC_Analysis/wwwroot/js/litigation-workspace.js'), 'utf8');

function harness(availability = false) {
    const requests = [], history = [], events = {}, attributes = new Map();
    let current;
    function list(page) {
        return { dataset: { requestId: '7', snapshot: '12', generation: 'generation-a', page: String(page) },
            querySelector: () => ({ textContent: `${page * 25 - 24}–${page * 25} of 3000 matching cases` }),
            replaceWith(replacement) { current = replacement; }, focus() { this.focused = true; } };
    }
    current = list(1);
    const feedback = { textContent: '', append(link) { this.link = link; } };
    const register = { querySelector: () => current, addEventListener(name, callback) { events[name] = callback; },
        setAttribute(name, value) { attributes.set(name, value); }, removeAttribute(name) { attributes.delete(name); } };
    const location = { origin: 'https://portal.test', href: 'https://portal.test/Requests/7#tab-litigation' };
    const button = { dataset: { analysisAvailability: '/Requests/7/Litigation/AnalysisAvailability' }, disabled: true };
    const tab = { addEventListener(name, callback) { events[name] = callback; } };
    const context = { URL, URLSearchParams, AbortController, location,
        FormData: class { constructor(form) { return form.entries; } },
        DOMParser: class { parseFromString(html) { return { getElementById: () => html === 'login' ? null : list(Number(html)) }; } },
        document: { getElementById: id => ({ 'litigation-register': register, 'litigation-list-feedback': feedback,
            'litigation-run-analysis': availability ? button : null, 'tab-button-litigation': tab })[id] ?? null,
            querySelectorAll: () => [], addEventListener() {}, createElement: () => ({}) },
        window: { addEventListener(name, callback) { events[name] = callback; } },
        history: { pushState(state, title, url) { history.push(url); location.href = url.href; } },
        fetch(url, options) { return new Promise(resolve => requests.push({ url, options, resolve })); }
    };
    vm.runInNewContext(source, context);
    const click = href => events.click({ target: { closest: () => ({ href }) }, button: 0, preventDefault() {} });
    const resolve = async (index, status = 200, html = '2') => {
        requests[index].resolve({ ok: status === 200, status, text: async () => html });
        await new Promise(resolve => setImmediate(resolve));
    };
    return { requests, history, events, attributes, feedback, button, click, resolve, current: () => current };
}

test('analysis availability runs once when the tab opens and does not block case paging', async () => {
    const h = harness(true);
    assert.equal(h.requests.length, 0);
    h.events['shown.bs.tab']();
    h.events['shown.bs.tab']();
    assert.equal(h.requests.length, 1);
    assert.equal(h.requests[0].url.searchParams.get('generation'), 'generation-a');
    h.click('https://portal.test/Requests/7?page=2');
    await h.resolve(1, 200, '2');
    assert.equal(h.current().dataset.page, '2');
    assert.equal(h.button.disabled, true);
    h.requests[0].resolve({ ok: true, json: async () => ({ needsAnalysis: true }) });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.button.disabled, false);
});

test('pagination replaces only the register, carries publication identity and preserves URL filters', async () => {
    const h = harness();
    h.click('https://portal.test/Requests/7?page=999&q=Opponent&status=Pending&pageSize=25#tab-litigation');
    assert.equal(h.attributes.get('aria-busy'), 'true');
    assert.equal(h.requests[0].url.pathname, '/Requests/7/Litigation/CaseList');
    assert.equal(h.requests[0].url.searchParams.get('snapshot'), '12');
    assert.equal(h.requests[0].url.searchParams.get('generation'), 'generation-a');
    await h.resolve(0);
    assert.equal(h.current().dataset.page, '2');
    assert.equal(h.history[0].searchParams.get('page'), '2');
    assert.equal(h.history[0].searchParams.get('q'), 'Opponent');
    assert.equal(h.history[0].hash, '#tab-litigation');
    assert.equal(h.attributes.has('aria-busy'), false);
    assert.equal(h.current().focused, true);
});

test('a superseded response cannot overwrite newer results or clear their loading state', async () => {
    const h = harness();
    h.click('https://portal.test/Requests/7?page=2');
    h.click('https://portal.test/Requests/7?page=3');
    assert.equal(h.requests[0].options.signal.aborted, true);
    await h.resolve(0, 200, '2');
    assert.equal(h.current().dataset.page, '1');
    assert.equal(h.attributes.get('aria-busy'), 'true');
    await h.resolve(1, 200, '3');
    assert.equal(h.current().dataset.page, '3');
    assert.equal(h.history.length, 1);
});

test('publication conflicts retain existing rows and offer the filtered full-page fallback', async () => {
    const h = harness();
    h.click('https://portal.test/Requests/7?page=2&q=Bank#tab-litigation');
    await h.resolve(0, 409);
    assert.equal(h.current().dataset.page, '1');
    assert.match(h.feedback.textContent, /report changed/i);
    assert.equal(h.feedback.link.href, 'https://portal.test/Requests/7?page=2&q=Bank#tab-litigation');
    assert.equal(h.history.length, 0);
    assert.equal(h.attributes.has('aria-busy'), false);
});

test('a redirected sign-in response cannot replace the case list', async () => {
    const h = harness();
    h.click('https://portal.test/Requests/7?page=2');
    await h.resolve(0, 200, 'login');
    assert.equal(h.current().dataset.page, '1');
    assert.match(h.feedback.textContent, /session/i);
});

test('a filter submission searches the request and resets the page', async () => {
    const h = harness();
    h.events.submit({ target: { closest: () => ({ action: 'https://portal.test/Requests/7#tab-litigation', entries: [['q', 'A & B'], ['pageSize', '50']] }) }, preventDefault() {} });
    assert.equal(h.requests[0].url.searchParams.get('q'), 'A & B');
    assert.equal(h.requests[0].url.searchParams.has('page'), false);
    await h.resolve(0, 200, '1');
});
