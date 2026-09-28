import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

function element() {
    const listeners = new Map();
    return {
        value: '',
        dataset: {},
        hidden: true,
        innerHTML: '',
        children: [],
        checked: false,
        addEventListener(type, handler) {
            const handlers = listeners.get(type) || [];
            handlers.push(handler);
            listeners.set(type, handlers);
        },
        dispatch(type, extra = {}) {
            for (const handler of listeners.get(type) || []) handler({ type, ...extra });
        },
        appendChild(child) { this.children.push(child); },
        style: {},
        textContent: ''
    };
}

function setup() {
    const ids = ['afCin', 'afName', 'afSelectedIdentifier', 'afHints', 'afHintState',
        'afHintDistrict', 'afHintPin', 'afHintYear', 'entityCompany', 'entityLlp'];
    const elements = Object.fromEntries(ids.map(id => [id, element()]));
    elements.afName.dataset.searchUrl = '/Requests/AutoFetch/search';
    elements.entityCompany.checked = true;

    let now = 0;
    let nextId = 1;
    const timers = new Map();
    const requests = [];
    const view = readFileSync(new URL('../../MCAROC_Analysis/Views/Requests/AutoFetch.cshtml', import.meta.url), 'utf8');
    const script = view.match(/<script>([\s\S]*?)<\/script>/)?.[1];
    assert.ok(script, 'Auto-fetch inline script exists');
    runInNewContext(script, {
        document: {
            getElementById: id => elements[id],
            createElement: () => element()
        },
        setTimeout(fn, delay) {
            const id = nextId++;
            timers.set(id, { fn, due: now + delay });
            return id;
        },
        clearTimeout(id) { timers.delete(id); },
        fetch(url) {
            requests.push(url);
            return Promise.resolve({ ok: true, json: async () => [] });
        },
        encodeURIComponent
    });

    function advance(milliseconds) {
        const end = now + milliseconds;
        while (true) {
            const due = [...timers.entries()]
                .filter(([, timer]) => timer.due <= end)
                .sort((a, b) => a[1].due - b[1].due)[0];
            if (!due) break;
            now = due[1].due;
            timers.delete(due[0]);
            due[1].fn();
        }
        now = end;
    }

    return { elements, requests, advance };
}

test('Auto-fetch Escape cancels a pending typeahead request and later typing still searches', () => {
    const { elements, requests, advance } = setup();
    elements.afName.value = 'rel';
    elements.afName.dispatch('input');
    assert.equal(elements.afHints.hidden, false);

    elements.afName.dispatch('keydown', { key: 'Escape' });
    assert.equal(elements.afHints.hidden, true);
    advance(200);
    assert.equal(requests.length, 0, 'hiding before the debounce expires must not query the database');

    elements.afName.value = 'reli';
    elements.afName.dispatch('input');
    advance(199);
    assert.equal(requests.length, 0);
    advance(1);
    assert.equal(requests.length, 1);
    assert.match(requests[0], /fastOnly=true&q=reli/);
});

test('Auto-fetch typing in the CIN field cancels an earlier name-field blur hide', () => {
    const { elements, requests, advance } = setup();
    elements.afName.dispatch('blur');
    elements.afCin.value = 'abc';
    elements.afCin.dispatch('input');

    advance(200);
    assert.equal(requests.length, 1);
    assert.match(requests[0], /fastOnly=true&q=abc/);
});
