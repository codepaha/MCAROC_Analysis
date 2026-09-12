import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { initChatPanel, createBubbleElement } = require('../../MCAROC_Analysis/wwwroot/js/chat-panel.js');

function createMockElement(tagName = 'div', initialAttrs = {}) {
    const el = {
        tagName: tagName.toUpperCase(),
        attributes: { ...initialAttrs },
        children: [],
        parentNode: null,
        className: '',
        classList: {
            classes: new Set(),
            add(c) { this.classes.add(c); },
            remove(c) { this.classes.delete(c); },
            contains(c) { return this.classes.has(c); }
        },
        style: {},
        _textContent: '',
        get textContent() {
            if (this._textContent) return this._textContent;
            return this.children.map(c => c.textContent).join('');
        },
        set textContent(v) {
            this._textContent = String(v ?? '');
        },
        value: '',
        disabled: false,
        hidden: false,
        innerHTML: '',
        eventListeners: {},
        addEventListener(event, fn) {
            if (!this.eventListeners[event]) this.eventListeners[event] = [];
            this.eventListeners[event].push(fn);
        },
        dispatchEvent(event) {
            const fns = this.eventListeners[event.type || event] || [];
            for (const fn of fns) fn(event);
            return true;
        },
        setAttribute(k, v) { this.attributes[k] = String(v); },
        getAttribute(k) { return this.attributes[k] ?? null; },
        focus() {},
        remove() {
            if (this.parentNode) {
                const idx = this.parentNode.children.indexOf(this);
                if (idx !== -1) this.parentNode.children.splice(idx, 1);
                this.parentNode = null;
            }
        },
        appendChild(child) {
            child.parentNode = this;
            this.children.push(child);
            return child;
        },
        querySelector(selector) {
            if (selector === 'input[name="__RequestVerificationToken"]') {
                return this.children.find(c => c.attributes && c.attributes.name === '__RequestVerificationToken') || null;
            }
            return null;
        },
        querySelectorAll(selector) {
            const results = [];
            function traverse(node) {
                for (const ch of node.children) {
                    if (selector.startsWith('.')) {
                        const cls = selector.slice(1);
                        if (ch.className && ch.className.split(' ').includes(cls)) {
                            results.push(ch);
                        }
                    }
                    traverse(ch);
                }
            }
            traverse(this);
            return results;
        }
    };
    return el;
}

function createMockDocument() {
    const elementsById = new Map();
    const doc = {
        createElement(tag) { return createMockElement(tag); },
        createTextNode(text) {
            const node = createMockElement('#text');
            node.textContent = text;
            return node;
        },
        getElementById(id) { return elementsById.get(id) || null; },
        register(id, el) { elementsById.set(id, el); return el; },
        addEventListener(event, fn) {},
        dispatchEvent(event) {}
    };
    return doc;
}

function setupFixture() {
    const doc = createMockDocument();
    const trigger = doc.register('mcaChatTrigger', doc.createElement('button'));
    const panel = doc.register('mcaChatPanel', doc.createElement('aside'));
    const closeBtn = doc.register('mcaChatClose', doc.createElement('button'));
    const form = doc.register('mcaChatForm', doc.createElement('form'));
    form.setAttribute('data-request-id', '42');

    const tokenInput = doc.createElement('input');
    tokenInput.setAttribute('name', '__RequestVerificationToken');
    tokenInput.value = 'test-token-123';
    form.appendChild(tokenInput);

    const input = doc.register('mcaChatInput', doc.createElement('textarea'));
    form.appendChild(input);

    const submitBtn = doc.register('mcaChatSubmit', doc.createElement('button'));
    form.appendChild(submitBtn);

    const messagesContainer = doc.register('mcaChatMessages', doc.createElement('div'));
    const emptyState = doc.register('mcaChatEmpty', doc.createElement('div'));
    emptyState.className = 'mca-empty mca-chat-empty';
    messagesContainer.appendChild(emptyState);

    const errorBanner = doc.register('mcaChatError', doc.createElement('div'));
    const charCount = doc.register('mcaCharCount', doc.createElement('span'));
    const body = doc.register('mcaChatBody', doc.createElement('div'));
    const badge = doc.register('mcaChatBadge', doc.createElement('span'));
    badge.textContent = '0';

    return {
        doc,
        trigger,
        panel,
        closeBtn,
        form,
        input,
        submitBtn,
        messagesContainer,
        emptyState,
        errorBanner,
        charCount,
        body,
        badge
    };
}

