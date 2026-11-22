// The interview page. Plain JavaScript, no framework or CDN.
//
// State lives on the server: the interview id is kept in the URL (#interview=…)
// and in localStorage, and every view is rebuilt from GET /api/interviews/{id},
// so a refresh or a container restart picks up where the interview stopped.
// A message's reply streams back as server-sent events over the POST response.
'use strict';

const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const store = {
  get: k => { try { return localStorage.getItem(k); } catch { return null; } },
  set: (k, v) => { try { v == null ? localStorage.removeItem(k) : localStorage.setItem(k, v); } catch { /* private mode */ } }
};
const fmtTime = s => s ? new Date(s).toISOString().slice(0, 16).replace('T', ' ') + ' UTC' : '—';
const num = n => n == null ? '—' : Number(n).toLocaleString();

let interviewId = null;
let interview = null;
let sending = false;
let gageOk = true;
let renderedPreviewKey = null;
let plantsTimer = null;
let liveTimer = null;

// ---------------- API ----------------

async function api(method, url, body) {
  const options = { method, headers: { Accept: 'application/json' } };
  if (body !== undefined) { options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(body); }
  const response = await fetch(url, options);
  let data = null;
  try { data = await response.json(); } catch { /* empty body */ }
  if (!response.ok) {
    const error = new Error(data?.error?.message || `The request failed (HTTP ${response.status}).`);
    error.code = data?.error?.code; error.status = response.status; error.data = data;
    throw error;
  }
  return data;
}

// ---------------- theme and tabs ----------------

function applyTheme(theme) {
  if (theme) document.documentElement.dataset.theme = theme; else delete document.documentElement.dataset.theme;
}
applyTheme(store.get('flowsim.theme'));
$('theme').addEventListener('click', () => {
  const dark = document.documentElement.dataset.theme ? document.documentElement.dataset.theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
  const next = dark ? 'light' : 'dark';
  applyTheme(next); store.set('flowsim.theme', next);
  renderedPreviewKey = null; renderPreview(); // the preview reads colors when it draws
});

function showTab(name) {
  for (const t of ['interview', 'plants']) {
    $('tab-' + t).setAttribute('aria-selected', String(t === name));
    $('view-' + t).hidden = t !== name;
  }
  clearInterval(plantsTimer);
  if (name === 'plants') { loadPlants(); plantsTimer = setInterval(loadPlants, 5000); }
}
$('tab-interview').addEventListener('click', () => showTab('interview'));
$('tab-plants').addEventListener('click', () => showTab('plants'));

// ---------------- Gage health ("explain and stop") ----------------

function showGage(gage) {
  gageOk = gage.ok || gage.state === 'unchecked';
  $('gage-banner').hidden = gageOk;
  if (!gageOk) {
    $('gage-message').textContent = gage.message || 'Pulse did not say why.';
    $('gage-code').textContent = gage.code ? `${gage.code} · model ${gage.model || '—'} · checked ${fmtTime(gage.checkedUtc)}` : '';
  }
  updateComposer();
}

async function loadHealth(refresh) {
  try {
    const health = await api('GET', '/api/health' + (refresh ? '?refresh=true' : ''));
    showGage(health.gage);
    if (health.gage.state === 'unchecked') setTimeout(() => loadHealth(false), 3000);
  } catch (e) { /* the page keeps working; the next turn reports problems */ }
}
$('gage-recheck').addEventListener('click', async () => {
  $('gage-recheck').disabled = true;
  await loadHealth(true);
  $('gage-recheck').disabled = false;
});

// ---------------- interview lifecycle ----------------

function setInterviewId(id) {
  interviewId = id;
  store.set('flowsim.interview', id);
  history.replaceState(null, '', id ? '#interview=' + id : location.pathname);
}

async function startInterview() {
  try {
    interview = await api('POST', '/api/interviews');
    setInterviewId(interview.id);
    renderedPreviewKey = null;
    renderInterview();
  } catch (e) {
    renderMessages([], `Could not start an interview: ${e.message}`);
  }
}

async function loadInterview() {
  if (!interviewId) return startInterview();
  try {
    interview = await api('GET', `/api/interviews/${interviewId}`);
    renderInterview();
  } catch (e) {
    if (e.status === 404) { setInterviewId(null); return startInterview(); }
    renderMessages([], `Could not load the interview: ${e.message}`);
  }
}

$('new-interview').addEventListener('click', () => {
  if (sending) return;
  setInterviewId(null);
  $('messages').innerHTML = '';
  startInterview();
  showTab('interview');
});

// ---------------- rendering: chat ----------------

