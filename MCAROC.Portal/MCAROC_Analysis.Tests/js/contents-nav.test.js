import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const McaContentsNav = require('../../MCAROC_Analysis/wwwroot/js/contents-nav.js');

function createMockElement(tagName = 'div', initialAttrs = {}) {
    const el = {
        tagName: tagName.toUpperCase(),
        attributes: { ...initialAttrs },
        children: [],
        parentNode: null,
        dataset: { ...initialAttrs },
        className: '',
        classList: {
            classes: new Set(initialAttrs.class ? initialAttrs.class.split(' ').filter(Boolean) : []),
            add(c) { this.classes.add(c); },
            remove(c) { this.classes.delete(c); },
            contains(c) { return this.classes.has(c); },
            toggle(c, force) {
                if (force === undefined) {
                    if (this.classes.has(c)) { this.classes.delete(c); return false; }
                    else { this.classes.add(c); return true; }
                } else if (force) {
                    this.classes.add(c); return true;
                } else {
                    this.classes.delete(c); return false;
                }
            }
        },
        style: {},
        offsetHeight: 40,
        scrollIntoViewCalls: [],
        focusCalls: [],
        eventListeners: {},
        addEventListener(event, fn, opts) {
            if (!this.eventListeners[event]) this.eventListeners[event] = [];
            this.eventListeners[event].push({ fn, opts });
        },
        removeEventListener(event, fn) {
            if (!this.eventListeners[event]) return;
            this.eventListeners[event] = this.eventListeners[event].filter(x => x.fn !== fn);
        },
        dispatchEvent(event) {
            const type = event.type || event;
            const entries = [...(this.eventListeners[type] || [])];
            for (const entry of entries) {
                entry.fn(event);
                if (entry.opts && entry.opts.once) {
                    this.removeEventListener(type, entry.fn);
                }
            }
            return true;
        },
        setAttribute(k, v) { this.attributes[k] = String(v); },
        getAttribute(k) { return this.attributes[k] ?? null; },
        scrollIntoView(options) { this.scrollIntoViewCalls.push(options); },
        focus(opts) { this.focusCalls.push(opts); },
        closest(selector) {
            let cur = this.parentNode;
            while (cur) {
                if (selector === 'tbody.collapse' && cur.tagName === 'TBODY' && cur.classList.contains('collapse')) {
                    return cur;
                }
                cur = cur.parentNode;
            }
            return null;
        },
        querySelector(selector) {
            for (const c of this.children) {
                if (c.matches && c.matches(selector)) return c;
                if (c.querySelector) {
                    const found = c.querySelector(selector);
                    if (found) return found;
                }
            }
            return null;
        },
        querySelectorAll(selector) {
            const results = [];
            const walk = (node) => {
                for (const c of node.children) {
                    if (c.matches && c.matches(selector)) results.push(c);
                    if (c.children) walk(c);
                }
            };
            walk(this);
            return results;
        },
        matches(selector) {
            if (selector.startsWith('#')) return this.attributes.id === selector.substring(1);
            if (selector.startsWith('.')) return this.classList.contains(selector.substring(1));
            if (selector.includes('[data-bs-target=')) {
                const val = selector.match(/\[data-bs-target="?([^"\]]+)"?\]/)?.[1];
                return this.attributes['data-bs-target'] === val;
            }
            if (selector.includes('[data-bs-toggle="tab"]')) {
                return this.attributes['data-bs-toggle'] === 'tab';
            }
            if (selector.includes('button[data-bs-toggle="tab"]')) {
                return this.tagName === 'BUTTON' && this.attributes['data-bs-toggle'] === 'tab';
            }
            if (selector.includes('a.nav-link')) {
                return this.tagName === 'A' && this.classList.contains('nav-link');
            }
            if (selector.includes('[data-basis-group]')) {
                return this.attributes['data-basis-group'] !== undefined;
            }
            if (selector.includes('[data-basis]')) {
                return this.attributes['data-basis'] !== undefined;
            }
            if (selector.includes('[data-mca-sections]')) {
                return this.attributes['data-mca-sections'] !== undefined;
            }
            return false;
        }
    };
    return el;
}

