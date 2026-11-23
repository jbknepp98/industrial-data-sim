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
let pendingFiles = [];   // files attached to the next message: { name, size, state: uploading | ready | error, id, kind, message }
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

// One button cycles System → Light → Dark, as in Historian and the other Timebase
// apps; its icon (Lucide monitor, sun, moon) shows the current mode.
const THEMES = {
  system: { label: 'System theme', icon: '<rect width="20" height="14" x="2" y="3" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/>' },
  light: { label: 'Light theme', icon: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2"/><path d="M12 20v2"/><path d="m4.93 4.93 1.41 1.41"/><path d="m17.66 17.66 1.41 1.41"/><path d="M2 12h2"/><path d="M20 12h2"/><path d="m6.34 17.66-1.41 1.41"/><path d="m19.07 4.93-1.41 1.41"/>' },
  dark: { label: 'Dark theme', icon: '<path d="M20.985 12.486a9 9 0 1 1-9.473-9.472c.405-.022.617.46.402.803a6 6 0 0 0 8.268 8.268c.344-.215.825-.004.803.401"/>' }
};
const THEME_ORDER = ['system', 'light', 'dark'];
let theme = THEMES[store.get('flowsim.theme')] ? store.get('flowsim.theme') : 'system';
function applyTheme(next) {
  theme = next;
  if (theme === 'system') delete document.documentElement.dataset.theme; else document.documentElement.dataset.theme = theme;
  $('theme-icon').innerHTML = THEMES[theme].icon;
  $('theme').setAttribute('aria-label', THEMES[theme].label);
  $('theme').title = THEMES[theme].label;
}
applyTheme(theme);
$('theme').addEventListener('click', () => {
  applyTheme(THEME_ORDER[(THEME_ORDER.indexOf(theme) + 1) % THEME_ORDER.length]);
  store.set('flowsim.theme', theme);
  renderedPreviewKey = null; renderPreview(); // the preview reads colors when it draws
});
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
  if (theme === 'system') { renderedPreviewKey = null; renderPreview(); }
});

// Like the other Timebase apps: the sidebar opens a view as a tab in the top strip;
// a tab closes with its ×, and the last open tab stays.
const VIEWS = {
  interview: { title: 'Interview', icon: '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>' },
  plants: { title: 'Plants', icon: '<path d="M2 20a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8l-7 5V8l-7 5V4a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2Z"/>' }
};
const openTabs = [];
let currentTab = null;

function renderTabs() {
  $('open-tabs').innerHTML = openTabs.map(t => `<div class="tab${t === currentTab ? ' active' : ''}">
    <button type="button" class="tab-open" data-tab="${t}"${t === currentTab ? ' aria-current="page"' : ''}><svg class="icon" viewBox="0 0 24 24" aria-hidden="true">${VIEWS[t].icon}</svg><span>${VIEWS[t].title}</span></button>
    ${openTabs.length > 1 ? `<button type="button" class="tab-close" data-close="${t}" aria-label="Close ${VIEWS[t].title}"><svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18"/><path d="m6 6 12 12"/></svg></button>` : ''}</div>`).join('');
}
$('open-tabs').addEventListener('click', e => {
  const close = e.target.closest('[data-close]');
  if (close) {
    const i = openTabs.indexOf(close.dataset.close);
    openTabs.splice(i, 1);
    if (close.dataset.close === currentTab) return showTab(openTabs[Math.max(0, i - 1)]);
    return renderTabs();
  }
  const open = e.target.closest('[data-tab]');
  if (open) showTab(open.dataset.tab);
});

function showTab(name) {
  if (!openTabs.includes(name)) openTabs.push(name);
  currentTab = name;
  for (const t of Object.keys(VIEWS)) {
    $('tab-' + t).setAttribute('aria-selected', String(t === name));
    $('view-' + t).hidden = t !== name;
  }
  renderTabs();
  writeHash();
  clearInterval(plantsTimer);
  if (name === 'plants') { loadPlants(); plantsTimer = setInterval(loadPlants, 5000); }
}
$('tab-interview').addEventListener('click', () => showTab('interview'));
$('tab-plants').addEventListener('click', () => showTab('plants'));

function applySidebar(collapsed) {
  document.body.classList.toggle('collapsed', collapsed);
  $('collapse').setAttribute('aria-expanded', String(!collapsed));
  $('collapse').setAttribute('aria-label', collapsed ? 'Expand the sidebar' : 'Collapse the sidebar');
}
applySidebar(store.get('flowsim.sidebar') === 'collapsed');
$('collapse').addEventListener('click', () => {
  const collapsed = !document.body.classList.contains('collapsed');
  applySidebar(collapsed); store.set('flowsim.sidebar', collapsed ? 'collapsed' : null);
});

let lastHealth = null;
function renderInstance() {
  const h = lastHealth, g = h?.gage;
  $('instance-facts').innerHTML = h
    ? `<dt>Page</dt><dd>${esc(h.status)}</dd><dt>Postgres</dt><dd>${esc(h.database)} · <code>flow_data_sim</code></dd><dt>Gage</dt><dd>${esc(g?.state || 'unknown')}${g?.answeredBy ? ` · <code>${esc(g.answeredBy)}</code>` : ''}</dd>${g?.checkedUtc ? `<dt>Checked</dt><dd>${esc(fmtTime(g.checkedUtc))}</dd>` : ''}`
    : '<dt>Status</dt><dd>Not checked yet</dd>';
}
$('instance').addEventListener('click', () => {
  const open = $('instance-info').hidden;
  $('instance-info').hidden = !open;
  $('instance').setAttribute('aria-expanded', String(open));
  if (open) renderInstance();
});

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
    lastHealth = health;
    if (!$('instance-info').hidden) renderInstance();
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
  if (!id) step = 'chat';
  writeHash();
}
// The URL keeps the interview, its step and the open plant together, so a
// reload or a shared link comes back to the same place:
// #interview=…&step=review&plant=Name&section=tables&table=name_lots
function writeHash() {
  const parts = [];
  if (interviewId) parts.push('interview=' + interviewId);
  if (interviewId && step === 'review') parts.push('step=review');
  if (currentTab === 'plants' && browse?.plant) {
    parts.push('plant=' + encodeURIComponent(browse.plant));
    if (browse.section !== 'summary') parts.push('section=' + browse.section);
    if (browse.section === 'tables' && browse.table) parts.push('table=' + encodeURIComponent(browse.table));
  }
  history.replaceState(null, '', parts.length ? '#' + parts.join('&') : location.pathname);
}

