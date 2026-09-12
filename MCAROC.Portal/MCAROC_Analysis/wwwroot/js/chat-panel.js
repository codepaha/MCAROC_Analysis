/**
 * chat-panel.js — Docked Omnipresent Document Assistant Panel
 * Hardened client driver: flight locks, AbortController timeouts, JSON headers,
 * accessible drawer focus management, and strict textContent DOM encoding.
 */
(function () {
    'use strict';

    const trigger = document.getElementById('mcaChatTrigger');
    const panel = document.getElementById('mcaChatPanel');
    const closeBtn = document.getElementById('mcaChatClose');
    const form = document.getElementById('mcaChatForm');
    const input = document.getElementById('mcaChatInput');
    const submitBtn = document.getElementById('mcaChatSubmit');
    const messagesContainer = document.getElementById('mcaChatMessages');
    const errorBanner = document.getElementById('mcaChatError');
    const emptyState = document.getElementById('mcaChatEmpty');
    const charCount = document.getElementById('mcaCharCount');
    const suggestions = document.querySelectorAll('.mca-suggestion-btn');
    const badge = document.getElementById('mcaChatBadge');

    if (!panel || !form || !input || !trigger) return;

    const requestId = form.getAttribute('data-request-id');
    let isSubmitting = false;
    let abortController = null;

    // ── Drawer Open / Close & Focus Management ──
    function openPanel() {
        panel.hidden = false;
        panel.classList.add('mca-panel-open');
        trigger.setAttribute('aria-expanded', 'true');
        input.focus();
        scrollToBottom();
    }

    function closePanel() {
        panel.classList.remove('mca-panel-open');
        panel.hidden = true;
        trigger.setAttribute('aria-expanded', 'false');
        trigger.focus();
    }

    trigger.addEventListener('click', function () {
        if (panel.hidden) openPanel();
        else closePanel();
    });

    if (closeBtn) closeBtn.addEventListener('click', closePanel);

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && !panel.hidden) {
            closePanel();
        }
    });

    // ── Character Count & Enter Key Handling ──
    input.addEventListener('input', function () {
        if (charCount) charCount.textContent = input.value.length;
    });

    input.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            if (!isSubmitting && input.value.trim().length > 0) {
                form.requestSubmit();
            }
        }
    });

    // ── Suggestions ──
    suggestions.forEach(function (btn) {
        btn.addEventListener('click', function () {
            input.value = btn.textContent.trim();
            if (charCount) charCount.textContent = input.value.length;
            input.focus();
        });
    });

    function scrollToBottom() {
        const body = document.getElementById('mcaChatBody');
        if (body) body.scrollTop = body.scrollHeight;
    }

    function showError(message) {
        if (!errorBanner) return;
        errorBanner.textContent = message;
        errorBanner.style.display = 'block';
        scrollToBottom();
    }

    function hideError() {
        if (!errorBanner) return;
        errorBanner.style.display = 'none';
        errorBanner.textContent = '';
    }

    function createBubbleElement(role, text, status, citations, createdDate) {
        const isUser = role.toLowerCase() === 'user';
        const turnDiv = document.createElement('div');
        turnDiv.className = 'mca-chat-turn ' + (isUser ? 'user' : 'assistant');

        const bubble = document.createElement('div');
        bubble.className = 'mca-chat-bubble';

        const header = document.createElement('div');
        header.className = 'mca-chat-bubble-header';

        const strong = document.createElement('strong');
        strong.textContent = isUser ? 'You' : 'Assistant';
        header.appendChild(strong);

        const small = document.createElement('small');
        small.className = 'text-muted ms-2';
        const d = createdDate ? new Date(createdDate) : new Date();
        small.textContent = d.getHours().toString().padStart(2, '0') + ':' + d.getMinutes().toString().padStart(2, '0');
        header.appendChild(small);

        if (status && status.toLowerCase() === 'failed') {
            const badgeSpan = document.createElement('span');
            badgeSpan.className = 'badge bg-danger ms-1';
            badgeSpan.textContent = 'Failed';
            header.appendChild(badgeSpan);
        }
        bubble.appendChild(header);

        const textDiv = document.createElement('div');
        textDiv.className = 'mca-chat-text';
        textDiv.textContent = text;
        bubble.appendChild(textDiv);

        if (citations && citations.length > 0) {
            const citationsDiv = document.createElement('div');
            citationsDiv.className = 'mca-chat-citations';

            const citeTitle = document.createElement('small');
            citeTitle.className = 'text-muted d-block mb-1';
            citeTitle.textContent = 'Sources:';
            citationsDiv.appendChild(citeTitle);

            citations.forEach(function (cit) {
                const display = (cit.sourceType === 'DocumentChunk' && cit.documentName && cit.pageNumber)
                    ? (cit.documentName + ', page ' + cit.pageNumber)
                    : (cit.label || 'Source reference');

                if (cit.viewerUrl) {
                    const a = document.createElement('a');
                    a.className = 'mca-citation';
                    a.href = cit.viewerUrl;
                    a.target = '_blank';
                    a.rel = 'noopener';

                    const icon = document.createElement('i');
                    icon.className = 'bi bi-file-earmark-pdf me-1';
                    a.appendChild(icon);

                    const textSpan = document.createTextNode(display);
                    a.appendChild(textSpan);
                    citationsDiv.appendChild(a);
                } else {
                    const span = document.createElement('span');
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

    // ── Form Submission ──
    form.addEventListener('submit', async function (e) {
        e.preventDefault();
        if (isSubmitting) return;

        const question = input.value.trim();
        if (!question) {
            showError('Question cannot be empty.');
            return;
        }
        if (question.length > 1000) {
            showError('Question exceeds the maximum length of 1,000 characters.');
            return;
        }

        hideError();
        isSubmitting = true;
        input.disabled = true;
        submitBtn.disabled = true;
        submitBtn.innerHTML = '<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span>';

        if (emptyState) emptyState.remove();

        // Immediately append user's turn
        const userTurn = createBubbleElement('User', question, 'Success', [], new Date());
        messagesContainer.appendChild(userTurn);
        scrollToBottom();

        // Extract token
        const tokenInput = form.querySelector('input[name="__RequestVerificationToken"]');
        const token = tokenInput ? tokenInput.value : '';

        abortController = new AbortController();
        const timeoutId = setTimeout(function () {
            abortController.abort();
        }, 45000);

        try {
            const response = await fetch('/Requests/' + encodeURIComponent(requestId) + '/chat', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'RequestVerificationToken': token,
                    'X-Requested-With': 'XMLHttpRequest'
                },
                body: JSON.stringify({ question: question }),
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
                    data.message.createdDate
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
                    data.message.createdDate
                );
                messagesContainer.appendChild(assistantTurn);
                showError(data.error && data.error.message ? data.error.message : 'Upstream AI completion failed.');
            } else if (data && data.error && data.error.message) {
                showError(data.error.message);
            } else if (response.status === 400) {
                showError('Invalid request or expired verification token. Please refresh.');
            } else if (response.status === 404) {
                showError('Request record was not found.');
            } else {
                showError('An unexpected error occurred (' + response.status + ').');
            }
        } catch (err) {
            clearTimeout(timeoutId);
            if (err.name === 'AbortError') {
                showError('Request timed out after 45 seconds. Please try again.');
            } else {
                showError('Network error connecting to assistant.');
            }
        } finally {
            isSubmitting = false;
            input.disabled = false;
            submitBtn.disabled = false;
            submitBtn.innerHTML = '<i class="bi bi-send-fill"></i>';
            input.focus();
            scrollToBottom();
        }
    });
})();