test('parseHash correctly resolves canonical section IDs, tab slugs, and legacy hashes', () => {
    // 1. Canonical section IDs
    const p1 = McaContentsNav.parseHash('#sec-financials-pl');
    assert.deepEqual(p1, {
        type: 'section',
        domain: 'financials',
        sectionId: 'sec-financials-pl',
        targetTab: 'tab-financials'
    });

    const p2 = McaContentsNav.parseHash('#sec-litigation-financial-disputes');
    assert.deepEqual(p2, {
        type: 'section',
        domain: 'litigation',
        sectionId: 'sec-litigation-financial-disputes',
        targetTab: 'tab-litigation'
    });

    // 2. Legacy slash format
    const p3 = McaContentsNav.parseHash('#tab-financials/ratios');
    assert.deepEqual(p3, {
        type: 'section',
        domain: 'financials',
        sectionId: 'sec-financials-ratios',
        targetTab: 'tab-financials'
    });

    const p4 = McaContentsNav.parseHash('#tab-charges/satisfied');
    assert.deepEqual(p4, {
        type: 'section',
        domain: 'charges',
        sectionId: 'sec-charges-satisfied',
        targetTab: 'tab-charges'
    });

    // 3. Normalization for singular financial-dispute
    const p5 = McaContentsNav.parseHash('#tab-litigation/financial-dispute');
    assert.deepEqual(p5, {
        type: 'section',
        domain: 'litigation',
        sectionId: 'sec-litigation-financial-disputes',
        targetTab: 'tab-litigation'
    });

    // 4. Tab-only hash
    const p6 = McaContentsNav.parseHash('#tab-compliance');
    assert.deepEqual(p6, {
        type: 'tab',
        domain: 'compliance',
        sectionId: null,
        targetTab: 'tab-compliance'
    });

    // 5. Invalid / unknown hash is safe no-op
    assert.equal(McaContentsNav.parseHash('#unknown-hash'), null);
    assert.equal(McaContentsNav.parseHash('#sec-invalid-domain'), null);
    assert.equal(McaContentsNav.parseHash(''), null);
    assert.equal(McaContentsNav.parseHash(null), null);
});

test('ensureTabActive: already-active tab invokes callback immediately without hanging for shown.bs.tab', () => {
    const btn = createMockElement('button', { 'data-bs-target': '#tab-financials', class: 'active' });
    let called = false;

    let showCalled = false;
    const mockBootstrap = {
        Tab: {
            getOrCreateInstance: (el) => ({
                show: () => { showCalled = true; }
            })
        }
    };

    McaContentsNav.ensureTabActive(btn, () => {
        called = true;
    }, mockBootstrap);

    assert.equal(called, true, 'Callback should be invoked immediately for already-active tab');
    assert.equal(showCalled, false, 'Bootstrap show() should not be called if already active');
});

test('ensureTabActive: inactive tab triggers bootstrap.Tab.show() and awaits shown.bs.tab', () => {
    const btn = createMockElement('button', { 'data-bs-target': '#tab-financials' });
    let called = false;
    let showCalled = false;

    const mockBootstrap = {
        Tab: {
            getOrCreateInstance: (el) => ({
                show: () => {
                    showCalled = true;
                    // Tab will dispatch shown.bs.tab asynchronously
                    setTimeout(() => {
                        el.classList.add('active');
                        el.dispatchEvent({ type: 'shown.bs.tab', target: el });
                    }, 5);
                }
            })
        }
    };

    return new Promise((resolve) => {
        McaContentsNav.ensureTabActive(btn, () => {
            called = true;
            assert.equal(showCalled, true);
            resolve();
        }, mockBootstrap);
    });
});

test('openAndScrollCharge: uses deterministic Bootstrap Collapse and sequences holder and drawer shown.bs.collapse', () => {
    const chargesTabBtn = createMockElement('button', { 'data-bs-target': '#tab-charges', class: 'active' });
    const row = createMockElement('tr', { id: 'charge-999', class: 'collapse' });
    const holderGroup = createMockElement('tbody', { class: 'collapse' });
    holderGroup.children.push(row);
    row.parentNode = holderGroup;

    const doc = {
        getElementById: (id) => {
            if (id === 'charge-999') return row;
            return null;
        },
        querySelector: (sel) => {
            if (sel.includes('#tab-charges')) return chargesTabBtn;
            return null;
        }
    };

    const showedInstances = [];
    const mockBootstrap = {
        Collapse: {
            getOrCreateInstance: (el, opts) => {
                return {
                    show: () => {
                        showedInstances.push(el);
                        setTimeout(() => {
                            el.classList.add('show');
                            el.dispatchEvent({ type: 'shown.bs.collapse', target: el });
                        }, 5);
                    }
                };
            }
        }
    };

    const win = {
        matchMedia: () => ({ matches: false })
    };

    return new Promise((resolve) => {
        row.scrollIntoView = (options) => {
            row.scrollIntoViewCalls.push(options);
            assert.equal(showedInstances.length, 2);
            assert.equal(showedInstances[0], holderGroup);
            assert.equal(showedInstances[1], row);
            assert.equal(options.block, 'center');
            resolve();
        };

        McaContentsNav.openAndScrollCharge(999, doc, win, mockBootstrap);
    });
});

