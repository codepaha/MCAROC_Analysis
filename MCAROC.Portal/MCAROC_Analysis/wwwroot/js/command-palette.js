/**
 * command-palette.js — Ctrl+K Command Palette Driver (#122, C7c)
 * Context-aware command palette with:
 * - Global Ctrl+K / Cmd+K toggle and Escape dismissal
 * - Focus trap, overlay click dismissal, and focus restoration
 * - ARIA combobox / listbox / option accessibility with stable IDs and aria-activedescendant
 * - On Company Details: context-aware per-tab AI prompt suggestions + canonical tab hash quick-jumps
 * - Off Details: global navigation and search actions only (no AI assistant off-page)
 * - Safe textContent rendering for dynamic search queries
 * - Direct integration with McaChatPanel.submitPrompt(prompt)
 */
(function (root, factory) {
    if (typeof exports === 'object' && typeof module !== 'undefined') {
        module.exports = factory();
    } else {
        root.McaCommandPalette = factory();
        if (typeof document !== 'undefined') {
            document.addEventListener('DOMContentLoaded', function () {
                root.McaCommandPalette.initCommandPalette();
            });
        }
    }
}(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const TAB_PROMPTS = {
        ai: [
            { text: 'Summarize key risk flags and synthesis', icon: 'bi-robot' },
            { text: 'What are the top credit strengths and weaknesses?', icon: 'bi-stars' },
            { text: 'Explain why Review Priority was assigned', icon: 'bi-shield-check' }
        ],
        corporate: [
            { text: 'Who are the current directors and key management?', icon: 'bi-people' },
            { text: 'Which directors have resigned or ceased?', icon: 'bi-person-dash' },
            { text: 'What is the promoter shareholding structure?', icon: 'bi-pie-chart' },
            { text: "What is the company's main business activity and MoA object?", icon: 'bi-building' }
        ],
        financials: [
            { text: 'What is the revenue and PAT trend over recent years?', icon: 'bi-graph-up' },
            { text: 'Summarize debt levels, leverage, and net worth', icon: 'bi-cash-stack' },
            { text: 'Are there differences between standalone and consolidated numbers?', icon: 'bi-intersect' }
        ],
        charges: [
            { text: 'List all open charges and total secured borrowing', icon: 'bi-lock' },
            { text: 'Which charges were recently modified or satisfied?', icon: 'bi-clock-history' },
            { text: 'Who are the primary charge holders and lending institutions?', icon: 'bi-bank' }
        ],
        compliance: [
            { text: 'Are there any delayed EPFO contribution payments?', icon: 'bi-calendar-check' },
            { text: 'Check GST registration and return filing regularity', icon: 'bi-file-earmark-check' },
            { text: 'Summarize auditor observations and qualifications', icon: 'bi-chat-left-text' }
        ],
        litigation: [
            { text: 'Are there any confirmed or probable legal disputes?', icon: 'bi-exclamation-triangle' },
            { text: 'List financial disputes with lenders or debt recovery tribunals', icon: 'bi-bank2' },
            { text: 'What is the total disputed amount across open suits?', icon: 'bi-currency-rupee' }
        ],
        timeline: [
            { text: 'What are the major corporate events in chronological order?', icon: 'bi-clock' },
            { text: 'Show key statutory filings and changes in the last 12 months', icon: 'bi-calendar3' }
        ],
        documents: [
            { text: 'Which filings and documents are available for download?', icon: 'bi-file-earmark-arrow-down' },
            { text: 'Search filings for auditor resignations or charter amendments', icon: 'bi-file-earmark-text' }
        ]
    };

    const TAB_JUMPS = [
        { label: 'AI Analysis', hash: '#tab-ai', icon: 'bi-robot' },
        { label: 'Corporate Overview', hash: '#tab-corporate', icon: 'bi-building' },
        { label: 'Financial Statements & Ratios', hash: '#tab-financials', icon: 'bi-cash-coin' },
        { label: 'Charges & Security', hash: '#tab-charges', icon: 'bi-lock' },
        { label: 'Compliance & Tax (GST / EPFO)', hash: '#tab-compliance', icon: 'bi-shield-check' },
        { label: 'Legal History & Litigation', hash: '#tab-litigation', icon: 'bi-journal-text' },
        { label: 'Corporate Timeline', hash: '#tab-timeline', icon: 'bi-calendar-range' },
        { label: 'Filed Documents & Search', hash: '#tab-documents', icon: 'bi-folder2-open' }
    ];

    const GLOBAL_ACTIONS = [
        { label: 'Dashboard', url: '/Dashboard', subtitle: 'Overview of searches and system status', icon: 'bi-grid-1x2' },
        { label: 'New Company Search', url: '/Requests/New', subtitle: 'Start new MCA extraction and analysis', icon: 'bi-plus-circle' },
        { label: 'Search History', url: '/Requests', subtitle: 'View previously analysed companies', icon: 'bi-clock-history' },
        { label: 'New Pre-login Report', url: '/pre-login-reports', subtitle: 'Generate public MCA ROC report', icon: 'bi-file-earmark-word' },
        { label: 'Toggle Dark / Light Theme', action: 'theme', subtitle: 'Switch color theme', icon: 'bi-moon-stars' }
    ];

    function initCommandPalette(options) {
        const d = (options && options.doc) || (typeof document !== 'undefined' ? document : null);
        const w = (options && options.window) || (typeof window !== 'undefined' ? window : null);
        const chatPanel = (options && options.chatPanel) || (typeof root !== 'undefined' && root.McaChatPanel ? root.McaChatPanel : (typeof McaChatPanel !== 'undefined' ? McaChatPanel : null));

        if (!d) return null;

        const overlay = d.getElementById('piCmdOverlay');
        const modal = d.getElementById('piCmdModal');
        const input = d.getElementById('piCmdInput');
        const hints = d.getElementById('piCmdHints');
        const triggerBtn = d.getElementById('piCmdBtn');

        if (!overlay || !input || !hints) return null;

        let isOpen = false;
        let activeIndex = 0;
        let currentOptions = [];
        let previousActiveElement = null;

        function isDetailsPage() {
            return !!(d.getElementById('mcaDetailHead') || d.getElementById('mcaTabs'));
        }

        function getActiveDomain() {
            if (!isDetailsPage()) return 'ai';
            const activeTabBtn = (d.querySelector && d.querySelector('#mcaTabs button[data-bs-toggle="tab"].active'))
                || (d.querySelector && d.querySelector('#mcaTabs button[data-bs-toggle="tab"]'));
            if (!activeTabBtn) return 'ai';
            const target = activeTabBtn.getAttribute('data-bs-target') || '';
            const domain = target.replace('#tab-', '').toLowerCase();
            return TAB_PROMPTS[domain] ? domain : 'ai';
        }

        function buildOptionsList(queryText) {
            const query = (queryText || '').trim().toLowerCase();
            const list = [];
            const onDetails = isDetailsPage();

            if (onDetails) {
                const domain = getActiveDomain();
                const domainPrompts = TAB_PROMPTS[domain] || TAB_PROMPTS.ai;

                // Suggested prompts (matching query if typed, or default)
                const matchedPrompts = domainPrompts.filter(p => !query || p.text.toLowerCase().includes(query));
                matchedPrompts.forEach(p => {
                    list.push({
                        category: 'Suggested Prompts (' + domain.toUpperCase() + ')',
                        type: 'ai-prompt',
                        title: p.text,
                        subtitle: 'Ask Document Assistant (full company records)',
                        icon: p.icon || 'bi-stars',
                        value: p.text
                    });
                });

                // If user typed a custom question not already in the list, offer free-text ask
                if (query.length > 0) {
                    const exactMatch = list.some(item => item.value.toLowerCase() === query);
                    if (!exactMatch) {
                        list.unshift({
                            category: 'Ask Question',
                            type: 'ai-prompt',
                            title: 'Ask Document Assistant: "' + queryText.trim() + '"',
                            subtitle: 'Query the full company corpus with this question',
                            icon: 'bi-robot',
                            value: queryText.trim()
                        });
                    }
                }

                // Tab quick-jumps
                const matchedJumps = TAB_JUMPS.filter(j => !query || j.label.toLowerCase().includes(query) || j.hash.includes(query));
                matchedJumps.forEach(j => {
                    list.push({
                        category: 'Jump to Section / Tab',
                        type: 'hash-jump',
                        title: j.label,
                        subtitle: 'Navigate to ' + j.hash,
                        icon: j.icon || 'bi-arrow-right-circle',
                        value: j.hash
                    });
                });
            }

            // Global navigation & actions (available on all pages)
            const matchedGlobals = GLOBAL_ACTIONS.filter(g => !query || g.label.toLowerCase().includes(query) || (g.subtitle && g.subtitle.toLowerCase().includes(query)));
            matchedGlobals.forEach(g => {
                list.push({
                    category: 'Navigation',
                    type: g.action ? 'action' : 'nav',
                    title: g.label,
                    subtitle: g.subtitle || '',
                    icon: g.icon || 'bi-link-45deg',
                    value: g.action ? g.action : g.url
                });
            });

            return list;
        }

        function renderOptions() {
            if (!hints) return;
            hints.innerHTML = '';
            currentOptions = buildOptionsList(input.value);

            if (currentOptions.length === 0) {
                const empty = d.createElement('div');
                empty.className = 'p-3 text-muted text-center small';
                empty.textContent = 'No matching commands or prompts.';
                hints.appendChild(empty);
                input.removeAttribute('aria-activedescendant');
                return;
            }

            if (activeIndex >= currentOptions.length) activeIndex = 0;
            if (activeIndex < 0) activeIndex = currentOptions.length - 1;

            let currentCategory = null;

            currentOptions.forEach((opt, idx) => {
                if (opt.category !== currentCategory) {
                    currentCategory = opt.category;
                    const catHeader = d.createElement('div');
                    catHeader.className = 'pi-cmd-hint-label';
                    catHeader.textContent = currentCategory;
                    hints.appendChild(catHeader);
                }

                const item = d.createElement('div');
                item.className = 'pi-cmd-item' + (idx === activeIndex ? ' active' : '');
                item.id = 'piCmdOption-' + idx;
                item.setAttribute('role', 'option');
                item.setAttribute('aria-selected', idx === activeIndex ? 'true' : 'false');

                const icon = d.createElement('i');
                icon.className = 'bi ' + (opt.icon || 'bi-stars');
                item.appendChild(icon);

                const content = d.createElement('div');
                content.style.flex = '1 1 auto';
                content.style.minWidth = '0';

                const strong = d.createElement('strong');
                strong.textContent = opt.title;
                content.appendChild(strong);

                if (opt.subtitle) {
                    const small = d.createElement('small');
                    small.textContent = opt.subtitle;
                    content.appendChild(small);
                }

                item.appendChild(content);

                item.addEventListener('click', function () {
                    executeOption(opt);
                });

                hints.appendChild(item);
            });

            input.setAttribute('aria-activedescendant', 'piCmdOption-' + activeIndex);
            scrollActiveIntoView();
        }

        function scrollActiveIntoView() {
            if (!hints) return;
            const activeEl = d.getElementById('piCmdOption-' + activeIndex);
            if (activeEl && typeof activeEl.scrollIntoView === 'function') {
                activeEl.scrollIntoView({ block: 'nearest' });
            }
        }

        function executeOption(opt) {
            if (!opt) return;
            close();

            if (opt.type === 'ai-prompt') {
                if (chatPanel && typeof chatPanel.submitPrompt === 'function') {
                    chatPanel.submitPrompt(opt.value, d);
                } else if (typeof root !== 'undefined' && root.McaChatPanel && typeof root.McaChatPanel.submitPrompt === 'function') {
                    root.McaChatPanel.submitPrompt(opt.value, d);
                }
            } else if (opt.type === 'hash-jump') {
                if (w && w.location) {
                    w.location.hash = opt.value;
                }
            } else if (opt.type === 'nav') {
                if (w && w.location) {
                    w.location.href = opt.value;
                }
            } else if (opt.type === 'action' && opt.value === 'theme') {
                const themeBtn = d.getElementById('piThemeToggle');
                if (themeBtn && typeof themeBtn.click === 'function') {
                    themeBtn.click();
                }
            }
        }

        function open() {
            if (isOpen) return;
            previousActiveElement = d.activeElement;
            isOpen = true;
            activeIndex = 0;

            overlay.removeAttribute('hidden');
            overlay.classList.add('open');
            input.setAttribute('aria-expanded', 'true');
            if (triggerBtn) triggerBtn.setAttribute('aria-expanded', 'true');

            input.value = '';
            renderOptions();
            if (input.focus) input.focus();
        }

        function close() {
            if (!isOpen) return;
            isOpen = false;

            overlay.classList.remove('open');
            overlay.setAttribute('hidden', 'true');
            input.setAttribute('aria-expanded', 'false');
            input.removeAttribute('aria-activedescendant');
            if (triggerBtn) triggerBtn.setAttribute('aria-expanded', 'false');

            if (previousActiveElement && typeof previousActiveElement.focus === 'function') {
                try {
                    previousActiveElement.focus();
                } catch (e) {}
            } else if (triggerBtn && typeof triggerBtn.focus === 'function') {
                triggerBtn.focus();
            }
        }

        function toggle() {
            if (isOpen) close();
            else open();
        }

        // ── Input & Navigation Events ──
        input.addEventListener('input', function () {
            activeIndex = 0;
            renderOptions();
        });

        input.addEventListener('keydown', function (e) {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                if (currentOptions.length > 0) {
                    activeIndex = (activeIndex + 1) % currentOptions.length;
                    renderOptions();
                }
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                if (currentOptions.length > 0) {
                    activeIndex = (activeIndex - 1 + currentOptions.length) % currentOptions.length;
                    renderOptions();
                }
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (currentOptions.length > 0 && currentOptions[activeIndex]) {
                    executeOption(currentOptions[activeIndex]);
                }
            } else if (e.key === 'Escape') {
                e.preventDefault();
                close();
            } else if (e.key === 'Tab') {
                // Focus trap: keep focus inside the palette modal
                e.preventDefault();
            }
        });

        // Overlay click outside modal dismisses
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) {
                close();
            }
        });

        if (triggerBtn) {
            triggerBtn.addEventListener('click', function (e) {
                e.preventDefault();
                toggle();
            });
        }

        // Global shortcut: Ctrl+K / Cmd+K
        function onGlobalKeyDown(e) {
            if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
                e.preventDefault();
                toggle();
            } else if (e.key === 'Escape' && isOpen) {
                close();
            }
        }

        if (d.addEventListener) {
            d.addEventListener('keydown', onGlobalKeyDown);
        }

        return {
            open,
            close,
            toggle,
            getIsOpen: () => isOpen,
            executeOption,
            buildOptionsList
        };
    }

    return {
        initCommandPalette,
        TAB_PROMPTS,
        TAB_JUMPS,
        GLOBAL_ACTIONS
    };
}));
