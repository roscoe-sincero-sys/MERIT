// Progressive enhancements for native patient tables, filter disclosures and server-rendered charts.
(() => {
    const phone = matchMedia('(max-width: 640px)');
    const charts = new Set();
    function resizeChart(svg) {
        if (!svg.isConnected || !svg.parentElement) return;
        const width = Math.max(200, Math.floor(svg.parentElement.clientWidth));
        const originalWidth = Number(svg.dataset.chartWidth), left = 48, right = 16;
        const x = value => left + (Number(value) - left) * (width - left - right) / (originalWidth - left - right);
        svg.querySelectorAll('[x], [x1], [x2], [cx], polyline').forEach(el => {
            for (const attr of ['x', 'x1', 'x2', 'cx', 'points']) {
                if (!el.hasAttribute(attr)) continue;
                const saved = 'data-chart-original-' + attr;
                if (!el.hasAttribute(saved)) el.setAttribute(saved, el.getAttribute(attr));
                const original = el.getAttribute(saved);
                el.setAttribute(attr, attr === 'points'
                    ? original.trim().split(/\s+/).map(pair => { const [px, py] = pair.split(','); return `${x(px)},${py}`; }).join(' ')
                    : String(x(original)));
            }
        });
        svg.setAttribute('viewBox', `0 0 ${width} 220`);
        svg.setAttribute('width', width); svg.setAttribute('height', '220');
    }
    const resize = new ResizeObserver(entries => entries.forEach(e => resizeChart(e.target)));
    function updateFilters() {
        document.querySelectorAll('[data-case-filters]').forEach(form => {
            const count = ['assigned','classification','population','analyteId','owner'].filter(key => form.elements.namedItem(key)?.value).length
                + (form.querySelector('#openOnly')?.checked ? 0 : 1);
            form.querySelector('[data-filter-count]').textContent = String(count);
        });
    }
    function init() {
        document.querySelectorAll('[data-phone-cards]').forEach(table => {
            const headings = [...table.querySelectorAll('thead th')].map(h => h.textContent.trim());
            table.querySelectorAll('thead, tbody').forEach(group => group.setAttribute('role', 'rowgroup'));
            table.querySelectorAll('tr').forEach(row => row.setAttribute('role', 'row'));
            table.querySelectorAll('thead th').forEach(h => h.setAttribute('role', 'columnheader'));
            table.querySelectorAll('tbody tr').forEach(row => [...row.children].forEach((cell, i) => {
                cell.setAttribute('role', cell.tagName === 'TH' ? 'rowheader' : 'cell');
                if (cell.querySelector(':scope > .merit-mobile-label')) return;
                const label = document.createElement('span');
                label.className = 'merit-mobile-label'; label.setAttribute('aria-hidden', 'true');
                label.textContent = (headings[i] || 'Actions') + ': ';
                cell.prepend(label);
            }));
        });
        document.querySelectorAll('[data-responsive-filters]:not([data-filter-ready])').forEach(details => {
            details.dataset.filterReady = 'true'; details.open = !phone.matches;
        });
        for (const svg of charts) if (!svg.isConnected) { resize.unobserve(svg); charts.delete(svg); }
        document.querySelectorAll('svg[data-chart-width]').forEach(svg => {
            if (!charts.has(svg)) { charts.add(svg); resize.observe(svg); }
            resizeChart(svg);
            const links = [...svg.querySelectorAll('a[data-chart-result]')];
            if (!links.some(a => a.getAttribute('tabindex') === '0')) links.forEach((a, i) => a.setAttribute('tabindex', i === 0 ? '0' : '-1'));
        });
        document.querySelectorAll('.merit-phone-table-wrap').forEach(wrap => {
            if (phone.matches) { wrap.removeAttribute('tabindex'); wrap.setAttribute('aria-label', 'Labeled records and actions'); }
            else { wrap.setAttribute('tabindex', '0'); wrap.setAttribute('aria-label', 'Table; scroll sideways for more columns'); }
        });
        updateFilters();
    }
    phone.addEventListener('change', () => {
        document.querySelectorAll('[data-responsive-filters]').forEach(details => details.open = !phone.matches);
        init();
    });
    document.addEventListener('htmx:after:swap', init);
    document.addEventListener('keydown', event => {
        const point = event.target.closest('svg[data-chart-width] a[data-chart-result]');
        if (!point || !['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home','End'].includes(event.key)) return;
        const points = [...point.closest('svg').querySelectorAll('a[data-chart-result]')];
        const i = points.indexOf(point);
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? points.length - 1
            : Math.max(0, Math.min(points.length - 1, i + (['ArrowRight','ArrowDown'].includes(event.key) ? 1 : -1)));
        event.preventDefault(); points.forEach((p, index) => p.setAttribute('tabindex', index === next ? '0' : '-1')); points[next].focus();
    });
    // Keep the default desktop page size, but request a bounded 25-event page on first phone entry.
    const audit = document.querySelector('[data-audit-page-size]');
    if (audit && matchMedia('(max-width: 480px)').matches) {
        const url = new URL(location.href);
        if (!url.searchParams.has('pageSize')) {
            const page = Math.max(1, Number(url.searchParams.get('page')) || 1);
            url.searchParams.set('pageSize', '25'); url.searchParams.set('page', String((page - 1) * 4 + 1));
            location.replace(url.href);
        }
    }
    init();
})();