test('scrollToSection respects prefers-reduced-motion', () => {
    const target = createMockElement('section', { id: 'sec-financials-pl' });
    const winReduced = {
        matchMedia: (query) => ({ matches: query.includes('reduce') })
    };

    McaContentsNav.scrollToSection(target, winReduced);
    assert.equal(target.scrollIntoViewCalls.length, 1);
    assert.equal(target.scrollIntoViewCalls[0].behavior, 'auto');

    const winNormal = {
        matchMedia: () => ({ matches: false })
    };
    target.scrollIntoViewCalls = [];
    McaContentsNav.scrollToSection(target, winNormal);
    assert.equal(target.scrollIntoViewCalls.length, 1);
    assert.equal(target.scrollIntoViewCalls[0].behavior, 'smooth');
});

test('initDetailsNav: sets up dual ResizeObserver, resets contents-nav height on non-long tabs', () => {
    const cssVars = {};
    const mockDoc = {
        documentElement: {
            style: {
                setProperty(name, val) { cssVars[name] = val; }
            }
        },
        body: createMockElement('body'),
        getElementById: (id) => {
            if (id === 'mcaDetailHead') {
                return createMockElement('div', { id: 'mcaDetailHead', 'data-request-id': 'req-123' });
            }
            if (id === 'contents-nav-financials') {
                const nav = createMockElement('nav', { id: 'contents-nav-financials' });
                nav.offsetHeight = 52;
                return nav;
            }
            return null;
        },
        querySelector: () => null,
        querySelectorAll: () => []
    };

    const observedEls = [];
    const unobservedEls = [];
    class MockResizeObserver {
        constructor(cb) { this.cb = cb; }
        observe(el) { observedEls.push(el); }
        unobserve(el) { unobservedEls.push(el); }
        disconnect() {}
    }

    const nav = McaContentsNav.initDetailsNav({
        doc: mockDoc,
        ResizeObserver: MockResizeObserver,
        bootstrap: {
            ScrollSpy: class {
                static getInstance() { return null; }
                dispose() {}
            }
        }
    });

    assert.ok(nav);
    // Detail head observed
    assert.equal(observedEls.length, 1);
    assert.equal(observedEls[0].attributes.id, 'mcaDetailHead');
});

test('jump link click handler prevents default and calls history.pushState', () => {
    const link = createMockElement('a', { class: 'nav-link', href: '#sec-financials-pl' });
    const nav = createMockElement('nav', { class: 'mca-contents-nav' });
    nav.children.push(link);
    link.parentNode = nav;

    const targetSection = createMockElement('section', { id: 'sec-financials-pl' });

    let pushedUrl = null;
    const mockWin = {
        history: {
            pushState: (state, title, url) => { pushedUrl = url; }
        },
        location: { hash: '' }
    };

    const mockDoc = {
        documentElement: { style: { setProperty() {} } },
        body: createMockElement('body'),
        getElementById: (id) => {
            if (id === 'sec-financials-pl') return targetSection;
            return null;
        },
        querySelector: () => null,
        querySelectorAll: (sel) => {
            if (sel === '.mca-contents-nav a.nav-link') return [link];
            return [];
        }
    };

    McaContentsNav.initDetailsNav({
        doc: mockDoc,
        window: mockWin
    });

    let defaultPrevented = false;
    link.dispatchEvent({
        type: 'click',
        preventDefault: () => { defaultPrevented = true; }
    });

    assert.equal(defaultPrevented, true, 'Click event default must be prevented');
    assert.equal(pushedUrl, '#sec-financials-pl', 'pushState should be called with href');
    assert.equal(targetSection.scrollIntoViewCalls.length, 1, 'Target section should be scrolled into view');
});

test('history navigation: popstate and hashchange safely no-op on invalid hash', () => {
    let popstateCb = null;
    let hashchangeCb = null;

    const mockWin = {
        location: { hash: '#totally-invalid' },
        addEventListener: (event, fn) => {
            if (event === 'popstate') popstateCb = fn;
            if (event === 'hashchange') hashchangeCb = fn;
        }
    };

    const mockDoc = {
        documentElement: { style: { setProperty() {} } },
        body: createMockElement('body'),
        getElementById: () => null,
        querySelector: () => null,
        querySelectorAll: () => []
    };

    const nav = McaContentsNav.initDetailsNav({
        doc: mockDoc,
        window: mockWin
    });

    assert.ok(popstateCb);
    assert.ok(hashchangeCb);

    // Invoking with invalid hash should not throw or error
    assert.doesNotThrow(() => {
        popstateCb();
        hashchangeCb();
    });
});

