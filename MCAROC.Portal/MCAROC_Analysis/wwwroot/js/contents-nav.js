/**
 * contents-nav.js — Per-Tab Sticky Contents Nav + Scroll-Spy & Unified Details Navigation
 * Single authoritative navigation controller for Company Details:
 * - Hash resolution (canonical #sec-*, legacy #tab-{domain}/{section}, tab-only #tab-*)
 * - Tab activation with already-active readiness (no hanging for shown.bs.tab)
 * - Deterministic Bootstrap Collapse for ?charge= flow without subtab dependencies
 * - Dual ResizeObserver height tracking (--mca-detail-head-h and --mca-contents-nav-h)
 * - Authoritative ScrollSpy lifecycle (re-targeting on long tabs, disposal on non-long tabs)
 * - Main and secondary subtab sessionStorage persistence
 * - Basis toggle ([data-basis-group]) & Documents paged fetch-and-swap
 * - prefers-reduced-motion accessibility
 */
(function (root, factory) {
    if (typeof exports === 'object' && typeof module !== 'undefined') {
        module.exports = factory();
    } else {
        root.McaContentsNav = factory();
        if (typeof document !== 'undefined') {
            document.addEventListener('DOMContentLoaded', function () {
                root.McaContentsNav.initDetailsNav();
            });
        }
    }
}(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    const LONG_DOMAINS = ['financials', 'charges', 'compliance', 'litigation'];

    // Known canonical section IDs mapped by domain
    const CANONICAL_SECTIONS = {
        financials: [
            'sec-financials-summary',
            'sec-financials-pl',
            'sec-financials-bs',
            'sec-financials-cf',
            'sec-financials-ratios',
            'sec-financials-indicators',
            'sec-financials-peers'
        ],
        charges: [
            'sec-charges-summary',
            'sec-charges-open',
            'sec-charges-satisfied'
        ],
        compliance: [
            'sec-compliance-mca',
            'sec-compliance-gst',
            'sec-compliance-epfo',
            'sec-compliance-msme',
            'sec-compliance-auditor',
            'sec-compliance-credit-ratings'
        ],
        litigation: [
            'sec-litigation-summary',
            'sec-litigation-confirmed',
            'sec-litigation-probable',
            'sec-litigation-unverified',
            'sec-litigation-financial-disputes'
        ]
    };

    // Legacy subtab slug mappings to canonical section ID
    const LEGACY_SLUG_MAP = {
        financials: {
            summary: 'sec-financials-summary',
            pl: 'sec-financials-pl',
            bs: 'sec-financials-bs',
            cf: 'sec-financials-cf',
            ratios: 'sec-financials-ratios',
            indicators: 'sec-financials-indicators',
            peers: 'sec-financials-peers'
        },
        charges: {
            summary: 'sec-charges-summary',
            open: 'sec-charges-open',
            satisfied: 'sec-charges-satisfied'
        },
        compliance: {
            mca: 'sec-compliance-mca',
            gst: 'sec-compliance-gst',
            epfo: 'sec-compliance-epfo',
            msme: 'sec-compliance-msme',
            auditor: 'sec-compliance-auditor',
            'credit-ratings': 'sec-compliance-credit-ratings'
        },
        litigation: {
            summary: 'sec-litigation-summary',
            confirmed: 'sec-litigation-confirmed',
            probable: 'sec-litigation-probable',
            unverified: 'sec-litigation-unverified',
            'financial-disputes': 'sec-litigation-financial-disputes',
            'financial-dispute': 'sec-litigation-financial-disputes' // normalization
        }
    };

    /**
     * Parse a hash string into domain and target section ID.
     * Returns null for empty, invalid, or unrecognized hashes.
     */
    function parseHash(hashStr) {
        if (!hashStr || typeof hashStr !== 'string') return null;
        const clean = hashStr.startsWith('#') ? hashStr.substring(1) : hashStr;
        if (!clean) return null;

        // 1. Direct canonical section: sec-{domain}-{id}
        if (clean.startsWith('sec-')) {
            const parts = clean.split('-');
            if (parts.length >= 3) {
                const domain = parts[1].toLowerCase();
                if (CANONICAL_SECTIONS[domain] && CANONICAL_SECTIONS[domain].includes(clean)) {
                    return {
                        type: 'section',
                        domain: domain,
                        sectionId: clean,
                        targetTab: 'tab-' + domain
                    };
                }
            }
        }

        // 2. Tab or legacy slash: tab-{domain} or tab-{domain}/{section}
        if (clean.startsWith('tab-')) {
            const afterTab = clean.substring(4);
            const slashIdx = afterTab.indexOf('/');
            if (slashIdx === -1) {
                const domain = afterTab.toLowerCase();
                return {
                    type: 'tab',
                    domain: domain,
                    sectionId: null,
                    targetTab: 'tab-' + domain
                };
            } else {
                const domain = afterTab.substring(0, slashIdx).toLowerCase();
                const slug = afterTab.substring(slashIdx + 1).toLowerCase();
                const mappedSection = LEGACY_SLUG_MAP[domain] && LEGACY_SLUG_MAP[domain][slug];
                if (mappedSection) {
                    return {
                        type: 'section',
                        domain: domain,
                        sectionId: mappedSection,
                        targetTab: 'tab-' + domain
                    };
                }
                // If section slug is unknown, still resolve parent tab
                return {
                    type: 'tab',
                    domain: domain,
                    sectionId: null,
                    targetTab: 'tab-' + domain
                };
            }
        }

        return null;
    }

    /**
     * Ensures target tab is active. If already active, onReady fires immediately.
     * Otherwise calls bootstrap.Tab.show() and awaits 'shown.bs.tab'.
     */
    function ensureTabActive(tabBtn, onReady, bootstrapLib) {
        if (!tabBtn) return;
        if (tabBtn.classList && tabBtn.classList.contains('active')) {
            if (typeof onReady === 'function') onReady();
            return;
        }

        const bLib = bootstrapLib || (typeof bootstrap !== 'undefined' ? bootstrap : null);
        if (!bLib || !bLib.Tab) {
            if (tabBtn.classList) tabBtn.classList.add('active');
            if (typeof onReady === 'function') onReady();
            return;
        }

        if (typeof onReady === 'function') {
            tabBtn.addEventListener('shown.bs.tab', onReady, { once: true });
        }
        bLib.Tab.getOrCreateInstance(tabBtn).show();
    }

    /**
     * Programmatic scroll to section, respecting prefers-reduced-motion.
     */
    function scrollToSection(targetEl, win) {
        if (!targetEl || typeof targetEl.scrollIntoView !== 'function') return;
        const w = win || (typeof window !== 'undefined' ? window : null);
        let reduced = false;
        try {
            reduced = !!(w && w.matchMedia && w.matchMedia('(prefers-reduced-motion: reduce)').matches);
        } catch (e) {}

        targetEl.scrollIntoView({
            behavior: reduced ? 'auto' : 'smooth',
            block: 'start'
        });

        if (typeof targetEl.focus === 'function') {
            try {
                targetEl.focus({ preventScroll: true });
            } catch (e) {
                targetEl.focus();
            }
        }
    }

    /**
     * Open charge drawer and scroll, without any subtab dependency.
     */
    function openAndScrollCharge(chargeId, doc, win, bootstrapLib) {
        const d = doc || (typeof document !== 'undefined' ? document : null);
        if (!d) return;
        const row = d.getElementById('charge-' + chargeId);
        if (!row) return;

        const w = win || (typeof window !== 'undefined' ? window : null);
        const bLib = bootstrapLib || (typeof bootstrap !== 'undefined' ? bootstrap : null);

        let reduced = false;
        try {
            reduced = !!(w && w.matchMedia && w.matchMedia('(prefers-reduced-motion: reduce)').matches);
        } catch (e) {}

        const chargesTabBtn = d.querySelector('#mcaTabs button[data-bs-target="#tab-charges"]');
        ensureTabActive(chargesTabBtn, function () {
            const scrollToRow = function () {
                if (typeof row.scrollIntoView === 'function') {
                    row.scrollIntoView({
                        behavior: reduced ? 'auto' : 'smooth',
                        block: 'center'
                    });
                }
            };

            const openDrawer = function () {
                if (row.classList && row.classList.contains('show')) {
                    scrollToRow();
                } else if (bLib && bLib.Collapse) {
                    row.addEventListener('shown.bs.collapse', scrollToRow, { once: true });
                    bLib.Collapse.getOrCreateInstance(row, { toggle: false }).show();
                } else {
                    if (row.classList) row.classList.add('show');
                    scrollToRow();
                }
            };

            const holderGroup = row.closest ? row.closest('tbody.collapse') : null;
            if (holderGroup && !holderGroup.classList.contains('show') && bLib && bLib.Collapse) {
                holderGroup.addEventListener('shown.bs.collapse', openDrawer, { once: true });
                bLib.Collapse.getOrCreateInstance(holderGroup, { toggle: false }).show();
            } else {
                openDrawer();
            }
        }, bLib);
    }

    /**
     * Unified controller initialization.
     */
    function initDetailsNav(options) {
        const d = (options && options.doc) || (typeof document !== 'undefined' ? document : null);
        const w = (options && options.window) || (typeof window !== 'undefined' ? window : null);
        const bLib = (options && options.bootstrap) || (typeof bootstrap !== 'undefined' ? bootstrap : null);
        if (!d) return null;

        const head = d.getElementById('mcaDetailHead');
        const reqId = (options && options.requestId) || (head && head.dataset && head.dataset.requestId) || '';
        const focusCharge = (options && options.focusCharge) || (head && head.dataset && head.dataset.focusCharge) || '';

        const tabKey = 'mca-v2-' + reqId + '-tab';
        const secKey = function (domain) { return 'mca-v2-' + reqId + '-sec-' + domain; };

        // ── Dual ResizeObserver Setup ──
        let detailHeadObserver = null;
        let contentsNavObserver = null;
        let activeObservedNav = null;

        function setCssVar(name, value) {
            if (d.documentElement && d.documentElement.style) {
                d.documentElement.style.setProperty(name, value);
            }
        }

        const RO = (options && options.ResizeObserver) || (typeof ResizeObserver !== 'undefined' ? ResizeObserver : null);
        if (RO && head) {
            detailHeadObserver = new RO(function (entries) {
                for (const entry of entries) {
                    const h = entry.target.offsetHeight || (entry.contentRect && entry.contentRect.height);
                    if (h) setCssVar('--mca-detail-head-h', h + 'px');
                }
            });
            detailHeadObserver.observe(head);
            // Initial sync
            if (head.offsetHeight) setCssVar('--mca-detail-head-h', head.offsetHeight + 'px');
        }

        function updateContentsNavObservation(domain) {
            if (!RO) return;
            if (contentsNavObserver && activeObservedNav) {
                contentsNavObserver.unobserve(activeObservedNav);
                activeObservedNav = null;
            }

            if (!LONG_DOMAINS.includes(domain)) {
                setCssVar('--mca-contents-nav-h', '0px');
                return;
            }

            const nav = d.getElementById('contents-nav-' + domain);
            if (!nav) {
                setCssVar('--mca-contents-nav-h', '0px');
                return;
            }

            activeObservedNav = nav;
            if (!contentsNavObserver) {
                contentsNavObserver = new RO(function (entries) {
                    for (const entry of entries) {
                        const h = entry.target.offsetHeight || (entry.contentRect && entry.contentRect.height);
                        if (h) setCssVar('--mca-contents-nav-h', h + 'px');
                    }
                });
            }
            contentsNavObserver.observe(nav);
            if (nav.offsetHeight) setCssVar('--mca-contents-nav-h', nav.offsetHeight + 'px');
        }

        // ── ScrollSpy Lifecycle ──
        function updateScrollSpy(domain) {
            if (!bLib || !bLib.ScrollSpy) return;
            // Always dispose previous instance on document.body
            const existing = bLib.ScrollSpy.getInstance(d.body);
            if (existing) {
                existing.dispose();
            }

            if (LONG_DOMAINS.includes(domain)) {
                const navId = '#contents-nav-' + domain;
                if (d.querySelector(navId)) {
                    new bLib.ScrollSpy(d.body, {
                        target: navId,
                        rootMargin: '0px 0px -40%'
                    });
                }
            }
        }

        // ── Section & Main Tab Activation ──
        function onMainTabActivated(domain) {
            updateContentsNavObservation(domain);
            updateScrollSpy(domain);
            try {
                if (w && w.sessionStorage) {
                    w.sessionStorage.setItem(tabKey, '#tab-' + domain);
                }
            } catch (e) {}
        }

        // Bind main tabs
        const mainTabs = d.querySelectorAll('#mcaTabs button[data-bs-toggle="tab"]');
        mainTabs.forEach(function (btn) {
            btn.addEventListener('shown.bs.tab', function (e) {
                const targetSelector = (e.target && e.target.getAttribute('data-bs-target')) || '';
                const domain = targetSelector.replace('#tab-', '').toLowerCase();
                onMainTabActivated(domain);
            });
        });

        // ── Secondary sub-tab persistence for non-long tabs (Corporate, AI, Documents) ──
        d.querySelectorAll('[data-mca-sections]').forEach(function (nav) {
            const domain = nav.getAttribute('data-mca-sections');
            if (LONG_DOMAINS.includes(domain)) return; // long tabs use continuous stacked sections

            let wanted = null;
            try {
                if (w && w.sessionStorage) wanted = w.sessionStorage.getItem(secKey(domain));
            } catch (e) {}

            if (wanted) {
                const subBtn = nav.querySelector('[data-bs-toggle="tab"][data-bs-target="' + wanted + '"]');
                if (subBtn) ensureTabActive(subBtn, null, bLib);
            }

            nav.querySelectorAll('button[data-bs-toggle="tab"]').forEach(function (btn) {
                btn.addEventListener('shown.bs.tab', function (e) {
                    try {
                        if (w && w.sessionStorage) {
                            w.sessionStorage.setItem(secKey(domain), e.target.getAttribute('data-bs-target'));
                        }
                    } catch (err) {}
                });
            });
        });

        // ── Standalone / Consolidated basis toggles ──
        d.querySelectorAll('[data-basis-group]').forEach(function (grp) {
            const id = grp.getAttribute('data-basis-group');
            grp.querySelectorAll('button[data-basis]').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    const basis = btn.getAttribute('data-basis');
                    grp.querySelectorAll('button[data-basis]').forEach(function (b) {
                        b.classList.toggle('active', b === btn);
                    });
                    d.querySelectorAll('[data-basis-panel="' + id + '"]').forEach(function (p) {
                        p.hidden = p.getAttribute('data-basis') !== basis;
                    });
                });
            });
        });

        // ── Documents pagination fetch-and-swap ──
        const docsPanel = d.getElementById('mca-docs-panel');
        if (docsPanel) {
            docsPanel.addEventListener('click', function (e) {
                const a = e.target.closest ? e.target.closest('a') : null;
                if (!a || !a.getAttribute('href')) return;
                const href = a.getAttribute('href');
                if (href.indexOf('/Requests/') === -1 || href.indexOf('/Documents') === -1) return;
                e.preventDefault();
                docsPanel.classList.add('mca-loading');
                if (w && w.fetch) {
                    w.fetch(href, { headers: { 'X-Requested-With': 'fetch' } })
                        .then(function (res) { return res.text(); })
                        .then(function (html) {
                            docsPanel.innerHTML = html;
                            docsPanel.classList.remove('mca-loading');
                        })
                        .catch(function () {
                            docsPanel.classList.remove('mca-loading');
                            if (w.location) w.location = href;
                        });
                } else if (w && w.location) {
                    w.location = href;
                }
            });
        }

        // ── Jump Link Click Handlers ──
        d.querySelectorAll('.mca-contents-nav a.nav-link').forEach(function (link) {
            link.addEventListener('click', function (e) {
                const href = link.getAttribute('href') || '';
                if (!href.startsWith('#')) return;
                const targetId = href.substring(1);
                const targetEl = d.getElementById(targetId);
                if (targetEl) {
                    e.preventDefault(); // Prevent native jump racing pushState
                    if (w && w.history && w.history.pushState) {
                        w.history.pushState(null, '', href);
                    }
                    scrollToSection(targetEl, w);
                }
            });
        });

        // ── Navigation Resolution from Hash ──
        function resolveAndNavigateHash(hashStr) {
            const parsed = parseHash(hashStr);
            if (!parsed) return false;

            const tabBtn = d.querySelector('#mcaTabs button[data-bs-target="#' + parsed.targetTab + '"]');
            if (!tabBtn) return false;

            ensureTabActive(tabBtn, function () {
                onMainTabActivated(parsed.domain);
                if (parsed.sectionId) {
                    const sectionEl = d.getElementById(parsed.sectionId);
                    if (sectionEl) {
                        scrollToSection(sectionEl, w);
                    }
                }
            }, bLib);

            return true;
        }

        // ── Popstate and Hashchange Event Listeners ──
        function onHistoryEvent() {
            const currentHash = (w && w.location && w.location.hash) || '';
            resolveAndNavigateHash(currentHash); // Invalid hash is safe no-op
        }

        if (w && w.addEventListener) {
            w.addEventListener('popstate', onHistoryEvent);
            w.addEventListener('hashchange', onHistoryEvent);
        }

        // ── Initial Navigation on Page Load ──
        const initialHash = (w && w.location && w.location.hash) || '';
        const navigated = resolveAndNavigateHash(initialHash);

        if (!navigated) {
            if (focusCharge) {
                openAndScrollCharge(focusCharge, d, w, bLib);
            } else {
                let savedTab = null;
                try {
                    if (w && w.sessionStorage) savedTab = w.sessionStorage.getItem(tabKey);
                } catch (e) {}

                if (savedTab) {
                    const savedBtn = d.querySelector('#mcaTabs button[data-bs-target="' + savedTab + '"]');
                    if (savedBtn) {
                        ensureTabActive(savedBtn, function () {
                            const domain = savedTab.replace('#tab-', '').toLowerCase();
                            onMainTabActivated(domain);
                        }, bLib);
                    }
                } else {
                    // Default active tab (AI Analysis)
                    const activeBtn = d.querySelector('#mcaTabs button[data-bs-toggle="tab"].active')
                        || d.querySelector('#mcaTabs button[data-bs-toggle="tab"]');
                    if (activeBtn) {
                        const targetSelector = activeBtn.getAttribute('data-bs-target') || '';
                        const domain = targetSelector.replace('#tab-', '').toLowerCase();
                        onMainTabActivated(domain);
                    }
                }
            }
        }

        return {
            parseHash: parseHash,
            ensureTabActive: ensureTabActive,
            scrollToSection: scrollToSection,
            openAndScrollCharge: openAndScrollCharge,
            resolveAndNavigateHash: resolveAndNavigateHash,
            destroy: function () {
                if (detailHeadObserver) detailHeadObserver.disconnect();
                if (contentsNavObserver) contentsNavObserver.disconnect();
                if (bLib && bLib.ScrollSpy) {
                    const existing = bLib.ScrollSpy.getInstance(d.body);
                    if (existing) existing.dispose();
                }
                if (w && w.removeEventListener) {
                    w.removeEventListener('popstate', onHistoryEvent);
                    w.removeEventListener('hashchange', onHistoryEvent);
                }
            }
        };
    }

    return {
        LONG_DOMAINS: LONG_DOMAINS,
        CANONICAL_SECTIONS: CANONICAL_SECTIONS,
        LEGACY_SLUG_MAP: LEGACY_SLUG_MAP,
        parseHash: parseHash,
        ensureTabActive: ensureTabActive,
        scrollToSection: scrollToSection,
        openAndScrollCharge: openAndScrollCharge,
        initDetailsNav: initDetailsNav
    };
}));
