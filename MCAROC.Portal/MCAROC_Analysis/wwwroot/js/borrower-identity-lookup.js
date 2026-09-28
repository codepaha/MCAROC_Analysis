// Local MCA master lookup. Selection is explicit; a similarity search never silently replaces identity.
function borrowerIdentityLookup(options) {
    const name = document.getElementById(options.name);
    const identifier = document.getElementById(options.identifier);
    const results = document.getElementById(options.results);
    const status = document.getElementById(options.status);
    if (!name || !identifier || !results || !status) return;
    let timer, request, selectedName = null;

    function clear() { results.replaceChildren(); }
    function search() {
        clearTimeout(timer);
        if (request) request.abort();
        clear();
        const entity = options.entity();
        if (!['Company', 'Llp', 'ForeignCompany', 'Limited', 'Private Limited', 'LLP', 'Foreign Company'].includes(entity)) {
            status.textContent = 'MCA name lookup applies to companies, LLPs and foreign companies.';
            return;
        }
        if (name.value.trim().length < 3) {
            status.textContent = 'Type at least 3 characters to search the MCA master database.';
            return;
        }
        timer = setTimeout(async function () {
            request = new AbortController();
            const current = request;
            status.textContent = 'Searching registered borrowers…';
            try {
                const url = new URL(options.url, window.location.href);
                url.searchParams.set('name', name.value.trim());
                url.searchParams.set('entityType', options.entity());
                const response = await fetch(url, { signal: current.signal, headers: { Accept: 'application/json' } });
                if (!response.ok) throw new Error('lookup failed');
                const matches = await response.json();
                if (current !== request) return;
                clear();
                for (const match of matches) {
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'list-group-item list-group-item-action';
                    button.setAttribute('role', 'option');
                    button.textContent = `${match.name} — ${match.identifier}${match.state ? ` · ${match.state}` : ''}${match.status ? ` · ${match.status}` : ''}`;
                    button.addEventListener('click', function () {
                        name.value = match.name;
                        identifier.value = match.identifier;
                        selectedName = match.name;
                        clear();
                        status.textContent = `Selected ${match.name} (${match.identifier}) from the MCA master database.`;
                    });
                    results.appendChild(button);
                }
                status.textContent = matches.length ? 'Select the correct name and CIN / LLPIN below.' : 'No matching registered borrower found. Check the spelling or enter the identifier yourself.';
            } catch (error) {
                if (error.name !== 'AbortError') status.textContent = 'MCA master lookup is unavailable. You can enter the identifier yourself.';
            }
        }, 250);
    }
    name.addEventListener('input', function () {
        if (selectedName && name.value !== selectedName) { identifier.value = ''; selectedName = null; }
        search();
    });
    const typeInputs = document.querySelectorAll('input[name="EntityType"]');
    typeInputs.forEach(input => input.addEventListener('change', search));
    document.getElementById('entityType')?.addEventListener('change', search);
    search();
}
