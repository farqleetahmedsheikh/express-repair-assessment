// Leads page: loads paged calls from /api/calls, simulates webhooks, and reloads on SignalR "callChanged".
(() => {
    'use strict';

    const state = { page: 1, pageSize: 10, status: '', search: '', sortBy: 'date', sortDirection: 'desc', items: [], openId: null, flash: new Set() };

    const $ = (sel) => document.querySelector(sel);
    // Everything shown in the table comes from callers / the LLM: always escape before putting it in HTML.
    const esc = (v) => v == null ? '' : String(v).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

    const APPLIANCE_ICONS = { fridge: 'bi-snow2', washer: 'bi-water', dryer: 'bi-wind', dishwasher: 'bi-droplet-half', oven: 'bi-fire', other: 'bi-tools' };
    const AVATAR_COLORS = ['#D1181F', '#7c3aed', '#db2777', '#ea580c', '#0d9488', '#AE141A', '#B82038', '#65a30d'];

    // ---------------------------------------------------------------- data

    let loadSeq = 0;
    async function load() {
        const seq = ++loadSeq;
        const params = new URLSearchParams({ page: state.page, pageSize: state.pageSize });
        if (state.status) params.set('status', state.status);
        if (state.search) params.set('search', state.search);
        params.set('sortBy', state.sortBy);
        params.set('sortDirection', state.sortDirection);

        try {
            const res = await fetch(`/api/calls?${params}`);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data = await res.json();
            if (seq !== loadSeq) return; // a newer request already started; ignore this stale answer
            state.page = data.page;      // the server clamps the page (e.g. after a filter shrinks the list)
            state.items = data.items;
            renderStats(data.stats);
            renderCounts(data.counts);
            renderRows();
            renderPagination(data);
            if (state.openId) await refreshDetails();
        } catch (err) {
            if (seq === loadSeq) {
                $('#calls-body').innerHTML = emptyRow('bi-wifi-off', "Couldn't load calls", esc(err.message));
            }
        }
    }

    let reloadTimer;
    const scheduleReload = () => { clearTimeout(reloadTimer); reloadTimer = setTimeout(load, 150); };

    // ---------------------------------------------------------------- rendering

    function renderStats(s) {
        $('#kpi-total').textContent = s.total;
        $('#kpi-processing').textContent = s.processing;
        $('#kpi-done').textContent = s.done;
        $('#kpi-failed').textContent = s.failed;
        $('#kpi-high').textContent = s.highUrgency;
    }

    function renderCounts(c) {
        document.querySelectorAll('[data-count]').forEach((el) => { el.textContent = c[el.dataset.count]; });
    }

    function renderRows() {
        const body = $('#calls-body');
        if (!state.items.length) {
            body.innerHTML = state.status || state.search
                ? emptyRow('bi-search', 'No matching calls', 'Try another status or search term.')
                : emptyRow('bi-telephone', 'No calls yet', 'Use “Simulate incoming call” above to send a webhook.');
            return;
        }
        body.innerHTML = state.items.map(rowHtml).join('');
        state.flash.clear();
    }

    function rowHtml(c) {
        const classes = ['call-row', state.flash.has(c.id) ? 'flash' : ''].join(' ');
        const action = `${c.status === 'Failed' ? `<button type="button" class="btn btn-outline-secondary btn-retry" data-retry="${c.id}"><i class="bi bi-arrow-clockwise" aria-hidden="true"></i>Retry</button>` : ''}<button type="button" class="row-details-button" title="View call details" aria-label="View call details for ${esc(c.customerName || c.from)}"><i class="bi bi-arrow-up-right" aria-hidden="true"></i></button>`;

        return `
            <tr class="${classes}" data-id="${c.id}" tabindex="0" aria-haspopup="dialog">
                <td class="received">${esc(formatDate(c.receivedAtUtc))}</td>
                <td class="phone">${esc(formatPhone(c.from))}</td>
                <td>${customerHtml(c)}</td>
                <td>${c.appliance ? `<span class="appliance"><i class="bi ${APPLIANCE_ICONS[c.appliance] || 'bi-tools'}"></i>${esc(c.appliance)}</span>` : dash(c)}</td>
                <td>${c.brand ? esc(c.brand) : dash(c)}</td>
                <td><div class="problem" title="${esc(c.problem)}">${c.problem ? esc(c.problem) : dash(c)}</div></td>
                <td>${c.urgency ? `<span class="badge-soft urg-${esc(c.urgency)}"><span class="bdot"></span>${esc(cap(c.urgency))}</span>` : dash(c)}</td>
                <td>${statusHtml(c)}</td>
                <td class="received" title="${esc(new Date(c.receivedAtUtc).toLocaleString())}">${esc(timeAgo(c.receivedAtUtc))}<small>${esc(formatTime(c.receivedAtUtc))}</small></td>
                <td class="row-actions-cell"><div class="row-actions">${action}</div></td>
            </tr>`;
    }

    function statusHtml(c) {
        switch (c.status) {
            case 'Processing': return '<span class="badge-soft status-Processing"><span class="spinner-border spin"></span>Processing</span>';
            case 'Done': return '<span class="badge-soft status-Done"><i class="bi bi-check-circle-fill"></i>Done</span>';
            default: return `<span class="badge-soft status-Failed" title="${esc(c.error)}"><i class="bi bi-x-circle-fill"></i>Failed</span>`;
        }
    }

    function customerHtml(c) {
        if (c.customerName) {
            return `<div class="customer"><span class="avatar" style="background:${colorFor(c.customerName)}">${esc(initials(c.customerName))}</span>${esc(c.customerName)}</div>`;
        }
        const label = c.status === 'Processing' ? '<span class="text-muted">Pending…</span>' : '<span class="unknown-name">Unknown</span>';
        return `<div class="customer"><span class="avatar unknown"><i class="bi bi-person"></i></span>${label}</div>`;
    }

    let detailSeq = 0;
    let detailSnapshot = '';

    function renderDetails(c) {
        const snapshot = JSON.stringify(c);
        if (snapshot === detailSnapshot) return;
        detailSnapshot = snapshot;
        const fullDate = value => value ? esc(new Date(value).toLocaleString([], { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })) : '—';
        const field = (label, value) => `<div><dt>${label}</dt><dd>${esc(value || 'Not provided')}</dd></div>`;
        const priority = c.urgency ? `<span class="badge-soft urg-${esc(c.urgency)}"><span class="bdot"></span>${esc(cap(c.urgency))} priority</span>` : '<span class="badge-soft priority-pending">Priority pending</span>';
        $('#call-modal-content').innerHTML = `
            <section class="call-customer-summary">
                <span class="modal-avatar">${c.customerName ? esc(initials(c.customerName)) : '<i class="bi bi-person" aria-hidden="true"></i>'}</span>
                <div class="call-customer-name"><h3>${esc(c.customerName || 'Unknown customer')}</h3><p>${esc(formatPhone(c.from))}</p></div>
                <div class="call-summary-badges">${priority}${statusHtml(c)}</div>
            </section>
            <div class="call-detail-grid">
                <section class="call-detail-card"><h3><i class="bi bi-tools" aria-hidden="true"></i>Repair request</h3>
                    <dl class="call-fields">${field('Appliance', cap(c.appliance))}${field('Brand', c.brand)}${field('Preferred time', c.preferredTime)}</dl>
                    <div class="call-problem"><h4>Reported problem</h4><p>${esc(c.problem || (c.status === 'Processing' ? 'Extracting repair details…' : 'No repair details available.'))}</p></div>
                </section>
                <section class="call-detail-card"><h3><i class="bi bi-telephone" aria-hidden="true"></i>Call information</h3>
                    <dl class="call-fields">${field('From', formatPhone(c.from))}${field('To', formatPhone(c.to))}${field('Recording duration', c.recordingDurationSeconds == null ? null : `${c.recordingDurationSeconds} seconds`)}<div><dt>Received</dt><dd>${fullDate(c.receivedAtUtc)}</dd></div><div><dt>Completed</dt><dd>${fullDate(c.completedAtUtc)}</dd></div>${field('Processing attempts', String(c.attempts))}</dl>
                </section>
            </div>
            <section class="call-detail-card transcript-card"><h3><i class="bi bi-chat-left-quote" aria-hidden="true"></i>Voicemail transcript</h3><blockquote>${esc(c.transcript || (c.status === 'Processing' ? 'Transcription is in progress. This window will update automatically.' : 'No transcript available for this call.'))}</blockquote></section>
            ${c.error ? `<section class="call-error"><h3><i class="bi bi-exclamation-triangle" aria-hidden="true"></i>Processing failed</h3><p>${esc(c.error)}</p></section>` : ''}
            <section class="call-references"><h3>Recording references</h3><dl>${field('Call SID', c.callSid)}${field('Recording SID', c.recordingSid)}${field('Recording URL', c.recordingUrl)}</dl></section>`;
        $('#modal-retry').hidden = c.status !== 'Failed';
        const phone = String(c.from || '').replace(/[^+\d]/g, '');
        $('#modal-call').hidden = !phone;
        if (phone) $('#modal-call').setAttribute('href', `tel:${phone}`);
        else $('#modal-call').removeAttribute('href');
    }

    async function refreshDetails() {
        const id = state.openId;
        if (!id) return;
        const seq = ++detailSeq;
        try {
            const response = await fetch(`/api/calls/${encodeURIComponent(id)}`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const call = await response.json();
            if (seq !== detailSeq || state.openId !== id) return;
            $('#call-modal-warning').hidden = true;
            renderDetails(call);
        } catch (error) {
            if (seq !== detailSeq || state.openId !== id) return;
            $('#call-modal-warning').textContent = `Could not refresh details (${error.message}). Showing the last available information.`;
            $('#call-modal-warning').hidden = false;
        }
    }

    function openDetails(id) {
        const call = state.items.find(item => item.id === id);
        if (!call) return;
        state.openId = id;
        detailSnapshot = '';
        $('#call-modal-warning').hidden = true;
        renderDetails(call);
        document.body.classList.add('call-modal-open');
        $('#call-modal').showModal();
        refreshDetails();
    }

    function renderPagination({ page, pageSize, totalCount, totalPages }) {
        const from = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
        const to = Math.min(page * pageSize, totalCount);
        $('#range-label').textContent = totalCount === 0 ? 'No calls' : `Showing ${from}–${to} of ${totalCount}`;

        const item = (label, target, { disabled = false, active = false, aria = '' } = {}) =>
            `<li class="page-item${disabled ? ' disabled' : ''}${active ? ' active' : ''}">
                <button class="page-link" data-page="${target}" ${disabled ? 'disabled' : ''} ${aria ? `aria-label="${aria}"` : ''}>${label}</button>
             </li>`;

        const parts = [item('<i class="bi bi-chevron-left"></i>', page - 1, { disabled: page <= 1, aria: 'Previous page' })];
        for (const p of pageList(page, totalPages)) {
            parts.push(p === '…'
                ? '<li class="page-item disabled"><span class="page-link">…</span></li>'
                : item(p, p, { active: p === page }));
        }
        parts.push(item('<i class="bi bi-chevron-right"></i>', page + 1, { disabled: page >= totalPages, aria: 'Next page' }));
        $('#pagination').innerHTML = parts.join('');
    }

    /** Page numbers with gaps, e.g. 1 … 4 5 [6] 7 8 … 20 */
    function pageList(current, total) {
        if (total <= 7) return Array.from({ length: total }, (_, i) => i + 1);
        const pages = new Set([1, total, current - 1, current, current + 1]);
        if (current <= 3) [2, 3, 4].forEach((p) => pages.add(p));
        if (current >= total - 2) [total - 3, total - 2, total - 1].forEach((p) => pages.add(p));
        const sorted = [...pages].filter((p) => p >= 1 && p <= total).sort((a, b) => a - b);
        const out = [];
        sorted.forEach((p, i) => { if (i > 0 && p - sorted[i - 1] > 1) out.push('…'); out.push(p); });
        return out;
    }

    const emptyRow = (icon, title, text) =>
        `<tr><td colspan="10"><div class="empty"><i class="bi ${icon}"></i><strong>${title}</strong>${text}</div></td></tr>`;

    // ---------------------------------------------------------------- formatting helpers

    const dash = (c) => c.status === 'Processing' ? '<span class="placeholder-glow"><span class="placeholder col-8 rounded"></span></span>' : '<span class="muted-dash">—</span>';
    const cap = (s) => s ? s[0].toUpperCase() + s.slice(1) : s;
    const initials = (name) => name.split(/\s+/).filter(Boolean).slice(0, 2).map((w) => w[0].toUpperCase()).join('');

    function colorFor(text) {
        let h = 0;
        for (const ch of text) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
        return AVATAR_COLORS[h % AVATAR_COLORS.length];
    }

    function formatPhone(p) {
        const m = /^\+1(\d{3})(\d{3})(\d{4})$/.exec(p || '');
        return m ? `+1 (${m[1]}) ${m[2]}-${m[3]}` : (p || '');
    }

    function timeAgo(iso) {
        const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
        if (s < 45) return 'Just now';
        if (s < 3600) return `${Math.round(s / 60)} min ago`;
        if (s < 86400) return `${Math.round(s / 3600)} h ago`;
        return `${Math.round(s / 86400)} d ago`;
    }

    const formatDate = (iso) => new Date(iso).toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' });
    const formatTime = (iso) => new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });

    // ---------------------------------------------------------------- actions

    const randomHex = () => Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');

    /** Sends the same form-encoded webhook a phone provider would. The sample key rides in the RecordingUrl. */
    async function simulate() {
        const choice = document.querySelector('input[name="sample"]:checked');
        const btn = $('#simulate-btn');
        const recordingSid = 'RE' + randomHex();
        const body = new URLSearchParams({
            CallSid: 'CA' + randomHex(),
            From: choice.dataset.from,
            To: '+16475550199',
            RecordingSid: recordingSid,
            RecordingUrl: `https://example.com/recordings/${recordingSid}-sample-${choice.value}.mp3`,
            RecordingDuration: choice.dataset.duration
        });

        btn.disabled = true;
        const started = performance.now();
        try {
            // A URLSearchParams body is sent as application/x-www-form-urlencoded, like Twilio.
            const res = await fetch('/api/calls/recording', { method: 'POST', body });
            const ms = Math.round(performance.now() - started);
            showWebhookLog(res.ok, `POST /api/calls/recording → ${res.status}${res.ok ? ' OK' : ''} · ${ms} ms`);
            if (!res.ok) throw new Error(await res.text());

            // Show the new row: first page, no filters.
            resetFilters();
            load();
        } catch (err) {
            toast('Webhook failed', err.message, 'danger');
        } finally {
            btn.disabled = false;
        }
    }

    function showWebhookLog(ok, text) {
        const log = $('#webhook-log');
        log.hidden = false;
        log.innerHTML = `<i class="bi ${ok ? 'bi-check-circle-fill ok' : 'bi-x-circle-fill err'}"></i><span>${esc(text)}</span>`;
    }

    async function retry(id, btn) {
        btn.disabled = true;
        try {
            const res = await fetch(`/api/calls/${id}/retry`, { method: 'POST' });
            if (!res.ok) throw new Error((await res.json().catch(() => ({}))).error || `HTTP ${res.status}`);
        } catch (err) {
            toast('Retry failed', err.message, 'danger');
        }
        load();
    }

    function setStatusTab(status) {
        state.status = status;
        document.querySelectorAll('#status-tabs button').forEach((b) => b.classList.toggle('active', b.dataset.status === status));
    }

    function toast(title, text, tone) {
        const icon = tone === 'success' ? 'bi-check-circle-fill text-success' : tone === 'danger' ? 'bi-x-circle-fill text-danger' : 'bi-info-circle-fill text-primary';
        const el = document.createElement('div');
        el.className = 'toast app-toast';
        el.setAttribute('role', 'status');
        el.innerHTML = `
            <div class="toast-body d-flex gap-2 align-items-start">
                <i class="bi ${icon} fs-5"></i>
                <div class="flex-grow-1"><strong>${esc(title)}</strong><div class="text-muted small">${esc(text)}</div></div>
                <button type="button" class="btn-close btn-close-sm" data-bs-dismiss="toast" aria-label="Close"></button>
            </div>`;
        $('#toasts').append(el);
        el.addEventListener('hidden.bs.toast', () => el.remove());
        new bootstrap.Toast(el, { delay: 4500 }).show();
    }

    // ---------------------------------------------------------------- live updates (SignalR, polling fallback)

    let pollTimer = null;
    const startPolling = () => { if (!pollTimer) pollTimer = setInterval(load, 3000); };
    const stopPolling = () => { clearInterval(pollTimer); pollTimer = null; };

    function setLive(mode, label) {
        const el = $('#live-status');
        el.classList.remove('live', 'offline', 'pill-muted');
        el.classList.add(mode);
        el.querySelector('.label').textContent = label;
    }

    function connect() {
        if (!window.signalR) { setLive('offline', 'Polling'); startPolling(); return; }

        const conn = new signalR.HubConnectionBuilder().withUrl('/hubs/calls').withAutomaticReconnect().build();

        conn.on('callChanged', (evt) => {
            state.flash.add(evt.id);
            if (evt.status === 'Done') toast('Lead ready', `${evt.customerName || formatPhone(evt.from)} · ${cap(evt.appliance || 'other')}`, 'success');
            if (evt.status === 'Failed') toast('Call failed', evt.error || 'Unknown error', 'danger');
            scheduleReload();
        });
        conn.onreconnecting(() => setLive('offline', 'Reconnecting…'));
        conn.onreconnected(() => { setLive('live', 'Live'); stopPolling(); load(); });
        conn.onclose(() => { setLive('offline', 'Polling'); startPolling(); });

        conn.start()
            .then(() => setLive('live', 'Live'))
            .catch(() => { setLive('offline', 'Polling'); startPolling(); });
    }

    // ---------------------------------------------------------------- wiring

    function renderSort() {
        document.querySelectorAll('[data-sort]').forEach(button => {
            const selected = button.dataset.sort === state.sortBy;
            button.closest('th').setAttribute('aria-sort', selected ? (state.sortDirection === 'asc' ? 'ascending' : 'descending') : 'none');
            button.querySelector('i').className = `bi ${selected ? (state.sortDirection === 'asc' ? 'bi-arrow-up' : 'bi-arrow-down') : 'bi-arrow-down-up'}`;
            button.setAttribute('title', `Sort ${button.dataset.sort} ${selected && state.sortDirection === 'asc' ? 'descending' : 'ascending'}`);
        });
    }

    function resetFilters() {
        clearTimeout(searchTimer);
        state.page = 1;
        state.search = '';
        state.sortBy = 'date';
        state.sortDirection = 'desc';
        $('#search').value = '';
        setStatusTab('');
        renderSort();
    }

    document.querySelectorAll('[data-sort]').forEach(button => {
        button.addEventListener('click', () => {
            state.sortDirection = state.sortBy === button.dataset.sort && state.sortDirection === 'asc' ? 'desc' : 'asc';
            state.sortBy = button.dataset.sort;
            state.page = 1;
            renderSort();
            load();
        });
    });

    $('#simulate-btn').addEventListener('click', simulate);

    $('#status-tabs').addEventListener('click', (e) => {
        const btn = e.target.closest('button[data-status]');
        if (!btn) return;
        setStatusTab(btn.dataset.status);
        state.page = 1;
        load();
    });

    let searchTimer;
    $('#search').addEventListener('input', (e) => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => { state.search = e.target.value.trim(); state.page = 1; load(); }, 300);
    });

    $('#page-sizes').addEventListener('click', (e) => {
        const button = e.target.closest('[data-page-size]');
        if (!button) return;
        const size = Number(button.dataset.pageSize);
        if (size === state.pageSize) return;
        state.pageSize = size;
        state.page = 1;
        document.querySelectorAll('[data-page-size]').forEach((option) => {
            const active = Number(option.dataset.pageSize) === size;
            option.classList.toggle('active', active);
            option.setAttribute('aria-pressed', String(active));
        });
        $('#calls-scroll').scrollTop = 0;
        load();
    });

    $('#pagination').addEventListener('click', (e) => {
        const btn = e.target.closest('button[data-page]');
        if (!btn || btn.disabled) return;
        state.page = Number(btn.dataset.page);
        $('#calls-scroll').scrollTop = 0;
        load();
    });

    $('#calls-body').addEventListener('click', (e) => {
        const retryBtn = e.target.closest('[data-retry]');
        if (retryBtn) { e.stopPropagation(); retry(retryBtn.dataset.retry, retryBtn); return; }
        const row = e.target.closest('tr.call-row');
        if (!row) return;
        openDetails(row.dataset.id);
    });

    $('#calls-body').addEventListener('keydown', (e) => {
        if (!e.target.matches('tr.call-row') || !['Enter', ' '].includes(e.key)) return;
        e.preventDefault();
        openDetails(e.target.dataset.id);
    });
    document.querySelectorAll('[data-close-call]').forEach(button => button.addEventListener('click', () => $('#call-modal').close()));
    $('#call-modal').addEventListener('close', () => {
        const id = state.openId;
        state.openId = null;
        detailSeq++;
        detailSnapshot = '';
        document.body.classList.remove('call-modal-open');
        const row = [...document.querySelectorAll('tr.call-row')].find(row => row.dataset.id === id);
        (row || $('#calls-scroll')).focus({ preventScroll: true });
    });
    $('#modal-retry').addEventListener('click', async () => {
        if (!state.openId) return;
        const button = $('#modal-retry');
        try { await retry(state.openId, button); await refreshDetails(); }
        finally { button.disabled = false; }
    });

    setInterval(() => { if (state.items.length) renderRows(); }, 30000); // keep "x min ago" fresh

    renderSort();
    load();
    connect();
})();
