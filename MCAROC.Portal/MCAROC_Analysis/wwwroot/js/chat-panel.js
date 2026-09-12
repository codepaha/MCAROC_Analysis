/**
 * chat-panel.js — Docked Omnipresent Document Assistant Panel
 * Hardened client driver: flight locks, AbortController timeouts, JSON headers,
 * accessible drawer focus management, strict textContent DOM encoding,
 * and optimistic turn rollback on cancellation/timeout/network errors.
 */
(function (root, factory) {
    if (typeof exports === 'object' && typeof module !== 'undefined') {
        module.exports = factory();
    } else {
        root.McaChatPanel = factory();
        if (typeof document !== 'undefined') {
            root.McaChatPanel.initChatPanel();
        }
    }
}(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    function createBubbleElement(role, text, status, citations, createdDate, doc) {
        const d = doc || (typeof document !== 'undefined' ? document : null);
        if (!d) return null;

        const isUser = role.toLowerCase() === 'user';
        const turnDiv = d.createElement('div');
        turnDiv.className = 'mca-chat-turn ' + (isUser ? 'user' : 'assistant');

        const bubble = d.createElement('div');
        bubble.className = 'mca-chat-bubble';

        const header = d.createElement('div');
        header.className = 'mca-chat-bubble-header';

        const strong = d.createElement('strong');
        strong.textContent = isUser ? 'You' : 'Assistant';
        header.appendChild(strong);

        const small = d.createElement('small');
        small.className = 'text-muted ms-2';
        const dateObj = createdDate ? new Date(createdDate) : new Date();
        small.textContent = dateObj.getHours().toString().padStart(2, '0') + ':' + dateObj.getMinutes().toString().padStart(2, '0');
        header.appendChild(small);

        if (status && status.toLowerCase() === 'failed') {
            const badgeSpan = d.createElement('span');
            badgeSpan.className = 'badge bg-danger ms-1';
            badgeSpan.textContent = 'Failed';
            header.appendChild(badgeSpan);
        }
        bubble.appendChild(header);

        const textDiv = d.createElement('div');
        textDiv.className = 'mca-chat-text';
        textDiv.textContent = text;
        bubble.appendChild(textDiv);

        if (citations && citations.length > 0) {
            const citationsDiv = d.createElement('div');
            citationsDiv.className = 'mca-chat-citations';

            const citeTitle = d.createElement('small');
            citeTitle.className = 'text-muted d-block mb-1';
            citeTitle.textContent = 'Sources:';
            citationsDiv.appendChild(citeTitle);

            citations.forEach(function (cit) {
                const display = (cit.sourceType === 'DocumentChunk' && cit.documentName && cit.pageNumber)
                    ? (cit.documentName + ', page ' + cit.pageNumber)
                    : (cit.label || 'Source reference');

                if (cit.viewerUrl) {
                    const a = d.createElement('a');
                    a.className = 'mca-citation';
                    a.href = cit.viewerUrl;
                    a.target = '_blank';
                    a.rel = 'noopener';

                    const icon = d.createElement('i');
                    icon.className = 'bi bi-file-earmark-pdf me-1';
                    a.appendChild(icon);

                    const textSpan = d.createTextNode(display);
                    a.appendChild(textSpan);
                    citationsDiv.appendChild(a);
                } else {
                    const span = d.createElement('span');
                    span.className = 'mca-citation-plain';
                    span.textContent = display;
                    citationsDiv.appendChild(span);
                }
            });

            bubble.appendChild(citationsDiv);
        }

        turnDiv.appendChild(bubble);
        return turnDiv;
    }

    function generateUUID() {
        if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
            return crypto.randomUUID();
        }
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
            const r = Math.random() * 16 | 0;
            const v = c === 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }

    function initChatPanel(doc, fetchFn, timeoutOverrideMs, pollIntervalOverrideMs) {
        const d = doc || (typeof document !== 'undefined' ? document : null);
        if (!d) return null;

        const trigger = d.getElementById('mcaChatTrigger');
        const panel = d.getElementById('mcaChatPanel');
        const closeBtn = d.getElementById('mcaChatClose');
        const form = d.getElementById('mcaChatForm');
        const input = d.getElementById('mcaChatInput');
        const submitBtn = d.getElementById('mcaChatSubmit');
        const messagesContainer = d.getElementById('mcaChatMessages');
        const errorBanner = d.getElementById('mcaChatError');
        const emptyState = d.getElementById('mcaChatEmpty');
        const charCount = d.getElementById('mcaCharCount');
        const suggestions = d.querySelectorAll ? d.querySelectorAll('.mca-suggestion-btn') : [];
        const badge = d.getElementById('mcaChatBadge');

        if (!panel || !form || !input || !trigger || !messagesContainer) return null;

        const requestId = form.getAttribute('data-request-id');
        let isSubmitting = false;
        let abortController = null;
        const fetchImpl = fetchFn || (typeof fetch !== 'undefined' ? fetch : null);
        const timeoutMs = typeof timeoutOverrideMs === 'number' ? timeoutOverrideMs : 45000;
        const pollIntervalMs = typeof pollIntervalOverrideMs === 'number' ? pollIntervalOverrideMs : 1500;

        // ── Drawer Open / Close & Focus Management ──
        function openPanel() {
            panel.hidden = false;
            if (panel.classList && panel.classList.add) {
                panel.classList.add('mca-panel-open');
            }
            trigger.setAttribute('aria-expanded', 'true');
            if (input.focus) input.focus();
            scrollToBottom();
        }

        function closePanel() {
            if (panel.classList && panel.classList.remove) {
                panel.classList.remove('mca-panel-open');
            }
            panel.hidden = true;
            trigger.setAttribute('aria-expanded', 'false');
            if (trigger.focus) trigger.focus();
        }

        trigger.addEventListener('click', function () {
            if (panel.hidden) openPanel();
            else closePanel();
        });

        if (closeBtn) closeBtn.addEventListener('click', closePanel);

        d.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && !panel.hidden) {
                closePanel();
            }
        });

        // ── Character Count & Enter Key Handling ──
        input.addEventListener('input', function () {
            if (charCount) charCount.textContent = (input.value || '').length;
        });

        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                if (e.preventDefault) e.preventDefault();
                if (!isSubmitting && input.value && input.value.trim().length > 0) {
                    if (form.requestSubmit) {
                        form.requestSubmit();
                    } else if (form.dispatchEvent && typeof Event !== 'undefined') {
                        form.dispatchEvent(new Event('submit', { cancelable: true }));
                    }
                }
            }
        });

        // ── Suggestions ──
        if (suggestions && suggestions.forEach) {
            suggestions.forEach(function (btn) {
                btn.addEventListener('click', function () {
                    input.value = (btn.textContent || '').trim();
                    if (charCount) charCount.textContent = input.value.length;
                    if (input.focus) input.focus();
                });
            });
        }

        function scrollToBottom() {
            const body = d.getElementById('mcaChatBody');
            if (body) body.scrollTop = body.scrollHeight;
        }

        function showError(message) {
            if (!errorBanner) return;
            errorBanner.textContent = message;
            if (errorBanner.style) errorBanner.style.display = 'block';
            scrollToBottom();
        }

        function hideError() {
            if (!errorBanner) return;
            if (errorBanner.style) errorBanner.style.display = 'none';
            errorBanner.textContent = '';
        }

        function renderTranscript(messages) {
            const existingTurns = messagesContainer.querySelectorAll ? messagesContainer.querySelectorAll('.mca-chat-turn') : [];
            if (existingTurns && existingTurns.forEach) {
                existingTurns.forEach(function (t) { if (t.remove) t.remove(); });
            } else if (existingTurns) {
                for (let i = existingTurns.length - 1; i >= 0; i--) {
                    if (existingTurns[i].remove) existingTurns[i].remove();
                }
            }
            if (emptyState && emptyState.remove) emptyState.remove();

            if (!messages || messages.length === 0) {
                if (emptyState) messagesContainer.appendChild(emptyState);
            } else {
                messages.forEach(function (msg) {
                    const bubble = createBubbleElement(
                        msg.role || 'Assistant',
                        msg.text || '',
                        msg.status || 'Success',
                        msg.citations || [],
                        msg.createdDate,
                        d
                    );
                    if (bubble) messagesContainer.appendChild(bubble);
                });
            }

            if (badge) {
                badge.textContent = (messages ? messages.length : 0).toString();
            }
            scrollToBottom();
        }

        function markTurnIndeterminate(turnEl) {
            if (!turnEl) return;
            const header = turnEl.querySelector ? turnEl.querySelector('.mca-chat-bubble-header') : null;
            if (header) {
                const badgeSpan = d.createElement('span');
                badgeSpan.className = 'badge bg-warning text-dark ms-1 mca-delivery-unconfirmed';
                badgeSpan.textContent = 'Delivery Unconfirmed';
                header.appendChild(badgeSpan);
            }
        }

        async function pollPendingTurn(turnId, userMsgId, userTurnEl) {
            const pendingTurn = d.createElement('div');
            pendingTurn.className = 'mca-chat-turn assistant mca-chat-turn-pending';

            const bubble = d.createElement('div');
            bubble.className = 'mca-chat-bubble';

            const header = d.createElement('div');
            header.className = 'mca-chat-bubble-header';
            const strong = d.createElement('strong');
            strong.textContent = 'Assistant';
            header.appendChild(strong);
            bubble.appendChild(header);

            const textDiv = d.createElement('div');
            textDiv.className = 'mca-chat-text text-muted';
            const spinner = d.createElement('span');
            spinner.className = 'spinner-border spinner-border-sm me-2';
            spinner.setAttribute('role', 'status');
            spinner.setAttribute('aria-hidden', 'true');
            textDiv.appendChild(spinner);
            const thinkingText = d.createTextNode('Thinking...');
            textDiv.appendChild(thinkingText);
            bubble.appendChild(textDiv);
            pendingTurn.appendChild(bubble);

            messagesContainer.appendChild(pendingTurn);
            scrollToBottom();

            const maxAttempts = 20;
            let resolved = false;

            for (let attempt = 0; attempt < maxAttempts; attempt++) {
                await new Promise(function (res) { setTimeout(res, pollIntervalMs); });

                try {
                    const pollRes = await fetchImpl('/Requests/' + encodeURIComponent(requestId) + '/chat', {
                        method: 'GET',
                        headers: { 'X-Requested-With': 'XMLHttpRequest' }
                    });

                    if (pollRes && pollRes.ok) {
                        const pollHistory = await pollRes.json().catch(function () { return null; });
                        if (pollHistory && pollHistory.success && Array.isArray(pollHistory.messages)) {
                            const uMsg = pollHistory.messages.find(function (m) {
                                return m.clientTurnId && m.clientTurnId.toLowerCase() === turnId.toLowerCase();
                            });

                            if (uMsg) {
                                const targetUserMsgId = uMsg.id || userMsgId;
                                const asst = pollHistory.messages.find(function (m) {
                                    return (m.role || '').toLowerCase() === 'assistant'
                                        && ((m.clientTurnId && m.clientTurnId.toLowerCase() === turnId.toLowerCase())
                                            || (targetUserMsgId && m.inReplyToChatMessageId === targetUserMsgId));
                                });

                                if (asst) {
                                    resolved = true;
                                    if (pendingTurn && pendingTurn.remove) pendingTurn.remove();
                                    renderTranscript(pollHistory.messages);
                                    if (asst.status && asst.status.toLowerCase() === 'failed') {
                                        showError('Sorry, something went wrong answering that question. Please try again.');
                                    } else {
                                        hideError();
                                    }
                                    input.value = '';
                                    if (charCount) charCount.textContent = '0';
                                    break;
                                }
                            } else {
                                // User turn was deleted / rolled back by server
                                resolved = true;
                                if (pendingTurn && pendingTurn.remove) pendingTurn.remove();
                                if (userTurnEl && userTurnEl.remove) userTurnEl.remove();
                                if (emptyState && messagesContainer.querySelectorAll && messagesContainer.querySelectorAll('.mca-chat-turn').length === 0) {
                                    messagesContainer.appendChild(emptyState);
                                }
                                showError('Request was cancelled or rolled back by server; please try again.');
                                break;
                            }
                        }
                    }
                } catch (e) {
                    // Polling network blip, retry next attempt
                }
            }

            if (!resolved) {
                if (pendingTurn && pendingTurn.remove) pendingTurn.remove();
                markTurnIndeterminate(userTurnEl);
                showError('Assistant response is taking longer than expected. Please refresh to check for the latest reply.');
            }
        }

        // ── Form Submission ──
        async function handleSubmit(e) {
            if (e && e.preventDefault) e.preventDefault();
            if (isSubmitting) return;

            const question = (input.value || '').trim();
            if (!question) {
                showError('Question cannot be empty.');
                return;
            }
            if (question.length > 1000) {
                showError('Question exceeds the maximum length of 1,000 characters.');
                return;
            }

            const clientTurnId = generateUUID();

            hideError();
            isSubmitting = true;
            input.disabled = true;
            submitBtn.disabled = true;
            submitBtn.innerHTML = '<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span>';

            if (emptyState && emptyState.remove) emptyState.remove();

            // Immediately append user's turn
            const userTurn = createBubbleElement('User', question, 'Success', [], new Date(), d);
            if (userTurn && userTurn.setAttribute) userTurn.setAttribute('data-client-turn-id', clientTurnId);
            messagesContainer.appendChild(userTurn);
            scrollToBottom();

            // Extract token
            const tokenInput = form.querySelector ? form.querySelector('input[name="__RequestVerificationToken"]') : null;
            const token = tokenInput ? tokenInput.value : '';

            abortController = new AbortController();
            const timeoutId = setTimeout(function () {
                abortController.abort();
            }, timeoutMs);

            try {
                const response = await fetchImpl('/Requests/' + encodeURIComponent(requestId) + '/chat', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': token,
                        'X-Requested-With': 'XMLHttpRequest'
                    },
                    body: JSON.stringify({ question: question, clientTurnId: clientTurnId }),
                    signal: abortController.signal
                });

                clearTimeout(timeoutId);
                const data = await response.json().catch(function () { return null; });

                if (response.ok && data && data.success && data.message) {
                    const assistantTurn = createBubbleElement(
                        data.message.role || 'Assistant',
                        data.message.text || '',
                        data.message.status || 'Success',
                        data.message.citations || [],
                        data.message.createdDate,
                        d
                    );
                    messagesContainer.appendChild(assistantTurn);
                    input.value = '';
                    if (charCount) charCount.textContent = '0';
                    if (badge) {
                        const currentCount = parseInt(badge.textContent || '0', 10);
                        badge.textContent = (currentCount + 2).toString();
                    }
                } else if (response.status === 502 && data && data.message) {
                    // Render persisted failed assistant message from server so client and DB stay aligned
                    const assistantTurn = createBubbleElement(
                        data.message.role || 'Assistant',
                        data.message.text || 'Sorry, something went wrong answering that question. Please try again.',
                        'Failed',
                        [],
                        data.message.createdDate,
                        d
                    );
                    messagesContainer.appendChild(assistantTurn);
                    showError(data.error && data.error.message ? data.error.message : 'Upstream AI completion failed.');
                } else {
                    // Server returned an explicit error response (400, 404, 500, etc.) confirming rejection.
                    // Remove optimistic userTurn so visible UI never diverges from the database.
                    if (userTurn && userTurn.remove) {
                        userTurn.remove();
                    }
                    if (emptyState && messagesContainer.querySelectorAll && messagesContainer.querySelectorAll('.mca-chat-turn').length === 0) {
                        messagesContainer.appendChild(emptyState);
                    }

                    if (data && data.error && data.error.message) {
                        showError(data.error.message);
                    } else if (response.status === 400) {
                        showError('Invalid request or expired verification token. Please refresh.');
                    } else if (response.status === 404) {
                        showError('Request record was not found.');
                    } else {
                        showError('An unexpected error occurred (' + response.status + ').');
                    }
                }
            } catch (err) {
                clearTimeout(timeoutId);

                // Indeterminate delivery state: client timeout or transport error can race a turn
                // that the server actually committed. Reconcile with the server before assuming rollback.
                let reconciled = false;
                try {
                    const reconcileRes = await fetchImpl('/Requests/' + encodeURIComponent(requestId) + '/chat', {
                        method: 'GET',
                        headers: {
                            'X-Requested-With': 'XMLHttpRequest'
                        }
                    });

                    if (reconcileRes && reconcileRes.ok) {
                        const history = await reconcileRes.json().catch(function () { return null; });
                        if (history && history.success && Array.isArray(history.messages)) {
                            reconciled = true;
                            const userMsg = history.messages.find(function (m) {
                                return m.clientTurnId && m.clientTurnId.toLowerCase() === clientTurnId.toLowerCase();
                            });

                            if (userMsg) {
                                const assistantMsg = history.messages.find(function (m) {
                                    return (m.role || '').toLowerCase() === 'assistant'
                                        && ((m.clientTurnId && m.clientTurnId.toLowerCase() === clientTurnId.toLowerCase())
                                            || (userMsg.id && m.inReplyToChatMessageId === userMsg.id));
                                });

                                if (assistantMsg) {
                                    // Server committed full turn: render canonical transcript
                                    renderTranscript(history.messages);
                                    hideError();
                                    input.value = '';
                                    if (charCount) charCount.textContent = '0';
                                } else {
                                    // Server committed user question, but assistant is still generating (in-flight pending turn)
                                    await pollPendingTurn(clientTurnId, userMsg.id, userTurn);
                                    return;
                                }
                            } else {
                                // Server confirmed the question was NOT persisted (it was cancelled / rolled back).
                                if (userTurn && userTurn.remove) {
                                    userTurn.remove();
                                }
                                if (emptyState && messagesContainer.querySelectorAll && messagesContainer.querySelectorAll('.mca-chat-turn').length === 0) {
                                    messagesContainer.appendChild(emptyState);
                                }
                                if (err && err.name === 'AbortError') {
                                    showError('Request timed out after 45 seconds. Delivery was not completed; please try again.');
                                } else {
                                    showError('Network error connecting to assistant. Delivery was not completed; please try again.');
                                }
                            }
                        }
                    }
                } catch (reconErr) {
                    // Reconcile attempt failed (true offline / unreachable host)
                }

                if (!reconciled) {
                    // Server state could not be verified. Do NOT claim rollback or DB alignment.
                    markTurnIndeterminate(userTurn);
                    showError('Network error. Delivery status could not be verified with server. Please refresh before retrying.');
                }
            } finally {
                isSubmitting = false;
                input.disabled = false;
                submitBtn.disabled = false;
                submitBtn.innerHTML = '<i class="bi bi-send-fill"></i>';
                if (input.focus) input.focus();
                scrollToBottom();
            }
        }

        form.addEventListener('submit', handleSubmit);

        return {
            openPanel,
            closePanel,
            handleSubmit,
            getIsSubmitting: () => isSubmitting
        };
    }

    return {
        initChatPanel,
        createBubbleElement
    };
}));