// A small, safe subset of Markdown for the interviewer's replies: headings,
// paragraphs, bullet and numbered lists, pipe tables, rules, **bold**,
// *italic* and `code`. Every piece of text is escaped first.
function formatText(text) {
  const inline = s => esc(s).replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>').replace(/(^|[^*\w])\*([^*]+)\*(?!\w)/g, '$1<em>$2</em>');
  const cells = line => line.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim());
  const out = [];
  let list = null, paragraph = null, table = null;
  const close = () => {
    if (list) out.push(`<${list.type}>${list.items.map(i => `<li>${i}</li>`).join('')}</${list.type}>`);
    if (paragraph) out.push(`<p>${paragraph.join('<br>')}</p>`);
    if (table) out.push(`<div class="mdtable"><table>${table.map((row, i) => `<tr>${row.map(c => i === 0 ? `<th>${inline(c)}</th>` : `<td>${inline(c)}</td>`).join('')}</tr>`).join('')}</table></div>`);
    list = paragraph = table = null;
  };
  for (const raw of text.split('\n')) {
    const line = raw.trimEnd();
    const heading = /^\s*#{1,6}\s+(.*)/.exec(line);
    const bullet = /^\s*[-•*]\s+(.*)/.exec(line), numbered = /^\s*\d+[.)]\s+(.*)/.exec(line);
    if (/^\s*\|.*\|\s*$/.test(line)) {
      if (!table) { close(); table = []; }
      const row = cells(line);
      if (!row.every(c => /^:?-{2,}:?$/.test(c))) table.push(row);
    } else if (/^\s*(-{3,}|\*{3,}|_{3,})\s*$/.test(line)) { close(); out.push('<hr>'); }
    else if (heading) { close(); out.push(`<p class="mdh">${inline(heading[1])}</p>`); }
    else if (bullet || numbered) {
      const type = bullet ? 'ul' : 'ol';
      if (!list || list.type !== type) { close(); list = { type, items: [] }; }
      list.items.push(inline((bullet || numbered)[1]));
    } else if (line.trim() === '') close();
    else if (list && /^\s{2,}/.test(raw)) list.items[list.items.length - 1] += ' ' + inline(line.trim());
    else { if (!paragraph) { close(); paragraph = []; } paragraph.push(inline(line)); }
  }
  close();
  return out.join('');
}

function messageElement(item) {
  const div = document.createElement('div');
  if (item.role === 'tool') { div.className = 'toolchip'; div.textContent = item.text; return div; }
  div.className = 'msg ' + item.role + (item.bad ? ' bad' : '');
  if (item.role === 'assistant') div.innerHTML = formatText(item.text);
  else div.textContent = item.text;
  return div;
}

function renderMessages(items, problem) {
  const box = $('messages');
  box.innerHTML = '';
  if (!items.length) {
    box.innerHTML = `<div class="empty-chat"><b>Who is the demo for?</b><span>Describe the audience (industry, roles in the room), what costs them the most, and the systems they run. The interviewer picks storylines from 43 customer conversations and builds a small simulated plant for you to preview.</span></div>`;
  }
  for (const item of items) box.appendChild(messageElement(item));
  if (problem) box.appendChild(messageElement({ role: 'notice', text: problem, bad: true }));
  box.scrollTop = box.scrollHeight;
}

function append(item) {
  const box = $('messages');
  box.querySelector('.empty-chat')?.remove();
  const element = messageElement(item);
  box.appendChild(element);
  box.scrollTop = box.scrollHeight;
  return element;
}

function updateComposer() {
  const approved = !!interview?.approvedSimulation;
  const blocked = !gageOk;
  $('send').disabled = sending || blocked || approved || !interviewId;
  $('input').disabled = blocked || approved;
  $('composer-hint').textContent = approved ? 'Approved. Start a new interview to design another plant.'
    : blocked ? 'Stopped until Pulse can reach a model (see the banner above).'
    : sending ? 'The interviewer is working…' : 'Enter sends · Shift+Enter for a new line';
}

// ---------------- rendering: side panel ----------------

function renderInterview() {
  renderMessages(interview.messages);
  renderStorylines();
  renderNeeds();
  renderApproval();
  renderPreview();
  renderLive();
  updateComposer();
}