test('chat-panel: timeout/abort removes optimistic userTurn, restores emptyState, and shows error banner', async () => {
    const fixture = setupFixture();
    const mockFetch = async () => {
        const err = new Error('The operation was aborted');
        err.name = 'AbortError';
        throw err;
    };

    const controller = initChatPanel(fixture.doc, mockFetch);
    assert.ok(controller, 'Controller initialized');

    fixture.input.value = 'Who are the current directors?';
    await controller.handleSubmit();

    // The optimistic user bubble must have been removed from DOM so client matches server rollback
    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 0, 'No user turns must remain in DOM after timeout/abort');

    // Empty state should be restored
    assert.equal(fixture.messagesContainer.children.includes(fixture.emptyState), true, 'Empty state must be restored');

    // Error banner should display timeout message
    assert.match(fixture.errorBanner.textContent, /timed out after 45 seconds/i);
    assert.equal(fixture.errorBanner.style.display, 'block');

    // Input must be re-enabled and still contain question for retry
    assert.equal(fixture.input.disabled, false);
    assert.equal(fixture.submitBtn.disabled, false);
    assert.equal(fixture.input.value, 'Who are the current directors?');
});

test('chat-panel: network error removes optimistic userTurn and shows network error banner', async () => {
    const fixture = setupFixture();
    const mockFetch = async () => {
        throw new TypeError('Failed to fetch');
    };

    const controller = initChatPanel(fixture.doc, mockFetch);
    fixture.input.value = 'What is the share capital?';
    await controller.handleSubmit();

    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 0, 'User turn must be removed on network failure');
    assert.match(fixture.errorBanner.textContent, /network error connecting to assistant/i);
    assert.equal(fixture.input.disabled, false);
});

test('chat-panel: server 400 error removes optimistic userTurn and shows error banner', async () => {
    const fixture = setupFixture();
    const mockFetch = async () => ({
        ok: false,
        status: 400,
        json: async () => ({
            success: false,
            error: { code: 'INVALID_TOKEN', message: 'Invalid request or expired verification token. Please refresh.' }
        })
    });

    const controller = initChatPanel(fixture.doc, mockFetch);
    fixture.input.value = 'Is company active?';
    await controller.handleSubmit();

    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 0, 'User turn must be removed on 400 validation rejection');
    assert.match(fixture.errorBanner.textContent, /expired verification token/i);
});

test('chat-panel: server 502 Upstream AI Failure keeps user turn and appends failed assistant turn', async () => {
    const fixture = setupFixture();
    const mockFetch = async () => ({
        ok: false,
        status: 502,
        json: async () => ({
            success: false,
            error: { code: 'AI_FAILURE', message: 'Sorry, something went wrong answering that question. Please try again.' },
            message: {
                role: 'Assistant',
                text: 'Sorry, something went wrong answering that question. Please try again.',
                status: 'Failed',
                citations: []
            }
        })
    });

    const controller = initChatPanel(fixture.doc, mockFetch);
    fixture.input.value = 'Tell me about charges.';
    await controller.handleSubmit();

    // On 502, server persisted user turn + failed assistant turn, so BOTH must be in DOM
    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 2, 'Both user and assistant turns must be present on 502 failure');
    assert.ok(turns[0].className.includes('user'), 'First turn is user');
    assert.ok(turns[1].className.includes('assistant'), 'Second turn is assistant');
    assert.match(turns[1].children[0].textContent, /Failed/, 'Assistant turn indicates Failed status');
    assert.match(fixture.errorBanner.textContent, /something went wrong/i);
});

test('chat-panel: server 200 OK keeps user turn and appends assistant turn with citations', async () => {
    const fixture = setupFixture();
    const mockFetch = async () => ({
        ok: true,
        status: 200,
        json: async () => ({
            success: true,
            message: {
                role: 'Assistant',
                text: 'The company has 3 active directors.',
                status: 'Success',
                citations: [
                    {
                        sourceType: 'DocumentChunk',
                        documentName: 'DIR-12.pdf',
                        pageNumber: 1,
                        viewerUrl: '/Requests/42/Documents/viewer?documentId=10#page=1'
                    }
                ]
            }
        })
    });

    const controller = initChatPanel(fixture.doc, mockFetch);
    fixture.input.value = 'Who are directors?';
    await controller.handleSubmit();

    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 2, 'User turn and assistant turn present on 200 OK');
    assert.ok(turns[1].className.includes('assistant'));
    assert.equal(fixture.input.value, '', 'Input must be cleared on success');
    assert.equal(fixture.badge.textContent, '2', 'Badge counter incremented by 2');
});

test('chat-panel: empty question is rejected without adding turn', async () => {
    const fixture = setupFixture();
    let fetchCalled = false;
    const mockFetch = async () => {
        fetchCalled = true;
        return { ok: true };
    };

    const controller = initChatPanel(fixture.doc, mockFetch);
    fixture.input.value = '   ';
    await controller.handleSubmit();

    assert.equal(fetchCalled, false, 'Fetch must not be called for whitespace-only question');
    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 0);
    assert.match(fixture.errorBanner.textContent, /cannot be empty/i);
});