// ---------------- steps: Interview, then Review plan ----------------
// Once a plant has been previewed, the conversation can be put away: Review plan
// (in the Ready to build band) hides the chat and gives Storylines, the needs
// profile and the plant preview the full width; Back to Interview returns. The
// step is kept in the URL (&step=review), so a reload keeps it.
let step = /[#&]step=review\b/.test(location.hash) ? 'review' : 'chat';
function setStep(next) {
  if (next === 'review' && !interview?.preview) next = 'chat';
  const changed = next !== step;
  step = next;
  document.body.classList.toggle('step-review', step === 'review');
  // The band's actions follow the step: Review plan on the interview, then
  // Back to Interview on the left and Approve plan on the right.
  $('dock-back').hidden = step !== 'review';
  $('dock-review').hidden = step === 'review';
  $('approve-open').hidden = step !== 'review';
  if (step !== 'review') { $('approve-confirm').hidden = true; $('approve-bar').hidden = false; }
  renderDockText();
  writeHash();
  if (changed) {
    renderedPreviewKey = null; renderPreview();   // redraw the preview at its new width
    window.scrollTo({ top: 0 });
    if (step === 'chat') { const box = $('messages'); box.scrollTop = box.scrollHeight; }
    $(step === 'review' ? 'approve-open' : 'dock-review').focus({ preventScroll: true });
  }
}
$('dock-review').addEventListener('click', () => setStep('review'));
$('dock-back').addEventListener('click', () => setStep('chat'));
function renderSteps() { setStep(step); }

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

// + New plant in the top bar is the one place a plant is started: a fresh
// interview on step 1.
$('new-interview').addEventListener('click', () => {
  if (sending) return;
  setInterviewId(null);
  $('messages').innerHTML = '';
  pendingFiles = []; renderPendingFiles();
  startInterview();
  setStep('chat');
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
  else if (item.role === 'user' && item.attachments?.length) {
    const files = document.createElement('div');
    files.className = 'msg-files';
    for (const file of item.attachments) files.appendChild(fileChip(file));
    div.appendChild(files);
    if (item.text) { const p = document.createElement('div'); p.textContent = item.text; div.appendChild(p); }
  }
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

function renderDockText() {
  const name = interview?.approval?.simulation;
  if (!name) return;
  const rebuild = !!interview.approval.replaces;
  $('approve-sub').innerHTML = `<code>${esc(name)}</code> · ` + (rebuild
    ? (step === 'review' ? 'Approving deletes its current data and rebuilds it from this plan.' : 'The interviewer has a changed plan. Review it before you rebuild.')
    : (step === 'review' ? 'Check the storylines and the preview. Nothing is written until you approve.' : 'The interviewer has a plan. Review it before you approve.'));
}

function updateComposer() {
  // The Ready to build band waits until the interviewer's turn is over.
  document.body.classList.toggle('turn-running', sending);
  // An approved interview stays open: messages there change its plant.
  const blocked = !gageOk;
  const uploading = pendingFiles.some(f => f.state === 'uploading');
  $('send').disabled = sending || blocked || !interviewId || uploading;
  $('input').disabled = blocked;
  $('attach').disabled = blocked || !interviewId;
  $('composer-hint').textContent = blocked ? 'Stopped until Pulse can reach a model (see the banner above).'
    : sending ? 'The interviewer is working…' : 'Enter sends · Shift+Enter for a new line';
}

// ---------------- rendering: side panel ----------------

function renderInterview() {
  const plant = interview.approvedSimulation;
  $('plant-context').hidden = !plant;
  if (plant) {
    $('plant-context').textContent = `Plant: ${plant}`;
    $('plant-context').title = `This interview built ${plant}. Changes you approve here rebuild it.`;
  }
  renderMessages(interview.messages);
  renderStorylines();
  renderNeeds();
  renderApproval();
  renderPreview();
  renderLive();
  renderSteps();
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
  const action = interview.pendingAction;
  const show = !!approval || !!action;
  $('approve-card').hidden = !show;
  document.body.classList.toggle('approve-docked', show);
  // A stop or start the interviewer proposed takes the band until it is confirmed or cancelled.
  $('action-bar').hidden = !action;
  if (action) {
    $('approve-bar').hidden = true; $('approve-confirm').hidden = true;
    const verb = action.action === 'stop' ? 'Stop' : 'Start';
    $('action-title').textContent = `${verb} ${action.plant}?`;
    $('action-yes').textContent = `${verb} plant`;
    return;
  }
  $('approve-bar').hidden = !$('approve-confirm').hidden;
  if (!approval) return;
  const rebuild = !!approval.replaces;
  $('dock-title').textContent = rebuild ? 'Ready to rebuild' : 'Ready to build';
  $('approve-heading').textContent = rebuild ? `Rebuild ${approval.replaces} from this plan?` : 'Build and run this plant?';
  $('rebuild-warning').hidden = !rebuild;
  document.querySelectorAll('.rebuild-name').forEach(n => { n.textContent = approval.replaces || ''; });
  $('approve-yes').textContent = rebuild ? `Rebuild ${approval.replaces}` : 'Approve plan';
  if (!approval.valid) {
    $('approve-open').disabled = true;
    $('approve-error').hidden = false;
    $('approve-error').textContent = 'The draft no longer validates at the current time. Ask the interviewer to compose and preview it again.';
    return;
  }
  $('approve-open').disabled = false;
  renderDockText();
  $('approve-facts').innerHTML = [
    ['Simulation', `<code>${esc(approval.simulation)}</code>`],
    ['Backfill', esc(approval.backfill || 'base default') + ` (history from ${esc(fmtTime(approval.historyStartsUtc))})`],
    ['Dataset', rebuild && approval.dataset
      ? `<code>${esc(approval.dataset)}</code> <span class="muted">emptied and reused: its tag history is deleted</span>`
      : approval.newDataset
      ? `<code>${esc(approval.dataset)}</code> <span class="muted">new, created on approval, purge age ${esc(approval.datasetPurgeAgeDays)} days</span>`
      : approval.dataset ? `<code>${esc(approval.dataset)}</code>` : '<span class="muted">none: rows only, no Historian configured</span>'],
    ['Tag root', `<code>${esc(approval.tagRoot)}</code>`],
    ['Tables', `${approval.tables.length} tables named <code>${esc(approval.tablePrefix)}…</code> in flow_data_sim`]
  ].map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('');
  $('approve-tables-summary').textContent = `The ${approval.tables.length} tables`;
  $('approve-tables').innerHTML = approval.tables.map(t => `<li><code>${esc(t)}</code></li>`).join('');
}

$('approve-open').addEventListener('click', () => {
  $('approve-confirm').hidden = false; $('approve-bar').hidden = true; $('approve-error').hidden = true;
  // Rebuilding a running plant takes a second, explicit step: tick the box, then Rebuild.
  const rebuild = !!interview?.approval?.replaces;
  $('rebuild-ack').checked = false;
  $('approve-yes').disabled = rebuild;
  (rebuild ? $('rebuild-ack') : $('approve-yes')).focus();
});
$('rebuild-ack').addEventListener('change', () => { $('approve-yes').disabled = !$('rebuild-ack').checked; });
async function answerAction(confirm) {
  $('action-yes').disabled = $('action-cancel').disabled = true;
  $('approve-error').hidden = true;
  try {
    const result = await api('POST', `/api/interviews/${interviewId}/plant-action`, { confirm });
    await loadInterview();
    if (confirm && currentTab === 'plants') loadPlants();
    return result;
  } catch (e) {
    $('approve-error').hidden = false;
    $('approve-error').textContent = e.message + (e.code ? ` (${e.code})` : '');
  } finally { $('action-yes').disabled = $('action-cancel').disabled = false; }
}
$('action-yes').addEventListener('click', () => answerAction(true));
$('action-cancel').addEventListener('click', () => answerAction(false));
$('approve-no').addEventListener('click', () => { $('approve-confirm').hidden = true; $('approve-bar').hidden = false; $('approve-open').focus(); });

// Wide layout: the chat panel ends 16 px above the window's bottom edge (or above
// the Ready to build band) whether the page is scrolled or not, so the composer
// stays in view. (CSS alone cannot: the panel starts below
// the page title, then sticks under the top bar.)
const chatPanel = document.querySelector('.chat');
let fitQueued = false;
function fitChat() {
  fitQueued = false;
  if (matchMedia('(max-width: 980px)').matches) { chatPanel.style.height = ''; return; }
  const top = Math.max(chatPanel.getBoundingClientRect().top, 72);
  const dock = $('approve-card');
  const reserve = dock.hidden ? 0 : dock.offsetHeight;
  chatPanel.style.height = Math.max(420, window.innerHeight - top - reserve - 16) + 'px';
}
const queueFit = () => { if (!fitQueued) { fitQueued = true; requestAnimationFrame(fitChat); } };
window.addEventListener('scroll', queueFit, { passive: true });
window.addEventListener('resize', queueFit);
// Also refit when the view is shown or anything above it (the Gage banner) changes size.
if ('ResizeObserver' in window) {
  const ro = new ResizeObserver(queueFit); ro.observe(document.body); ro.observe($('view-interview'));
  new ResizeObserver(() => { document.body.style.setProperty('--dock-h', $('approve-card').offsetHeight + 'px'); queueFit(); }).observe($('approve-card'));
}
queueFit();

// The chat footer (the composer) floats over the
// messages; keep the last message clear of it whatever its height.
if ('ResizeObserver' in window) {
  new ResizeObserver(([entry]) => {
    document.querySelector('.chat').style.setProperty('--foot-h', Math.ceil(entry.borderBoxSize?.[0]?.blockSize ?? entry.target.offsetHeight) + 'px');
  }).observe($('chat-foot'));
}
$('approve-yes').addEventListener('click', async () => {
  $('approve-yes').disabled = true;
  $('approve-error').hidden = true;
  try {
    const replaceExisting = !!interview?.approval?.replaces;
    if (replaceExisting && !$('rebuild-ack').checked) return;
    await api('POST', `/api/interviews/${interviewId}/approve`, { confirm: true, replaceExisting });
    $('approve-confirm').hidden = true; $('approve-bar').hidden = false;
    const approvedName = interview?.approval?.simulation;
    await loadInterview();
    // Show the plant where it now lives: its page on the Plants tab, Backfilling until it catches up.
    showTab('plants');
    if (approvedName) openPlant(approvedName);
  } catch (e) {
    $('approve-error').hidden = false;
    $('approve-error').textContent = e.message + (e.code ? ` (${e.code})` : '');
  } finally { $('approve-yes').disabled = false; }
});

function renderPreview() {
  const preview = interview?.preview;
  $('preview-section').hidden = !preview;
  $('gage-kit').hidden = !preview;
  if (!preview) { renderedPreviewKey = null; return; }
  const key = interview.id + preview.anchorUtc + preview.changeCount;
  if (key === renderedPreviewKey) return;
  renderedPreviewKey = key;
  renderGageKit();
  try {
    PlantPreviewRenderer.render($('preview'), PlantPreviewRenderer.fromPreview(preview));
  } catch (e) {
    $('preview').innerHTML = '<p class="error">The preview could not be drawn. Ask the interviewer to run the preview again.</p>';
  }
}

// The Gage kit at the bottom of Review plan: sample prompts with a copy button,
// and the Connect and Atlas briefs to hand Gage. Served for the interview's
// plan, so it stays available after the plant is built (and on the Plants tab).
// Files to save carry a download symbol and save straight away; the demo guide
// (HTML) is a page to read, so it opens in a new tab instead.
const DOWNLOAD_ICON = '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 15V3"/><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10 5 5 5-5"/></svg>';
const OPEN_ICON = '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M15 3h6v6"/><path d="M10 14 21 3"/><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/></svg>';
function downloadLink(href, label, file = '') { return `<a class="file" href="${href}" download="${esc(file)}" title="Download ${label}">${DOWNLOAD_ICON}<span>${label}</span></a>`; }
function viewLink(href, label) { return `<a class="file" href="${href}" target="_blank" rel="noopener" title="Open ${label} in a new tab">${OPEN_ICON}<span>${label}</span></a>`; }
function gageLinks(base) {
  return [[`${base}/gage/connect`, 'Gage brief: Connect'], [`${base}/gage/atlas`, 'Gage brief: Atlas'], [`${base}/gage/prompts`, 'Gage prompts']]
    .map(([href, label]) => downloadLink(href, label)).join('');
}
function plantFiles(base) {
  const name = decodeURIComponent(base.split('/').pop());
  return viewLink(`${base}/demo-guide?format=html`, 'Demo guide') + downloadLink(`${base}/demo-guide`, 'Demo guide (Markdown)', `${name}-demo-guide.md`) +
    downloadLink(`${base}/connect-spec`, 'Connect spec', `${name}-connect-spec.md`) + downloadLink(`${base}/connect-spec?format=json`, 'Connect spec (JSON)', `${name}-connect-spec.json`) +
    downloadLink(`${base}/atlas-starter`, 'Atlas starter', `${name}-atlas-starter.json`) + gageLinks(base);
}
async function renderGageKit() {
  const id = interview?.id;
  const base = `/api/interviews/${id}`;
  $('gage-downloads').innerHTML = gageLinks(base);
  let prompts = [];
  try { prompts = await api('GET', `${base}/gage/prompts?format=json`); } catch { /* the downloads still work */ }
  if (interview?.id !== id) return;
  $('gage-prompts').innerHTML = '';
  for (const p of prompts) {
    const item = document.createElement('li');
    item.className = 'prompt';
    item.innerHTML = `<div class="prompt-head"><b></b><button type="button" class="copy">Copy</button></div><blockquote></blockquote><span class="muted"></span>`;
    item.querySelector('b').textContent = p.title;
    item.querySelector('blockquote').textContent = p.prompt;
    item.querySelector('.muted').textContent = 'Expect: ' + p.expect;
    const button = item.querySelector('.copy');
    button.addEventListener('click', async () => {
      try { await navigator.clipboard.writeText(p.prompt); button.textContent = 'Copied'; }
      catch { const range = document.createRange(); range.selectNodeContents(item.querySelector('blockquote')); getSelection().removeAllRanges(); getSelection().addRange(range); button.textContent = 'Selected'; }
      setTimeout(() => { button.textContent = 'Copy'; }, 1600);
    });
    $('gage-prompts').appendChild(item);
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
  $('live-downloads').innerHTML = plantFiles(base);
  const refresh = async () => {
    try {
      const data = await api('GET', '/api/plants');
      const plant = data.plants.find(p => p.simulation === name);
      if (!plant) { $('live-facts').innerHTML = '<dt>Status</dt><dd>Removed from flow_data_sim</dd>'; $('live-downloads').innerHTML = ''; clearInterval(liveTimer); return; }
      const h = plant.hosting;
      $('live-facts').innerHTML = [
        ['Status', `<span class="badge ${esc(shownState(h.state))}">${esc(stateLabel(h.state))}</span>${h.message ? ` <span class="muted">${esc(h.message)}</span>` : ''}`],
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

async function send(text, attachments = []) {
  if (sending || !interviewId) return;
  sending = true; updateComposer();
  append({ role: 'user', text, attachments });
  let chip = null;
  const finishChip = () => { chip?.classList.remove('running'); chip = null; };
  try {
    const response = await fetch(`/api/interviews/${interviewId}/messages`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' }, body: JSON.stringify({ text, attachments: attachments.map(a => a.id) })
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
  if (interview?.handoff) await followHandoff();
}

// change_plant: a change to an existing plant continues in the interview that
// built it. Take the hand-off, open that interview, and send the request there.
async function followHandoff() {
  let handoff;
  try { handoff = await api('POST', `/api/interviews/${interviewId}/handoff`); }
  catch (e) { append({ role: 'notice', text: e.message, bad: true }); return; }
  append({ role: 'notice', text: `Opening ${handoff.plant}'s own interview to make this change…` });
  await new Promise(resolve => setTimeout(resolve, 900));
  setInterviewId(handoff.interviewId);
  pendingFiles = []; renderPendingFiles();
  setStep('chat'); showTab('interview');
  await loadInterview();
  if (handoff.request) await send(handoff.request);
}

$('composer').addEventListener('submit', e => {
  e.preventDefault();
  const text = $('input').value.trim();
  const ready = pendingFiles.filter(f => f.state === 'ready');
  if ((!text && !ready.length) || $('send').disabled) return;
  $('input').value = '';
  growInput();
  pendingFiles = [];
  renderPendingFiles();
  send(text, ready.map(f => ({ id: f.id, name: f.name, kind: f.kind })));
});

// ---------------- attachments ----------------
// Files go to the server as soon as they are picked (POST …/attachments, base64
// in JSON); the message then refers to them by id. The server checks the type
// by content, converts Excel and Word to text, and refuses what it cannot read.
const MAX_FILE_BYTES = 10 * 1024 * 1024, MAX_FILES_PER_MESSAGE = 5;
const KIND_LABEL = { pdf: 'PDF', image: 'Image', text: 'Text' };
function fileExtension(name) { return (/\.([a-z0-9]+)$/i.exec(name)?.[1] || '').toUpperCase(); }
function sizeText(bytes) { return bytes >= 1048576 ? (bytes / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(bytes / 1024)) + ' KB'; }
function fileChip(file, onRemove) {
  const chip = document.createElement('span');
  chip.className = 'file-chip' + (file.state ? ' ' + file.state : '');
  const badge = document.createElement('span');
  badge.className = 'file-badge';
  badge.textContent = fileExtension(file.name || '') || KIND_LABEL[file.kind] || 'FILE';
  const label = document.createElement('span');
  label.className = 'file-name';
  label.textContent = file.name;
  chip.title = file.message || file.name;
  chip.append(badge, label);
  if (file.state) {
    const meta = document.createElement('span');
    meta.className = 'file-meta';
    meta.textContent = file.state === 'uploading' ? 'Uploading…' : file.state === 'error' ? file.message : sizeText(file.size) + (file.truncated ? ' · first part only' : '');
    chip.appendChild(meta);
  }
  if (onRemove) {
    const remove = document.createElement('button');
    remove.type = 'button'; remove.className = 'file-remove'; remove.setAttribute('aria-label', 'Remove ' + file.name);
    remove.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"/></svg>';
    remove.addEventListener('click', onRemove);
    chip.appendChild(remove);
  }
  return chip;
}
function renderPendingFiles() {
  const box = $('files');
  box.innerHTML = '';
  for (const file of pendingFiles)
    box.appendChild(fileChip(file, () => { pendingFiles = pendingFiles.filter(f => f !== file); renderPendingFiles(); $('input').focus(); }));
  box.hidden = pendingFiles.length === 0;
  updateComposer();
}
function readBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}
async function addFiles(list) {
  if (!interviewId || $('attach').disabled) return;
  for (const file of Array.from(list)) {
    const entry = { key: Math.random(), name: file.name, size: file.size, state: 'uploading' };
    if (pendingFiles.filter(f => f.state !== 'error').length >= MAX_FILES_PER_MESSAGE) {
      Object.assign(entry, { state: 'error', message: `At most ${MAX_FILES_PER_MESSAGE} files per message` });
    } else if (file.size > MAX_FILE_BYTES) {
      Object.assign(entry, { state: 'error', message: 'Over 10 MB' });
    }
    pendingFiles.push(entry);
    renderPendingFiles();
    if (entry.state === 'error') continue;
    try {
      const data = await readBase64(file);
      const stored = await api('POST', `/api/interviews/${interviewId}/attachments`, { name: file.name, data });
      Object.assign(entry, { state: 'ready', id: stored.id, kind: stored.kind, name: stored.name, truncated: stored.truncated });
    } catch (e) {
      Object.assign(entry, { state: 'error', message: e.message });
    }
    renderPendingFiles();
  }
}
$('attach').addEventListener('click', () => $('file-input').click());
$('file-input').addEventListener('change', e => { addFiles(e.target.files); e.target.value = ''; });
$('input').addEventListener('paste', e => { if (e.clipboardData?.files?.length) { e.preventDefault(); addFiles(e.clipboardData.files); } });
// Drop files anywhere on the conversation.
chatPanel.addEventListener('dragover', e => { if (e.dataTransfer?.types?.includes('Files')) { e.preventDefault(); chatPanel.classList.add('dropping'); } });
chatPanel.addEventListener('dragleave', e => { if (!chatPanel.contains(e.relatedTarget)) chatPanel.classList.remove('dropping'); });
chatPanel.addEventListener('drop', e => { if (e.dataTransfer?.files?.length) { e.preventDefault(); chatPanel.classList.remove('dropping'); addFiles(e.dataTransfer.files); } });
// The input grows with what is typed, up to 10 lines, then scrolls.
function growInput() {
  const box = $('input');
  box.style.height = 'auto';
  box.style.height = Math.min(box.scrollHeight, 240) + 'px';
}
$('input').addEventListener('input', growInput);
$('input').addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); $('composer').requestSubmit(); }
});

// ---------------- Plants: the list, and one plant's page ----------------
// The list is the overview (like Historian's Datasets). Opening a plant shows its
// page: a header with status and Stop / Start / Remove, and three read-only
// sections, Summary, Time series and Tables. The 5-second poll updates the list
// and the header in place; it never redraws a section, so a search, a page of
// rows or an open Remove dialog stays as it is.

let pendingRemove = null;
let pendingRemoveDataset = false; // the Remove dialog's checkbox survives the poll
let plantsData = null;            // null until the first answer
var browse = {      // var: writeHash may run before this line is reached
  plant: null, section: 'summary', table: null,
  summary: null, tables: null, rows: null,
  grid: { sort: 'row_version', dir: 'desc', q: '', deleted: false, page: 1 },
  seq: 0                           // drops answers that arrive after the user moved on
};
const MAX_PAGE = 501, PAGE_SIZE = 200;

async function loadPlants() {
  try {
    const data = await api('GET', '/api/plants');
    $('plants-error').hidden = true;
    $('plants-dataset').textContent = data.datasetPurgeAgeDays ? `Each one writes its tags to its own Historian Dataset, named after the plant, with a ${data.datasetPurgeAgeDays}-day purge age.` : '';
    plantsData = data.plants;
    if (browse.plant) renderDetailHead(); else renderPlants();
  } catch (e) {
    $('plants-error').hidden = false;
    $('plants-error').textContent = e.message;
  }
}

function renderPlants() {
  if (!plantsData) return;
  const filter = $('plant-filter').value.trim().toLowerCase();
  const shown = plantsData.filter(p => !filter || p.simulation.toLowerCase().includes(filter));
  $('plants').innerHTML = shown.length ? shown.map(plantRow).join('')
    : `<p class="list-empty">${plantsData.length ? 'No plant matches the filter.' : 'No plants yet. Click New plant at the top to design one.'}</p>`;
}
$('plant-filter').addEventListener('input', renderPlants);

// The status a person sees: "starting" is the first moment of the backfill, so a
// newly approved plant reads Backfilling (yellow) until it has caught up to now,
// then Live (green).
const STATE_LABEL = { starting: 'Backfilling', backfilling: 'Backfilling', live: 'Live', stopped: 'Stopped', error: 'Error', running_elsewhere: 'Running elsewhere', not_hosted: 'Not hosted' };
const shownState = state => state === 'starting' ? 'backfilling' : state;
const stateLabel = state => STATE_LABEL[state] || String(state).replace('_', ' ');

const metric = (label, value) => `<span class="metric"><span class="metric-label">${label}</span><span class="metric-value">${value}</span></span>`;

function plantRow(p) {
  const h = p.hosting, name = esc(p.simulation);
  return `<div class="list-row">
    <button type="button" class="row-head" data-open="${name}" title="Open ${name}">
      <svg class="icon chevron" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 18 6-6-6-6"/></svg>
      <span class="dot ${esc(shownState(h.state))}" title="${esc(stateLabel(h.state))}"></span>
      <span class="row-name">${name}</span>
      <span class="metrics">${metric('Status', esc(stateLabel(h.state)))}${metric('Delivered through', esc(fmtTime(h.throughUtc || p.tagCursorUtc)))}${metric('Rows', num(h.rowsApplied || null))}${metric('Tag points', num(h.pointsPublished || null))}</span>
    </button>
  </div>`;
}
$('plants').addEventListener('click', e => {
  const row = e.target.closest('[data-open]');
  if (row) openPlant(row.dataset.open);
});

function openPlant(name, section = 'summary', table = null) {
  browse.plant = name; browse.table = table;
  browse.summary = browse.tables = browse.rows = null;
  browse.grid = { sort: 'row_version', dir: 'desc', q: '', deleted: false, page: 1 };
  $('grid-search').value = ''; $('grid-deleted').checked = false;
  pendingRemove = null; pendingRemoveDataset = false;
  $('plants-overview').hidden = true;
  $('plant-detail').hidden = false;
  window.scrollTo({ top: 0 });
  renderDetailHead();
  selectSection(['summary', 'series', 'tables'].includes(section) ? section : 'summary');
}
function closePlant() {
  browse.plant = null; pendingRemove = null;
  $('plant-detail').hidden = true;
  $('plants-overview').hidden = false;
  renderPlants();
  writeHash();
}
$('plant-back').addEventListener('click', closePlant);

function detailPlant() { return plantsData?.find(p => p.simulation === browse.plant) || null; }

function renderDetailHead() {
  const name = browse.plant, p = detailPlant();
  $('detail-name').textContent = name;
  $('detail-crumb').textContent = name;
  if (!p) {
    $('detail-badge').hidden = true;
    $('detail-sub').textContent = plantsData ? "That plant is not in this page's list. Pick one from Plants." : 'Loading…';
    $('detail-actions').innerHTML = ''; $('detail-confirm').innerHTML = '';
    return;
  }
  const h = p.hosting, running = ['starting', 'backfilling', 'live', 'error'].includes(h.state), safe = esc(name);
  $('detail-badge').hidden = false;
  $('detail-badge').className = 'badge ' + shownState(h.state);
  $('detail-badge').textContent = stateLabel(h.state);
  $('detail-sub').innerHTML = [
    p.dataset ? `Dataset <code>${esc(p.dataset)}</code>` : '',
    `tables <code>${esc(p.tablePrefix)}…</code>`,
    `delivered through ${esc(fmtTime(h.throughUtc || p.tagCursorUtc))}`,
    h.message || h.code ? `${h.code ? `<code>${esc(h.code)}</code> ` : ''}${esc(h.message || '')}` : ''
  ].filter(Boolean).join(' · ');
  $('detail-actions').innerHTML =
    (running ? `<button type="button" data-act="stop">Stop</button>` : h.state === 'running_elsewhere' ? '' : `<button type="button" data-act="start">Start</button>`) +
    `<button type="button" class="danger" data-act="remove">Remove…</button>`;
  if (pendingRemove !== name) { $('detail-confirm').innerHTML = ''; return; }
  if ($('detail-confirm').firstElementChild) return; // already open: leave the dialog alone
  const keeps = p.datasetCreated ? 'Its Dataset and tags stay unless you tick the box below.' : 'Tags already in Historian stay.';
  const datasetChoice = p.datasetCreated
    ? `<label class="remove-dataset"><input type="checkbox" id="remove-dataset"${pendingRemoveDataset ? ' checked' : ''}><span>Also delete its Dataset <code>${esc(p.dataset)}</code> and every tag in it. This cannot be undone.</span></label>`
    : '';
  $('detail-confirm').innerHTML = `<div class="remove-confirm"><b>Remove ${safe}?</b><span>This stops the plant and drops its tables (<code>${esc(p.tablePrefix)}…</code>) and registration. Other plants are not touched. ${keeps}</span>${datasetChoice}<div class="row"><button type="button" class="danger solid" data-act="remove-yes">Remove ${safe}</button><button type="button" data-act="remove-no">Cancel</button></div></div>`;
}

$('plant-detail').addEventListener('change', e => {
  if (e.target.id === 'remove-dataset') pendingRemoveDataset = e.target.checked;
});
document.querySelector('.detail-head').addEventListener('click', async e => {
  const button = e.target.closest('button[data-act]');
  if (!button) return;
  const name = browse.plant, act = button.dataset.act;
  if (act === 'remove') { pendingRemove = name; pendingRemoveDataset = false; return renderDetailHead(); }
  if (act === 'remove-no') { pendingRemove = null; pendingRemoveDataset = false; return renderDetailHead(); }
  button.disabled = true;
  try {
    if (act === 'stop' || act === 'start') await api('POST', `/api/plants/${encodeURIComponent(name)}/${act}`);
    if (act === 'remove-yes') {
      await api('DELETE', `/api/plants/${encodeURIComponent(name)}?confirm=true${pendingRemoveDataset ? '&deleteDataset=true' : ''}`);
      pendingRemove = null; pendingRemoveDataset = false;
      closePlant();
    }
  } catch (err) {
    $('plants-error').hidden = false;
    $('plants-error').textContent = err.message + (err.code ? ` (${err.code})` : '');
  }
  await loadPlants();
});

// ----- sections -----

function selectSection(section) {
  browse.section = section;
  for (const name of ['summary', 'series', 'tables']) {
    $('sec-' + name).setAttribute('aria-selected', String(name === section));
    $('pane-' + name).hidden = name !== section;
  }
  writeHash();
  if (section === 'summary' && !browse.summary) loadSummary();
  if (section === 'tables' && !browse.tables) loadTables();
  if (section === 'series') renderSeriesPane();
}
document.querySelector('.section-tabs').addEventListener('click', e => {
  const tab = e.target.closest('[data-section]');
  if (tab) selectSection(tab.dataset.section);
});
document.querySelector('.section-tabs').addEventListener('keydown', e => {
  if (!['ArrowLeft', 'ArrowRight'].includes(e.key) || !e.target.matches('[data-section]')) return;
  const order = ['summary', 'series', 'tables'];
  const next = order[(order.indexOf(e.target.dataset.section) + (e.key === 'ArrowRight' ? 1 : 2)) % 3];
  selectSection(next); $('sec-' + next).focus();
});
$('detail-refresh').addEventListener('click', () => {
  if (browse.section === 'summary') loadSummary();
  if (browse.section === 'tables') { loadTables(); }
  if (browse.section === 'series') renderSeriesPane(true);
});

const browseUrl = (path = '') => `/api/browse/${encodeURIComponent(browse.plant)}${path}`;
const problem = e => `<p class="error">${esc(e.message)}${e.code ? ` <code>${esc(e.code)}</code>` : ''}</p>`;

// ----- Summary -----

async function loadSummary() {
  const seq = ++browse.seq, plant = browse.plant;
  if (!browse.summary) $('pane-summary').innerHTML = '<p class="muted">Loading…</p>';
  try {
    const summary = await api('GET', browseUrl());
    if (plant !== browse.plant || seq !== browse.seq) return;
    browse.summary = summary;
    renderSummary();
  } catch (e) {
    if (plant !== browse.plant) return;
    $('pane-summary').insertAdjacentHTML('afterbegin', problem(e));
  }
}

function renderSummary() {
  const s = browse.summary, base = `/api/plants/${encodeURIComponent(s.simulation)}`;
  const modules = Object.entries(s.modules).filter(([, on]) => on).map(([name]) => name);
  const lines = s.lines.map(l => `<li><b>${esc(l.name)}</b> <code>${esc(l.id)}</code> <span class="muted">${esc(l.kind)}</span>
      <ul>${l.equipment.map(e => `<li><code>${esc(e.id)}</code> ${esc(e.name)} <span class="muted">${esc(e.class)}</span></li>`).join('')}</ul></li>`).join('');
  const incidents = s.incidents.length
    ? `<div class="grid-wrap"><table class="grid compact"><thead><tr><th>When</th><th>Line</th><th>Machine</th><th>Failure mode</th><th>Note</th></tr></thead><tbody>${s.incidents.map(i =>
        `<tr><td class="mono">${esc(fmtTime(i.atUtc))}</td><td class="mono">${esc(i.line || '—')}</td><td class="mono">${esc(i.equipment || '—')}</td><td class="mono">${esc(i.failureMode)}</td><td>${esc(i.note)}</td></tr>`).join('')}</tbody></table></div>`
    : '<p class="muted">No planted incidents.</p>';
  const drifts = s.drifts.length
    ? `<p class="muted">Golden-batch drifts: ${s.drifts.map(d => `${esc(d.line)} ${esc(d.phase)} from ${esc(fmtTime(d.fromUtc))} (${esc(d.cause)})`).join('; ')}</p>` : '';
  $('pane-summary').innerHTML = `<div class="summary-grid">
    <section class="card"><div class="eyebrow">Run</div><dl class="kv">
      <dt>History starts</dt><dd>${esc(fmtTime(s.window.startUtc))}</dd>
      <dt>Approved ("now")</dt><dd>${esc(fmtTime(s.window.anchorUtc))}</dd>
      <dt>Tags delivered through</dt><dd>${esc(fmtTime(s.delivery.tagCursorUtc))}</dd>
      <dt>Last snapshot</dt><dd>${esc(fmtTime(s.delivery.snapshotUtc))}</dd>
      <dt>Changes applied</dt><dd>${num(s.delivery.appliedSequence)}</dd>
      <dt>Status</dt><dd><span class="badge ${esc(shownState(s.hosting.state))}">${esc(stateLabel(s.hosting.state))}</span> ${esc(s.hosting.message || '')}</dd>
    </dl></section>
    <section class="card"><div class="eyebrow">Where the data is</div><dl class="kv">
      <dt>Dataset</dt><dd>${s.dataset ? `<code>${esc(s.dataset)}</code>${s.datasetCreated ? ' <span class="muted">created by this page, 365-day purge</span>' : ''}` : '—'}</dd>
      <dt>Tag root</dt><dd><code>${esc(s.tagRoot)}</code></dd>
      <dt>Postgres tables</dt><dd><code>${esc(s.tablePrefix)}…</code> in <code>flow_data_sim</code></dd>
      <dt>Counts</dt><dd>${num(s.counts.tables)} tables · ${num(s.counts.tags)} tags</dd>
      <dt>Areas</dt><dd>${modules.length ? modules.map(esc).join(', ') : 'core only'}</dd>
    </dl></section>
    <section class="card"><div class="eyebrow">Lines and equipment</div><ul class="tree">${lines}</ul></section>
    <section class="card"><div class="eyebrow">Storylines and planted incidents</div>
      ${s.storylines.length ? `<ul class="plain">${s.storylines.map(t => `<li>${esc(t.title)} <code>${esc(t.id)}</code></li>`).join('')}</ul>` : '<p class="muted">No storylines recorded.</p>'}
      ${incidents}${drifts}
    </section>
  </div>
  <section class="card"><div class="eyebrow">Files</div><div class="downloads">${plantFiles(base)}</div></section>`;
}

// ----- Time series (Phase 4 fills this) -----

function renderSeriesPane() {
  $('pane-series').innerHTML = '<p class="muted">Trends are coming in this build.</p>';
}

// ----- Tables -----

async function loadTables() {
  const plant = browse.plant;
  $('table-list').innerHTML = '<li class="muted">Loading…</li>';
  try {
    const tables = await api('GET', browseUrl('/tables'));
    if (plant !== browse.plant) return;
    browse.tables = tables;
    if (!tables.some(t => t.name === browse.table)) browse.table = tables[0]?.name || null;
    renderTableList();
    writeHash();
    if (browse.table) loadRows();
  } catch (e) {
    if (plant !== browse.plant) return;
    $('table-list').innerHTML = `<li>${problem(e)}</li>`;
  }
}

function renderTableList() {
  $('table-list').innerHTML = browse.tables.map(t => `<li><button type="button" data-table="${esc(t.name)}"${t.name === browse.table ? ' aria-current="true"' : ''} title="${esc(t.description)}">
    <span class="table-purpose">${esc(t.purpose)}</span><span class="table-count">${num(t.rows)}${t.deletedRows ? ` <span class="muted">(${num(t.deletedRows)} deleted)</span>` : ''}</span></button></li>`).join('');
}
$('table-list').addEventListener('click', e => {
  const button = e.target.closest('[data-table]');
  if (!button || button.dataset.table === browse.table) return;
  browse.table = button.dataset.table;
  browse.grid = { sort: 'row_version', dir: 'desc', q: '', deleted: $('grid-deleted').checked, page: 1 };
  $('grid-search').value = '';
  renderTableList();
  writeHash();
  loadRows();
});

async function loadRows() {
  const seq = ++browse.seq, g = browse.grid;
  const query = new URLSearchParams({ sort: g.sort, dir: g.dir, page: String(g.page) });
  if (g.q) query.set('q', g.q);
  if (g.deleted) query.set('deleted', 'true');
  $('grid').classList.add('loading');
  try {
    const rows = await api('GET', browseUrl(`/tables/${encodeURIComponent(browse.table)}/rows?${query}`));
    if (seq !== browse.seq) return;
    browse.rows = rows;
    $('grid-error').hidden = true;
    renderGrid();
  } catch (e) {
    if (seq !== browse.seq) return;
    $('grid-error').hidden = false;                 // the last good page stays visible
    $('grid-error').innerHTML = `${esc(e.message)}${e.code ? ` <code>${esc(e.code)}</code>` : ''}`;
  } finally { if (seq === browse.seq) $('grid').classList.remove('loading'); }
}

const ISO_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/;
function cell(value) {
  if (value === null || value === undefined) return '<td class="null">null</td>';
  if (typeof value === 'number') return `<td class="num">${esc(value.toLocaleString(undefined, { maximumFractionDigits: 6 }))}</td>`;
  if (typeof value === 'boolean') return `<td class="mono">${value}</td>`;
  const text = String(value);
  if (ISO_TIME.test(text)) return `<td class="mono" title="${esc(text)}">${esc(text.slice(0, 19).replace('T', ' '))}</td>`;
  return `<td${text.endsWith('…') ? ` title="${esc(text)}"` : ''}>${esc(text)}</td>`;
}

function renderGrid() {
  const r = browse.rows, g = browse.grid;
  const deletedAt = r.columns.indexOf('is_deleted');
  $('grid').querySelector('thead').innerHTML = '<tr>' + r.columns.map(c => {
    const sorted = c === r.sort;
    return `<th aria-sort="${sorted ? (r.dir === 'asc' ? 'ascending' : 'descending') : 'none'}"><button type="button" data-sort="${esc(c)}">${esc(c)}${sorted ? `<span class="arrow" aria-hidden="true">${r.dir === 'asc' ? '▲' : '▼'}</span>` : ''}</button></th>`;
  }).join('') + '</tr>';
  $('grid').querySelector('tbody').innerHTML = r.rows.length
    ? r.rows.map(row => `<tr${deletedAt >= 0 && row[deletedAt] === true ? ' class="deleted"' : ''}>${row.map(cell).join('')}</tr>`).join('')
    : `<tr><td class="muted" colspan="${r.columns.length}">${g.q ? 'No rows match the search.' : 'No rows.'}</td></tr>`;
  const pages = Math.max(1, Math.min(MAX_PAGE, Math.ceil(r.total / PAGE_SIZE)));
  $('grid-count').textContent = `${num(r.total)} rows · page ${r.page} of ${num(pages)}`;
  $('grid-page').textContent = `Page ${r.page} of ${num(pages)}`;
  $('grid-prev').disabled = r.page <= 1;
  $('grid-next').disabled = r.page >= pages;
}
$('grid').addEventListener('click', e => {
  const th = e.target.closest('[data-sort]');
  if (!th) return;
  const g = browse.grid, column = th.dataset.sort;
  g.dir = g.sort === column && g.dir === 'desc' ? 'asc' : 'desc';
  g.sort = column; g.page = 1;
  loadRows();
});
$('grid-prev').addEventListener('click', () => { if (browse.grid.page > 1) { browse.grid.page--; loadRows(); } });
$('grid-next').addEventListener('click', () => { if (browse.grid.page < MAX_PAGE) { browse.grid.page++; loadRows(); } });
$('grid-deleted').addEventListener('change', e => { browse.grid.deleted = e.target.checked; browse.grid.page = 1; loadRows(); });
let searchTimer = null;
function search() { clearTimeout(searchTimer); browse.grid.q = $('grid-search').value.trim(); browse.grid.page = 1; loadRows(); }
$('grid-search').addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(search, 300); });
$('grid-search').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); search(); } });

