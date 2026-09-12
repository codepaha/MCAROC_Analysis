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
            const all = this.querySelectorAll(selector);
            return all.length > 0 ? all[0] : null;
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

test('chat-panel: timeout/abort with server confirming rollback removes userTurn, restores emptyState, and shows retry error banner', async () => {
    const fixture = setupFixture();
    let callCount = 0;
    const mockFetch = async (url, options) => {
        callCount++;
        if (options && options.method === 'POST') {
            const err = new Error('The operation was aborted');
            err.name = 'AbortError';
            throw err;
        }
        // Reconciliation GET /Requests/42/chat confirms server rolled back (empty messages)
        return {
            ok: true,
            status: 200,
            json: async () => ({ success: true, messages: [] })
        };
    };

    const controller = initChatPanel(fixture.doc, mockFetch);
    assert.ok(controller, 'Controller initialized');

    fixture.input.value = 'Who are the current directors?';
    await controller.handleSubmit();

    assert.equal(callCount, 2, 'POST followed by reconciliation GET');
    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 0, 'No user turns must remain in DOM after server-confirmed rollback');
    assert.equal(fixture.messagesContainer.children.includes(fixture.emptyState), true, 'Empty state must be restored');
    assert.match(fixture.errorBanner.textContent, /timed out after 45 seconds/i);
    assert.equal(fixture.input.disabled, false);
    assert.equal(fixture.submitBtn.disabled, false);
    assert.equal(fixture.input.value, 'Who are the current directors?');
});

test('chat-panel: timeout/abort where server actually committed the turn (race condition) renders canonical transcript without duplicates', async () => {
    const fixture = setupFixture();
    let capturedTurnId = null;
    const mockFetch = async (url, options) => {
        if (options && options.method === 'POST') {
            capturedTurnId = JSON.parse(options.body).clientTurnId;
            const err = new Error('The operation was aborted');
            err.name = 'AbortError';
            throw err;
        }
        // Reconciliation GET reveals the server committed both turns before client timeout
        return {
            ok: true,
            status: 200,
            json: async () => ({
                success: true,
                messages: [
                    { id: 10, clientTurnId: capturedTurnId, role: 'User', text: 'Who are the current directors?', status: 'Success' },
                    { id: 11, inReplyToChatMessageId: 10, clientTurnId: capturedTurnId, role: 'Assistant', text: 'There are 3 active directors.', status: 'Success' }
                ]
            })
        };
    };

    const controller = initChatPanel(fixture.doc, mockFetch);
    fixture.input.value = 'Who are the current directors?';
    await controller.handleSubmit();

    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 2, 'Canonical turns from server rendered');
    assert.ok(turns[0].className.includes('user'));
    assert.ok(turns[1].className.includes('assistant'));
    assert.equal(fixture.input.value, '', 'Input cleared to prevent duplicate submission');
    assert.equal(fixture.badge.textContent, '2');
});

test('chat-panel: network error with reconciliation failure marks turn as Delivery Unconfirmed without claiming rollback', async () => {
    const fixture = setupFixture();
    const mockFetch = async () => {
        throw new TypeError('Failed to fetch');
    };

    const controller = initChatPanel(fixture.doc, mockFetch);
    fixture.input.value = 'What is the share capital?';
    await controller.handleSubmit();

    // Turn must NOT be deleted because rollback could not be confirmed
    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 1, 'Optimistic turn preserved when delivery state cannot be verified');
    const unconfirmedPill = turns[0].querySelectorAll('.mca-delivery-unconfirmed');
    assert.equal(unconfirmedPill.length, 1, 'Marked with Delivery Unconfirmed pill');
    assert.match(fixture.errorBanner.textContent, /delivery status could not be verified/i);
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

test('chat-panel: submission sends a clientTurnId UUID in the request payload', async () => {
    const fixture = setupFixture();
    let capturedBody = null;
    const mockFetch = async (url, options) => {
        if (options && options.method === 'POST') {
            capturedBody = JSON.parse(options.body);
            return {
                ok: true,
                status: 200,
                json: async () => ({
                    success: true,
                    message: { role: 'Assistant', text: 'Answer.', status: 'Success' }
                })
            };
        }
        return { ok: false };
    };

    const controller = initChatPanel(fixture.doc, mockFetch);
    fixture.input.value = 'Valid question?';
    await controller.handleSubmit();

    assert.ok(capturedBody, 'POST body captured');
    assert.equal(capturedBody.question, 'Valid question?');
    assert.ok(capturedBody.clientTurnId, 'clientTurnId must be present in payload');
    assert.match(capturedBody.clientTurnId, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i, 'clientTurnId must be a valid UUID');
});

test('chat-panel: reconciliation observing user-only pending turn enters pending state and polls until assistant arrives', async () => {
    const fixture = setupFixture();
    let getCallCount = 0;
    let capturedTurnId = null;
    const mockFetch = async (url, options) => {
        if (options && options.method === 'POST') {
            capturedTurnId = JSON.parse(options.body).clientTurnId;
            const err = new Error('Client timeout');
            err.name = 'AbortError';
            throw err;
        }

        // GET calls:
        getCallCount++;
        if (getCallCount === 1) {
            // First reconcile GET: server persisted user message, assistant is still generating (in-flight)
            return {
                ok: true,
                status: 200,
                json: async () => ({
                    success: true,
                    messages: [
                        { id: 101, clientTurnId: capturedTurnId, role: 'User', text: 'Tell me about charges.', status: 'Success' }
                    ]
                })
            };
        }

        // Subsequent poll GET: assistant response has now committed
        return {
            ok: true,
            status: 200,
            json: async () => ({
                success: true,
                messages: [
                    { id: 101, clientTurnId: capturedTurnId, role: 'User', text: 'Tell me about charges.', status: 'Success' },
                    { id: 102, inReplyToChatMessageId: 101, clientTurnId: capturedTurnId, role: 'Assistant', text: 'Here are the charges details.', status: 'Success' }
                ]
            })
        };
    };

    // Use fast pollInterval (10ms) so test finishes instantly
    const controller = initChatPanel(fixture.doc, mockFetch, 5000, 10);
    fixture.input.value = 'Tell me about charges.';
    await controller.handleSubmit();

    assert.ok(getCallCount >= 2, `Poll GET must be called at least twice (actual: ${getCallCount})`);
    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 2, 'Canonical turns rendered after polling resolves');
    assert.ok(turns[0].className.includes('user'));
    assert.ok(turns[1].className.includes('assistant'));
    assert.equal(turns[1].children[0].textContent.includes('Here are the charges details.'), true);
    assert.equal(fixture.input.value, '', 'Input cleared once completed turn is reconciled');
    assert.equal(fixture.input.disabled, false, 'Input re-enabled');
    assert.equal(fixture.submitBtn.disabled, false, 'Submit button re-enabled');
});