function renderStorylines() {
  const cards = interview.storylines || [];
  if (!cards.length) { $('storylines').innerHTML = '<p class="muted">The interviewer picks storylines from the library once it knows your audience.</p>'; return; }
  $('storylines').innerHTML = '<div class="stories">' + cards.map(s => s.kind === 'chosen'
    ? `<div class="story"><div class="story-head"><b>${esc(s.title)}</b><span class="badge ${esc(s.engine)}">${esc(s.engine)}</span></div><span class="muted">${esc(s.pitch)}</span><span class="muted">Raised in ${esc(s.evidence)} customer meetings.</span></div>`
    : `<div class="story requested"><div class="story-head"><b>Asked for: ${esc(s.words)}</b><span class="badge ${esc(s.engine)}">${esc(s.engine === 'unmapped' ? 'not in library' : 'not yet: ' + s.engine)}</span></div>${s.title && s.title !== s.words ? `<span class="muted">Library: ${esc(s.title)}</span>` : ''}${s.offeredInstead ? `<span class="muted">Offered instead: ${esc(s.offeredInstead)}</span>` : ''}</div>`
  ).join('') + '</div>';
}

function renderNeeds() {
  const a = interview.needs?.audience;
  $('needs-card').hidden = !a;
  if (!a) return;
  const rows = [['Industry', a.industry], ['Product', a.product], ['Roles', (a.roles || []).join(', ')], ['KPIs', (a.kpis || []).join(', ')],
    ['Problems', (a.pains || []).join('; ')], ['Systems', (a.systems || []).join(', ')], ['Constraints', (a.constraints || []).join('; ')], ['History', a.historyDays ? a.historyDays + ' days' : '']];
  $('needs').innerHTML = rows.filter(([, v]) => v).map(([k, v]) => `<dt>${k}</dt><dd>${esc(v)}</dd>`).join('');
}

function renderApproval() {
  const approval = interview.approval;
  const show = !!approval && !interview.approvedSimulation;
  $('approve-card').hidden = !show;
  if (!show) return;
  if (!approval.valid) {
    $('approve-open').disabled = true;
    $('approve-error').hidden = false;
    $('approve-error').textContent = 'The draft no longer validates at the current time. Ask the interviewer to compose and preview it again.';
    return;
  }
  $('approve-open').disabled = false;
  $('approve-facts').innerHTML = [
    ['Simulation', `<code>${esc(approval.simulation)}</code>`],
    ['Backfill', esc(approval.backfill || 'base default') + ` (history from ${esc(fmtTime(approval.historyStartsUtc))})`],
    ['Dataset', approval.newDataset
      ? `<code>${esc(approval.dataset)}</code> <span class="muted">new, created on approval, purge age ${esc(approval.datasetPurgeAgeDays)} days</span>`
      : approval.dataset ? `<code>${esc(approval.dataset)}</code>` : '<span class="muted">none: rows only, no Historian configured</span>'],
    ['Tag root', `<code>${esc(approval.tagRoot)}</code>`],
    ['Tables', `${approval.tables.length} tables named <code>${esc(approval.tablePrefix)}…</code> in flow_data_sim`]
  ].map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('');
  $('approve-tables-summary').textContent = `The ${approval.tables.length} tables`;
  $('approve-tables').innerHTML = approval.tables.map(t => `<li><code>${esc(t)}</code></li>`).join('');
}

$('approve-open').addEventListener('click', () => { $('approve-confirm').hidden = false; $('approve-open').hidden = true; $('approve-error').hidden = true; });
$('approve-no').addEventListener('click', () => { $('approve-confirm').hidden = true; $('approve-open').hidden = false; });
$('approve-yes').addEventListener('click', async () => {
  $('approve-yes').disabled = true;
  $('approve-error').hidden = true;
  try {
    await api('POST', `/api/interviews/${interviewId}/approve`, { confirm: true });
    $('approve-confirm').hidden = true; $('approve-open').hidden = false;
    await loadInterview();
  } catch (e) {
    $('approve-error').hidden = false;
    $('approve-error').textContent = e.message + (e.code ? ` (${e.code})` : '');
  } finally { $('approve-yes').disabled = false; }
});

function renderPreview() {
  const preview = interview?.preview;
  $('preview-section').hidden = !preview;
  if (!preview) { renderedPreviewKey = null; return; }
  const key = interview.id + preview.anchorUtc + preview.changeCount;
  if (key === renderedPreviewKey) return;
  renderedPreviewKey = key;
  try {
    PlantPreviewRenderer.render($('preview'), PlantPreviewRenderer.fromPreview(preview));
  } catch (e) {
    $('preview').innerHTML = '<p class="error">The preview could not be drawn. Ask the interviewer to run the preview again.</p>';
  }
}

