import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const McaCommandPalette = require('../../MCAROC_Analysis/wwwroot/js/command-palette.js');

function createMockElement(tagName = 'div', initialAttrs = {}) {
    const el = {
        tagName: tagName.toUpperCase(),
        attributes: { ...initialAttrs },
        children: [],
        parentNode: null,
        get id() { return this.attributes['id'] || ''; },
        set id(val) { this.attributes['id'] = String(val); },
        dataset: { ...initialAttrs },
        get className() { return Array.from(this.classList.classes).join(' '); },
        set className(val) {
            this.classList.classes = new Set(val ? String(val).split(' ').filter(Boolean) : []);
        },
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
        value: '',
        textContent: '',
        _innerHTML: '',
        get innerHTML() { return this._innerHTML; },
        set innerHTML(val) {
            this._innerHTML = val;
            if (val === '') {
                this.children.forEach(c => { c.parentNode = null; });
                this.children = [];
            }
        },
        focusCalls: 0,
        clickCalls: 0,
        scrollIntoViewCalls: [],
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
            }
            return true;
        },
        setAttribute(k, v) { this.attributes[k] = String(v); },
        getAttribute(k) { return this.attributes[k] ?? null; },
        removeAttribute(k) { delete this.attributes[k]; },
        appendChild(child) {
            child.parentNode = this;
            this.children.push(child);
            return child;
        },
        removeChild(child) {
            const idx = this.children.indexOf(child);
            if (idx >= 0) {
                this.children.splice(idx, 1);
                child.parentNode = null;
            }
            return child;
        },
        focus() { this.focusCalls++; },
        click() { this.clickCalls++; this.dispatchEvent({ type: 'click' }); },
        scrollIntoView(options) { this.scrollIntoViewCalls.push(options); }
    };
    return el;
}

function setupMockDocument(isDetails = true, activeTabDomain = 'financials') {
    const triggerBtn = createMockElement('button', { id: 'piCmdBtn', 'aria-expanded': 'false' });
    const overlay = createMockElement('div', { id: 'piCmdOverlay', hidden: 'true' });
    const modal = createMockElement('div', { id: 'piCmdModal' });
    const input = createMockElement('input', {
        id: 'piCmdInput',
        role: 'combobox',
        'aria-autocomplete': 'list',
        'aria-expanded': 'false',
        'aria-controls': 'piCmdHints'
    });
    const hints = createMockElement('div', { id: 'piCmdHints', role: 'listbox' });
    const themeBtn = createMockElement('button', { id: 'piThemeToggle' });

    modal.appendChild(input);
    modal.appendChild(hints);
    overlay.appendChild(modal);

    const docElements = {
        piCmdBtn: triggerBtn,
        piCmdOverlay: overlay,
        piCmdModal: modal,
        piCmdInput: input,
        piCmdHints: hints,
        piThemeToggle: themeBtn
    };

    let activeTabBtn = null;
    if (isDetails) {
        docElements.mcaDetailHead = createMockElement('div', { id: 'mcaDetailHead', 'data-request-id': 'req-1' });
        docElements.mcaTabs = createMockElement('div', { id: 'mcaTabs' });
        activeTabBtn = createMockElement('button', {
            'data-bs-toggle': 'tab',
            'data-bs-target': '#tab-' + activeTabDomain,
            class: 'active'
        });
        docElements.mcaTabs.appendChild(activeTabBtn);
    }

    const docListeners = {};
    const doc = {
        getElementById(id) {
            return docElements[id] || null;
        },
        createElement(tagName) {
            return createMockElement(tagName);
        },
        querySelector(sel) {
            if (sel.includes('#mcaTabs button') && activeTabBtn) {
                return activeTabBtn;
            }
            return null;
        },
        querySelectorAll() { return []; },
        activeElement: null,
        addEventListener(evt, fn) {
            if (!docListeners[evt]) docListeners[evt] = [];
            docListeners[evt].push(fn);
        },
        dispatchDocEvent(evt) {
            const list = docListeners[evt.type] || [];
            for (const fn of list) fn(evt);
        }
    };

    const win = {
        location: { hash: '', href: '' }
    };

    return { doc, win, triggerBtn, overlay, modal, input, hints, themeBtn };
}

test('command-palette: initial state has aria-expanded false and hidden overlay', () => {
    const { doc, win, triggerBtn, overlay, input } = setupMockDocument(true);

    const palette = McaCommandPalette.initCommandPalette({ doc, window: win });
    assert.ok(palette);

    assert.equal(input.getAttribute('aria-expanded'), 'false');
    assert.equal(triggerBtn.getAttribute('aria-expanded'), 'false');
    assert.equal(overlay.getAttribute('hidden'), 'true');
    assert.equal(palette.getIsOpen(), false);
});