test('chat-panel: polling for pending turn cleans up optimistic turn if server cancels/rolls back during polling', async () => {
    const fixture = setupFixture();
    let getCallCount = 0;
    let capturedTurnId = null;
    const mockFetch = async (url, options) => {
        if (options && options.method === 'POST') {
            capturedTurnId = JSON.parse(options.body).clientTurnId;
            const err = new Error('Client timeout');
            err.name = 'AbortError';
            throw err;
        }

        getCallCount++;
        if (getCallCount === 1) {
            // User message present
            return {
                ok: true,
                status: 200,
                json: async () => ({
                    success: true,
                    messages: [
                        { id: 201, clientTurnId: capturedTurnId, role: 'User', text: 'Tell me about charges.', status: 'Success' }
                    ]
                })
            };
        }

        // Server rolled back user message
        return {
            ok: true,
            status: 200,
            json: async () => ({
                success: true,
                messages: []
            })
        };
    };

    const controller = initChatPanel(fixture.doc, mockFetch, 5000, 10);
    fixture.input.value = 'Tell me about charges.';
    await controller.handleSubmit();

    assert.ok(getCallCount >= 2, `Poll GET must be called at least twice (actual: ${getCallCount})`);
    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 0, 'Optimistic user turn must be removed when server rolled back');
    assert.match(fixture.errorBanner.textContent, /cancelled or rolled back/i);
    assert.equal(fixture.input.disabled, false, 'Input re-enabled for retry');
    assert.equal(fixture.submitBtn.disabled, false, 'Submit button re-enabled');
});

test('chat-panel: reconciliation does not match on question text if clientTurnId differs (strict ID matching)', async () => {
    const fixture = setupFixture();
    let getCalled = false;
    const mockFetch = async (url, options) => {
        if (options && options.method === 'POST') {
            const err = new Error('Client timeout');
            err.name = 'AbortError';
            throw err;
        }
        getCalled = true;
        // Server has identical question text from an earlier turn or another session, but with a different clientTurnId
        return {
            ok: true,
            status: 200,
            json: async () => ({
                success: true,
                messages: [
                    { id: 999, clientTurnId: '00000000-0000-0000-0000-000000000001', role: 'User', text: 'Repeated question?', status: 'Success' },
                    { id: 1000, inReplyToChatMessageId: 999, clientTurnId: '00000000-0000-0000-0000-000000000001', role: 'Assistant', text: 'Old answer.', status: 'Success' }
                ]
            })
        };
    };

    const controller = initChatPanel(fixture.doc, mockFetch);
    fixture.input.value = 'Repeated question?';
    await controller.handleSubmit();

    assert.equal(getCalled, true);
    // Since clientTurnId did not match, it treats the current turn as uncommitted/rolled back and removes the optimistic turn
    const turns = fixture.messagesContainer.querySelectorAll('.mca-chat-turn');
    assert.equal(turns.length, 0, 'Optimistic turn removed because this specific turn ID was not persisted');
    assert.match(fixture.errorBanner.textContent, /timed out after 45 seconds/i);
});