// After approval: live status, downloads and the demo guide.
function renderLive() {
  const name = interview.approvedSimulation;
  clearInterval(liveTimer);
  $('live-card').hidden = !name;
  if (!name) return;
  $('live-title').textContent = name;
  const base = `/api/plants/${encodeURIComponent(name)}`;
  $('live-downloads').innerHTML = [
    [`${base}/demo-guide?format=html`, 'Demo guide'], [`${base}/demo-guide`, 'Demo guide (Markdown)'],
    [`${base}/connect-spec`, 'Connect spec'], [`${base}/connect-spec?format=json`, 'Connect spec (JSON)'], [`${base}/atlas-starter`, 'Atlas starter']
  ].map(([href, label]) => `<a href="${href}" target="_blank" rel="noopener">${label}</a>`).join('');
  const refresh = async () => {
    try {
      const data = await api('GET', '/api/plants');
      const plant = data.plants.find(p => p.simulation === name);
      if (!plant) { $('live-facts').innerHTML = '<dt>Status</dt><dd>Removed from flow_data_sim</dd>'; $('live-downloads').innerHTML = ''; clearInterval(liveTimer); return; }
      const h = plant.hosting;
      $('live-facts').innerHTML = [
        ['Status', `<span class="badge ${esc(h.state)}">${esc(h.state.replace('_', ' '))}</span>${h.message ? ` <span class="muted">${esc(h.message)}</span>` : ''}`],
        ['Delivered through', esc(fmtTime(h.throughUtc || plant.tagCursorUtc))],
        ['Rows · tag points', `${num(h.rowsApplied)} · ${num(h.pointsPublished)} this run`],
        ['Tag root', `<code>${esc(name)}</code>`],
        ['Tables', `<code>${esc(plant.tablePrefix)}…</code>`],
        ['Dataset', `<code>${esc(plant.dataset || '—')}</code>`]
      ].map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('');
    } catch { /* keep the last status */ }
  };
  refresh();
  liveTimer = setInterval(refresh, 5000);
}

// ---------------- sending a message (SSE over the POST response) ----------------

async function send(text) {
  if (sending || !interviewId) return;
  sending = true; updateComposer();
  append({ role: 'user', text });
  let chip = null;
  const finishChip = () => { chip?.classList.remove('running'); chip = null; };
  try {
    const response = await fetch(`/api/interviews/${interviewId}/messages`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' }, body: JSON.stringify({ text })
    });
    if (!response.ok) {
      let data = null; try { data = await response.json(); } catch { }
      append({ role: 'notice', text: data?.error?.message || `The message was not sent (HTTP ${response.status}).`, bad: true });
      return;
    }
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let cut;
      while ((cut = buffer.indexOf('\n\n')) >= 0) {
        const raw = buffer.slice(0, cut); buffer = buffer.slice(cut + 2);
        const name = /^event: (.*)$/m.exec(raw)?.[1];
        const dataLine = /^data: (.*)$/m.exec(raw)?.[1];
        if (!name || !dataLine) continue;
        const data = JSON.parse(dataLine);
        if (name === 'tool_activity') { finishChip(); chip = append({ role: 'tool', text: data.text }); chip.classList.add('running'); }
        else if (name === 'assistant_text') { finishChip(); append({ role: 'assistant', text: data.text }); }
        else if (name === 'preview_ready') { finishChip(); }
        else if (name === 'stopped') { finishChip(); append({ role: 'notice', text: data.text, bad: true }); if (data.code?.startsWith('gage.')) showGage({ ok: false, state: 'stopped', code: data.code, message: data.text, checkedUtc: new Date().toISOString() }); }
        else if (name === 'refusal' || name === 'error' || name === 'failure') { finishChip(); append({ role: 'notice', text: data.text, bad: name !== 'refusal' }); }
        else if (name === 'done') { finishChip(); }
      }
    }
  } catch (e) {
    append({ role: 'notice', text: 'The connection dropped. The interviewer may still be answering; the page reloads the conversation.', bad: true });
  } finally {
    finishChip();
    sending = false;
    await loadInterview(); // the stored state is the truth: cards, preview, approval
  }
}

$('composer').addEventListener('submit', e => {
  e.preventDefault();
  const text = $('input').value.trim();
  if (!text || $('send').disabled) return;
  $('input').value = '';
  send(text);
});
$('input').addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); $('composer').requestSubmit(); }
});

// ---------------- Plants tab ----------------

let pendingRemove = null;
let pendingRemoveDataset = false; // the Remove dialog's checkbox; the list redraws every 5 s