test('command-palette: Ctrl+K toggles palette open and Escape closes it with focus restoration', () => {
    const { doc, win, triggerBtn, overlay, input } = setupMockDocument(true);
    doc.activeElement = triggerBtn;

    const palette = McaCommandPalette.initCommandPalette({ doc, window: win });

    // Press Ctrl+K
    let prevented = false;
    doc.dispatchDocEvent({
        type: 'keydown',
        key: 'k',
        ctrlKey: true,
        preventDefault: () => { prevented = true; }
    });

    assert.equal(prevented, true, 'Ctrl+K default must be prevented');
    assert.equal(palette.getIsOpen(), true);
    assert.equal(overlay.classList.contains('open'), true);
    assert.equal(overlay.getAttribute('hidden'), null);
    assert.equal(input.getAttribute('aria-expanded'), 'true');
    assert.equal(triggerBtn.getAttribute('aria-expanded'), 'true');
    assert.equal(input.focusCalls > 0, true, 'Input must be focused on open');

    // Press Escape
    doc.dispatchDocEvent({
        type: 'keydown',
        key: 'Escape',
        preventDefault: () => {}
    });

    assert.equal(palette.getIsOpen(), false);
    assert.equal(overlay.classList.contains('open'), false);
    assert.equal(overlay.getAttribute('hidden'), 'true');
    assert.equal(input.getAttribute('aria-expanded'), 'false');
    assert.equal(triggerBtn.getAttribute('aria-expanded'), 'false');
    assert.equal(triggerBtn.focusCalls > 0, true, 'Focus must be restored to trigger on close');
});

test('command-palette: off-Details page presents global navigation only, no AI prompts', () => {
    const { doc, win } = setupMockDocument(false); // isDetails = false

    const palette = McaCommandPalette.initCommandPalette({ doc, window: win });
    palette.open();

    const options = palette.buildOptionsList('');
    // Must NOT contain any ai-prompt items
    const hasAiPrompt = options.some(o => o.type === 'ai-prompt');
    assert.equal(hasAiPrompt, false, 'No AI prompts or free-text ask allowed off Details pages');

    // Must contain global navigation items with correct routes
    const hasDashboard = options.some(o => o.value === '/Dashboard');
    const hasPreLogin = options.some(o => o.value === '/pre-login-reports');
    const hasSearchHistory = options.some(o => o.value === '/Requests');
    assert.equal(hasDashboard, true, 'Dashboard route present');
    assert.equal(hasPreLogin, true, 'Pre-login route /pre-login-reports present');
    assert.equal(hasSearchHistory, true, 'Search history route present');
});

test('command-palette: on Details page presents per-tab suggestions and tab quick-jumps', () => {
    const { doc, win } = setupMockDocument(true, 'financials'); // Active tab: financials

    const palette = McaCommandPalette.initCommandPalette({ doc, window: win });
    palette.open();

    const options = palette.buildOptionsList('');
    const financialPrompt = options.find(o => o.value.includes('revenue and PAT trend'));
    assert.ok(financialPrompt, 'Per-tab financial prompt suggested when on Financials tab');
    assert.equal(financialPrompt.type, 'ai-prompt');

    const chargesJump = options.find(o => o.value === '#tab-charges');
    assert.ok(chargesJump, 'Tab quick-jump to #tab-charges present');
    assert.equal(chargesJump.type, 'hash-jump');

    // Execute hash jump
    palette.executeOption(chargesJump);
    assert.equal(win.location.hash, '#tab-charges', 'Quick-jump sets canonical window.location.hash');
});

test('command-palette: selecting AI prompt calls McaChatPanel.submitPrompt and queries full corpus', () => {
    const { doc, win } = setupMockDocument(true, 'compliance');

    let submittedPrompt = null;
    let submittedDoc = null;
    const mockChatPanel = {
        submitPrompt: (prompt, d) => {
            submittedPrompt = prompt;
            submittedDoc = d;
            return true;
        }
    };

    const palette = McaCommandPalette.initCommandPalette({
        doc,
        window: win,
        chatPanel: mockChatPanel
    });
    palette.open();

    const options = palette.buildOptionsList('');
    const epfoPrompt = options.find(o => o.value.includes('delayed EPFO'));
    assert.ok(epfoPrompt);

    palette.executeOption(epfoPrompt);

    // Semantic invariant: per-tab prompts are suggestions only; prompt is sent in full to query the whole corpus
    assert.equal(submittedPrompt, 'Are there any delayed EPFO contribution payments?');
    assert.equal(submittedDoc, doc);
    assert.equal(palette.getIsOpen(), false, 'Palette must close after executing option');
});

test('command-palette: arrow keys cycle active option and update aria-activedescendant', () => {
    const { doc, win, input, hints } = setupMockDocument(true, 'corporate');

    const palette = McaCommandPalette.initCommandPalette({ doc, window: win });
    palette.open();

    // Verify option elements have stable IDs and role="option"
    const optionEls = hints.children.filter(c => c.getAttribute('role') === 'option');
    assert.ok(optionEls.length > 1);
    assert.equal(optionEls[0].getAttribute('id'), 'piCmdOption-0');
    assert.equal(optionEls[1].getAttribute('id'), 'piCmdOption-1');
    assert.equal(input.getAttribute('aria-activedescendant'), 'piCmdOption-0');

    // ArrowDown
    input.dispatchEvent({
        type: 'keydown',
        key: 'ArrowDown',
        preventDefault: () => {}
    });

    assert.equal(input.getAttribute('aria-activedescendant'), 'piCmdOption-1');
    const updatedOptionEls = hints.children.filter(c => c.getAttribute('role') === 'option');
    assert.equal(updatedOptionEls[1].classList.contains('active'), true);
});

test('command-palette: custom query dynamically adds free-text ask option with safe textContent', () => {
    const { doc, win, input } = setupMockDocument(true, 'ai');

    const palette = McaCommandPalette.initCommandPalette({ doc, window: win });
    palette.open();

    const query = '<script>alert(1)</script>';
    const options = palette.buildOptionsList(query);
    assert.ok(options.length > 0);
    assert.equal(options[0].type, 'ai-prompt');
    assert.equal(options[0].value, query);
    assert.equal(options[0].title, 'Ask Document Assistant: "' + query + '"');
});