test('basis toggle wires Standalone and Consolidated panels', () => {
    const btnStd = createMockElement('button', { 'data-basis': 'Standalone', class: 'active' });
    const btnCon = createMockElement('button', { 'data-basis': 'Consolidated' });
    const group = createMockElement('div', { 'data-basis-group': 'basis-pl' });
    group.children.push(btnStd, btnCon);
    btnStd.parentNode = group;
    btnCon.parentNode = group;

    const panelStd = createMockElement('div', { 'data-basis-panel': 'basis-pl', 'data-basis': 'Standalone' });
    const panelCon = createMockElement('div', { 'data-basis-panel': 'basis-pl', 'data-basis': 'Consolidated' });
    panelCon.hidden = true;

    const mockDoc = {
        documentElement: { style: { setProperty() {} } },
        body: createMockElement('body'),
        getElementById: () => null,
        querySelector: () => null,
        querySelectorAll: (sel) => {
            if (sel === '[data-basis-group]') return [group];
            if (sel === '[data-basis-panel="basis-pl"]') return [panelStd, panelCon];
            return [];
        }
    };

    McaContentsNav.initDetailsNav({ doc: mockDoc });

    // Click Consolidated
    btnCon.dispatchEvent({ type: 'click' });
    assert.equal(btnCon.classList.contains('active'), true);
    assert.equal(btnStd.classList.contains('active'), false);
    assert.equal(panelStd.hidden, true);
    assert.equal(panelCon.hidden, false);
});

test('parseHash correctly resolves retained subtab slugs for corporate, ai, and documents', () => {
    // Retained subtab legacy slugs
    const p1 = McaContentsNav.parseHash('#tab-corporate/management');
    assert.deepEqual(p1, {
        type: 'subtab',
        domain: 'corporate',
        sectionId: 'sec-corporate-management',
        targetTab: 'tab-corporate'
    });

    const p2 = McaContentsNav.parseHash('#tab-ai/charges');
    assert.deepEqual(p2, {
        type: 'subtab',
        domain: 'ai',
        sectionId: 'sec-ai-charges',
        targetTab: 'tab-ai'
    });

    const p3 = McaContentsNav.parseHash('#tab-documents/ask');
    assert.deepEqual(p3, {
        type: 'subtab',
        domain: 'documents',
        sectionId: 'sec-documents-ask',
        targetTab: 'tab-documents'
    });

    // Direct canonical subtab IDs
    const p4 = McaContentsNav.parseHash('#sec-corporate-overview');
    assert.deepEqual(p4, {
        type: 'subtab',
        domain: 'corporate',
        sectionId: 'sec-corporate-overview',
        targetTab: 'tab-corporate'
    });

    const p5 = McaContentsNav.parseHash('#sec-ai-xsec');
    assert.deepEqual(p5, {
        type: 'subtab',
        domain: 'ai',
        sectionId: 'sec-ai-xsec',
        targetTab: 'tab-ai'
    });
});

test('hash resolution and tab selector injection safety', () => {
    // Unrecognized or malicious selectors
    assert.equal(McaContentsNav.parseHash('#tab-foo\\'), null);
    assert.equal(McaContentsNav.parseHash('#tab-unknown'), null);
    assert.equal(McaContentsNav.parseHash('#sec-unknown-foo'), null);

    const mockDoc = {
        documentElement: { style: { setProperty() {} } },
        body: createMockElement('body'),
        getElementById: () => null,
        querySelector: (sel) => {
            // If an unescaped raw selector is passed to querySelector, in real DOM it throws DOMException
            if (sel.includes('\\')) throw new Error('DOMException: Invalid selector');
            return null;
        },
        querySelectorAll: () => []
    };

    const mockWin = {
        location: { hash: '#tab-foo\\' }
    };

    // initDetailsNav must not throw when facing malicious or malformed hash
    assert.doesNotThrow(() => {
        McaContentsNav.initDetailsNav({ doc: mockDoc, window: mockWin });
    });
});