async function loadPlants() {
  try {
    const data = await api('GET', '/api/plants');
    $('plants-error').hidden = true;
    $('plants-dataset').textContent = data.datasetPurgeAgeDays ? `Each plant approved here writes its tags to its own Historian Dataset, named after the plant, with a ${data.datasetPurgeAgeDays}-day purge age.` : '';
    $('plants').innerHTML = data.plants.length ? data.plants.map(plantRow).join('') : '<tr><td colspan="7" class="muted">No plants yet. Approve a preview on the Interview tab.</td></tr>';
  } catch (e) {
    $('plants-error').hidden = false;
    $('plants-error').textContent = e.message;
  }
}

function plantRow(p) {
  const h = p.hosting, name = esc(p.simulation), running = ['starting', 'backfilling', 'live', 'error'].includes(h.state);
  const base = `/api/plants/${encodeURIComponent(p.simulation)}`;
  const actions = p.managed
    ? (running ? `<button type="button" data-act="stop" data-name="${name}">Stop</button>` : h.state === 'running_elsewhere' ? '' : `<button type="button" data-act="start" data-name="${name}">Start</button>`) +
      `<button type="button" class="danger" data-act="remove" data-name="${name}">Remove…</button>`
    : '<span class="muted">read-only</span>';
  const datasetChoice = p.datasetCreated
    ? `<label class="remove-dataset"><input type="checkbox" data-remove-dataset="${name}"${pendingRemoveDataset ? ' checked' : ''}><span>Also delete its Dataset <code>${esc(p.dataset)}</code> and every tag in it. This cannot be undone.</span></label>`
    : '';
  const keeps = p.datasetCreated ? 'Its Dataset and tags stay unless you tick the box below.' : 'Tags already in Historian stay.';
  const confirm = pendingRemove === p.simulation
    ? `<div class="remove-confirm"><b>Remove ${name}?</b><span>This stops the plant and drops its tables (<code>${esc(p.tablePrefix)}…</code>) and registration. Other plants are not touched. ${keeps}</span>${datasetChoice}<div class="row"><button type="button" class="danger solid" data-act="remove-yes" data-name="${name}">Remove ${name}</button><button type="button" data-act="remove-no" data-name="${name}">Cancel</button></div></div>`
    : '';
  return `<tr><td><b>${name}</b><div class="detail"><code>${esc(p.tablePrefix)}…</code>${p.dataset ? ` · Dataset <code>${esc(p.dataset)}</code>` : ''}</div></td>
    <td><span class="badge ${esc(h.state)}">${esc(h.state.replace('_', ' '))}</span>${h.message || h.code ? `<div class="detail">${h.code ? `<code>${esc(h.code)}</code> ` : ''}${esc(h.message || '')}</div>` : ''}</td>
    <td class="num">${esc(fmtTime(h.throughUtc || p.tagCursorUtc))}</td><td class="num">${num(h.rowsApplied || null)}</td><td class="num">${num(h.pointsPublished || null)}</td><td class="num">${esc(fmtTime(p.snapshotUtc))}</td>
    <td><div class="actions">${actions}</div>${p.managed ? `<div class="downloads" style="margin-top:6px"><a href="${base}/demo-guide?format=html" target="_blank" rel="noopener">Demo guide</a><a href="${base}/connect-spec" target="_blank" rel="noopener">Connect spec</a><a href="${base}/atlas-starter" target="_blank" rel="noopener">Atlas starter</a></div>` : ''}${confirm}</td></tr>`;
}

$('plants').addEventListener('change', e => {
  if (e.target.matches('input[data-remove-dataset]')) pendingRemoveDataset = e.target.checked;
});

$('plants').addEventListener('click', async e => {
  const button = e.target.closest('button[data-act]');
  if (!button) return;
  const name = button.dataset.name, act = button.dataset.act;
  if (act === 'remove') { pendingRemove = name; pendingRemoveDataset = false; return loadPlants(); }
  if (act === 'remove-no') { pendingRemove = null; pendingRemoveDataset = false; return loadPlants(); }
  button.disabled = true;
  try {
    if (act === 'stop' || act === 'start') await api('POST', `/api/plants/${encodeURIComponent(name)}/${act}`);
    if (act === 'remove-yes') {
      await api('DELETE', `/api/plants/${encodeURIComponent(name)}?confirm=true${pendingRemoveDataset ? '&deleteDataset=true' : ''}`);
      pendingRemove = null;
      pendingRemoveDataset = false;
    }
  } catch (err) {
    $('plants-error').hidden = false;
    $('plants-error').textContent = err.message + (err.code ? ` (${err.code})` : '');
  }
  await loadPlants();
});

// ---------------- start ----------------

interviewId = /#interview=([0-9a-f-]{36})/.exec(location.hash)?.[1] || store.get('flowsim.interview');
loadHealth(false);
loadInterview();