// ---------------- start ----------------

// Where the page opens: a link to an interview opens that interview; otherwise
// the Plants view when this page has created plants, and the interview (step 1)
// when it has none yet.
const linkedInterview = /#interview=([0-9a-f-]{36})/.exec(location.hash)?.[1];
const hashValue = key => { const m = new RegExp(`[#&]${key}=([^&]*)`).exec(location.hash); return m ? decodeURIComponent(m[1]) : null; };
const linkedPlant = hashValue('plant');
// A link pasted or followed while the page is open (the page itself writes the
// URL with replaceState, which does not fire this).
window.addEventListener('hashchange', () => {
  const plant = hashValue('plant');
  if (!plant) return;
  if (currentTab !== 'plants') showTab('plants');
  if (plant !== browse.plant) return openPlant(plant, hashValue('section') || 'summary', hashValue('table'));
  const section = hashValue('section') || 'summary', table = hashValue('table');
  if (section === 'tables' && table && table !== browse.table && browse.tables?.some(t => t.name === table)) {
    browse.table = table; renderTableList();
    browse.grid = { sort: 'row_version', dir: 'desc', q: '', deleted: $('grid-deleted').checked, page: 1 };
    $('grid-search').value = '';
  }
  if (section !== browse.section) selectSection(section);
  else if (section === 'tables' && table === browse.table) loadRows();
});
interviewId = linkedInterview || store.get('flowsim.interview');
loadHealth(false);
loadInterview();
(async () => {
  if (linkedPlant) {
    showTab('plants');
    openPlant(linkedPlant, hashValue('section') || 'summary', hashValue('table'));
    return;
  }
  let open = 'interview';
  if (!linkedInterview) {
    try { if (((await api('GET', '/api/plants')).plants || []).some(p => p.managed)) open = 'plants'; }
    catch { /* open the interview; the Plants view reports its own errors */ }
  }
  showTab(open);
})();