test('retained subtab hash activates both parent tab and subtab button, and persists to sessionStorage', () => {
    const parentTabBtn = createMockElement('button', { 'data-bs-target': '#tab-corporate', 'data-bs-toggle': 'tab' });
    const subtabBtnOverview = createMockElement('button', { 'data-bs-target': '#sec-corporate-overview', 'data-bs-toggle': 'tab', class: 'active' });
    const subtabBtnMgmt = createMockElement('button', { 'data-bs-target': '#sec-corporate-management', 'data-bs-toggle': 'tab' });

    const corporateSubnav = createMockElement('nav', { 'data-mca-sections': 'corporate' });
    corporateSubnav.children.push(subtabBtnOverview, subtabBtnMgmt);

    const storage = {};
    const mockWin = {
        location: { hash: '#tab-corporate/management' },
        sessionStorage: {
            setItem: (k, v) => { storage[k] = v; },
            getItem: (k) => storage[k] || null
        },
        addEventListener: () => {}
    };

    const mockDoc = {
        documentElement: { style: { setProperty() {} } },
        body: createMockElement('body'),
        getElementById: (id) => {
            if (id === 'mcaDetailHead') return createMockElement('div', { id: 'mcaDetailHead', 'data-request-id': 'req-1' });
            return null;
        },
        querySelector: () => null,
        querySelectorAll: (sel) => {
            if (sel === '#mcaTabs button[data-bs-toggle="tab"]') return [parentTabBtn];
            if (sel === '[data-mca-sections]') return [corporateSubnav];
            return [];
        }
    };

    const mockBootstrap = {
        Tab: {
            getOrCreateInstance: (el) => ({
                show: () => {
                    el.classList.add('active');
                    el.dispatchEvent({ type: 'shown.bs.tab', target: el });
                }
            })
        },
        ScrollSpy: class {
            static getInstance() { return null; }
            dispose() {}
        }
    };

    McaContentsNav.initDetailsNav({
        doc: mockDoc,
        window: mockWin,
        bootstrap: mockBootstrap
    });

    assert.equal(parentTabBtn.classList.contains('active'), true, 'Parent tab must be active');
    assert.equal(subtabBtnMgmt.classList.contains('active'), true, 'Management subtab must be active');
    assert.equal(storage['mca-v2-req-1-sec-corporate'], '#sec-corporate-management', 'Subtab must be persisted to sessionStorage');
});

test('focusCharge contract: ?charge=71 with compatible hash #tab-charges runs openAndScrollCharge', () => {
    const chargesTabBtn = createMockElement('button', { 'data-bs-target': '#tab-charges', 'data-bs-toggle': 'tab' });
    const row = createMockElement('tr', { id: 'charge-71', class: 'collapse' });
    const holderGroup = createMockElement('tbody', { class: 'collapse' });
    holderGroup.children.push(row);
    row.parentNode = holderGroup;

    let chargeScrolled = false;
    row.scrollIntoView = () => { chargeScrolled = true; };

    const mockWin = {
        location: { hash: '#tab-charges' },
        matchMedia: () => ({ matches: false }),
        addEventListener: () => {}
    };

    const mockDoc = {
        documentElement: { style: { setProperty() {} } },
        body: createMockElement('body'),
        getElementById: (id) => {
            if (id === 'mcaDetailHead') return createMockElement('div', { id: 'mcaDetailHead', 'data-request-id': 'req-1', 'data-focus-charge-id': '71' });
            if (id === 'charge-71') return row;
            return null;
        },
        querySelector: (sel) => {
            if (sel.includes('#tab-charges')) return chargesTabBtn;
            return null;
        },
        querySelectorAll: (sel) => {
            if (sel === '#mcaTabs button[data-bs-toggle="tab"]') return [chargesTabBtn];
            return [];
        }
    };

    const mockBootstrap = {
        Tab: {
            getOrCreateInstance: (el) => ({
                show: () => {
                    el.classList.add('active');
                    el.dispatchEvent({ type: 'shown.bs.tab', target: el });
                }
            })
        },
        Collapse: {
            getOrCreateInstance: (el) => ({
                show: () => {
                    el.classList.add('show');
                    setTimeout(() => {
                        el.dispatchEvent({ type: 'shown.bs.collapse', target: el });
                    }, 5);
                }
            })
        },
        ScrollSpy: class {
            static getInstance() { return null; }
            dispose() {}
        }
    };

    return new Promise((resolve) => {
        McaContentsNav.initDetailsNav({
            doc: mockDoc,
            window: mockWin,
            bootstrap: mockBootstrap
        });

        // Give collapse event chain time to settle
        setTimeout(() => {
            assert.equal(chargesTabBtn.classList.contains('active'), true, 'Charges tab activated');
            assert.equal(chargeScrolled, true, 'Charge 71 was scrolled into view despite #tab-charges hash');
            resolve();
        }, 50);
    });
});


