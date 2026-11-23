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
// A plant opened from the Plants list gets its own tab, "plant:<name>", like an
// item opened in the other Timebase apps; several can be open at once. Each plant
// tab remembers its section and table while it is open.
const PLANT_TAB = 'plant:';
const isPlantTab = t => t?.startsWith(PLANT_TAB);
const tabPlant = t => t.slice(PLANT_TAB.length);
const plantTabState = new Map();   // name → { section, table }
const openTabs = [];
const tabHistory = [];              // most recently shown last, for "close returns to the previous tab"
let currentTab = null;

function tabInfo(t) {
  return isPlantTab(t) ? { title: tabPlant(t), icon: VIEWS.plants.icon } : VIEWS[t];
}

function renderTabs() {
  $('open-tabs').innerHTML = openTabs.map(t => {
    const info = tabInfo(t), tab = esc(t), title = esc(info.title);
    return `<div class="tab${t === currentTab ? ' active' : ''}${isPlantTab(t) ? ' plant-tab' : ''}">
    <button type="button" class="tab-open" data-tab="${tab}"${t === currentTab ? ' aria-current="page"' : ''} title="${title}"><svg class="icon" viewBox="0 0 24 24" aria-hidden="true">${info.icon}</svg><span>${title}</span></button>
    ${openTabs.length > 1 ? `<button type="button" class="tab-close" data-close="${tab}" aria-label="Close ${title}"><svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18"/><path d="m6 6 12 12"/></svg></button>` : ''}</div>`;
  }).join('');
}
$('open-tabs').addEventListener('click', e => {
  const close = e.target.closest('[data-close]');
  if (close) return closeTab(close.dataset.close);
  const open = e.target.closest('[data-tab]');
  if (open) showTab(open.dataset.tab);
});

function closeTab(t) {
  const i = openTabs.indexOf(t);
  if (i < 0) return;
  openTabs.splice(i, 1);
  if (isPlantTab(t)) plantTabState.delete(tabPlant(t));
  for (let k = tabHistory.length - 1; k >= 0; k--) if (tabHistory[k] === t) tabHistory.splice(k, 1);
  if (t !== currentTab) return renderTabs();
  const previous = [...tabHistory].reverse().find(x => openTabs.includes(x)) || openTabs[Math.max(0, i - 1)] || 'plants';
  showTab(previous);
}

function showTab(name, section, table) {
  if (isPlantTab(currentTab) && browse?.plant) plantTabState.set(browse.plant, { section: browse.section, table: browse.table });
  if (!openTabs.includes(name)) openTabs.push(name);
  currentTab = name;
  tabHistory.push(name);
  if (tabHistory.length > 50) tabHistory.shift();
  const view = isPlantTab(name) ? 'plants' : name;
  for (const t of Object.keys(VIEWS)) {
    $('tab-' + t).setAttribute('aria-selected', String(t === view));
    $('view-' + t).hidden = t !== view;
  }
  if (view === 'plants') {
    if (isPlantTab(name)) {
      const saved = plantTabState.get(tabPlant(name)) || {};
      showPlantPage(tabPlant(name), section || saved.section || 'summary', table ?? saved.table ?? null);
    } else showPlantList();
  }
  renderTabs();
  writeHash();
  clearInterval(plantsTimer);
  if (view === 'plants') { loadPlants(); plantsTimer = setInterval(loadPlants, 5000); }
}
/** Opens a plant in its own tab, or focuses the tab it already has. */
function openPlant(name, section, table) { showTab(PLANT_TAB + name, section, table); }
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
  if (isPlantTab(currentTab) && browse?.plant) {
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
    if (approvedName) openPlant(approvedName); else showTab('plants');
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
// The essentials only: the two briefs that hand the plant to Gage (Connect
// first, then Atlas), and the demo guide to read. The prompts are on the page
// with a Copy button. The older spec and starter files are still served for
// scripts, but the page no longer offers them: the briefs contain what they held.
function gageLinks(base) {
  return downloadLink(`${base}/gage/connect`, '1 · Gage brief: Connect') + downloadLink(`${base}/gage/atlas`, '2 · Gage brief: Atlas');
}
function plantFiles(base) {
  return viewLink(`${base}/demo-guide?format=html`, 'Demo guide') + gageLinks(base);
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

function showPlantPage(name, section = 'summary', table = null) {
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
function showPlantList() {
  browse.plant = null; pendingRemove = null;
  $('plant-detail').hidden = true;
  $('plants-overview').hidden = false;
  renderPlants();
}

function detailPlant() { return plantsData?.find(p => p.simulation === browse.plant) || null; }

function renderDetailHead() {
  const name = browse.plant, p = detailPlant();
  $('detail-name').textContent = name;
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
      closeTab(PLANT_TAB + name);
      if (currentTab !== 'plants') showTab('plants');
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
    const [summary, overview] = await Promise.all([api('GET', browseUrl()), api('GET', browseUrl('/overview')).catch(e => ({ failed: e }))]);
    if (plant !== browse.plant || seq !== browse.seq) return;
    browse.summary = summary; browse.overview = overview;
    renderSummary();
  } catch (e) {
    if (plant !== browse.plant) return;
    $('pane-summary').insertAdjacentHTML('afterbegin', problem(e));
  }
  // Live values move: refresh every 15 s while this plant's Summary is showing.
  clearTimeout(browse.summaryTimer);
  browse.summaryTimer = setTimeout(() => { if (browse.plant === plant && browse.section === 'summary' && !$('pane-summary').hidden) loadSummary(); }, 15000);
}

// The Summary leads with what the plant is doing now: key numbers, then each
// line's process flow (product moving machine to machine while the line runs,
// paused otherwise), then the week as a timeline of stops and planted storyline
// moments, then the storylines and the records produced so far.
const SVGNS = 'http://www.w3.org/2000/svg';
const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)');
const lc = v => String(v ?? '').toLowerCase();
function unitState(value) {
  const v = lc(value);
  if (!v) return 'idle';
  if (/run|produc|ok/.test(v)) return 'run';
  if (/down|fault|fail|jam|trip|stop/.test(v)) return 'down';
  if (/change|setup|clean|cip|rinse|wash|starv|block/.test(v)) return 'change';
  return 'idle';
}
const SHORT_MEASURE = { Vibration: 'vib', FillVolume: 'fill', Temperature: 'temp', Pressure: 'press', Level: 'level', Speed: 'speed' };
function fmtValue(v) { return typeof v === 'number' ? (Math.abs(v) >= 1000 ? Math.round(v).toLocaleString() : v.toFixed(Math.abs(v) < 10 ? 2 : 1)) : String(v); }

function renderSummary() {
  const s = browse.summary, o = browse.overview && !browse.overview.failed ? browse.overview : null;
  const now = o?.now || {}, counts = o?.counts || {}, root = s.tagRoot;
  const val = tag => now[tag]?.[1];
  const base = `/api/plants/${encodeURIComponent(s.simulation)}`;
  const asOf = Date.parse(o?.asOfUtc || s.delivery.tagCursorUtc || s.window.anchorUtc);

  // Key numbers
  const lineStates = s.lines.map(l => ({ l, state: val(`${root}.${l.id}.State`), speed: val(`${root}.${l.id}.Speed`), sku: val(`${root}.${l.id}.SKU`), good: val(`${root}.${l.id}.GoodCount`) }));
  const good = lineStates.reduce((sum, x) => sum + (typeof x.good === 'number' ? x.good : 0), 0);
  const days = Math.max(1, Math.round((asOf - Date.parse(s.window.startUtc)) / 864e5));
  const kpi = (big, label, small = '') => `<div class="sum-kpi"><b>${big}</b><span>${label}</span>${small ? `<small>${small}</small>` : ''}</div>`;
  const kpis = [
    kpi(`${s.lines.length} line${s.lines.length === 1 ? '' : 's'}`, lineStates.map(x => `${esc(x.l.id)} ${esc(lc(x.state) || '—')}`).join(' · ')),
    o?.now ? kpi(num(good), 'good cases so far', lineStates.map(x => `${esc(x.l.id)} ${num(x.good)}`).join(' · ')) : '',
    counts.downtimeevents !== undefined ? kpi(num(counts.downtimeevents), 'downtime events', `${num(counts.maintenancetickets)} maintenance tickets`) : '',
    counts.qualityholds !== undefined ? kpi(num(counts.qualityholds), 'lots held by Quality', `of ${num(counts.lots)} lots`)
      : counts.salesorders !== undefined ? kpi(num(counts.salesorders), 'sales orders') : '',
    kpi(`${days} day${days === 1 ? '' : 's'}`, 'of history', `since ${esc(fmtTime(s.window.startUtc))}`)
  ].join('');

  // Planted incidents per machine (purple badge with their count)
  const planted = new Map();
  for (const i of s.incidents) if (i.equipment) planted.set(`${i.line}.${i.equipment}`, (planted.get(`${i.line}.${i.equipment}`) || 0) + 1);

  const flows = lineStates.map(x => flowSvg(s, x, now, planted)).join('');

  // Storylines and records
  const stories = s.storylines.length
    ? s.storylines.map((t, i) => `<div class="sum-story"><span class="n">${i + 1}</span><div><b>${esc(t.title)}</b>${t.pitch ? `<p>${esc(t.pitch)}</p>` : ''}</div></div>`).join('')
    : '<p class="muted">No storylines recorded.</p>';
  const masters = new Set(['lines', 'equipment', 'skus', 'shifts', 'customers', 'employees', 'certifications', 'parts', 'failurecodes', 'qualityspecs', 'recipes', 'recipephases', 'goldenprofiles', 'workinstructions']);
  const LABEL = { qualityresults: 'Quality results', inventorytransactions: 'Inventory moves', maintenanceticketstatus: 'Ticket status changes', salesorders: 'Sales orders',
    productionorders: 'Production orders', downtimeevents: 'Downtime events', productionruns: 'Production runs', lots: 'Lots', shipments: 'Shipments', maintenancetickets: 'Maintenance tickets',
    qualityholds: 'Quality holds', qualitysamples: 'Quality samples', partsusage: 'Parts used', batches: 'Batches', batchphases: 'Batch phases', deviations: 'Deviations', trainingrecords: 'Training records', workcompliance: 'Work compliance checks', operatorcompliance: 'Operator compliance checks', batchconsumption: 'Batch consumption' };
  const recs = Object.entries(counts).filter(([p, n]) => !masters.has(p) && n > 0).sort((a, b) => b[1] - a[1]).slice(0, 12);
  const max = Math.max(1, ...recs.map(r => r[1]));
  const records = recs.length ? recs.map(([p, n], i) => `<div class="sum-rec"><span>${esc(LABEL[p] || p)}</span><span class="bar"><i style="width:${(n / max * 100).toFixed(1)}%;animation-delay:${i * 50}ms"></i></span><b>${num(n)}</b></div>`).join('')
    : '<p class="muted">No records yet.</p>';
  const drifts = s.drifts.length ? `<p class="muted">Golden-batch drifts: ${s.drifts.map(d => `${esc(d.line)} ${esc(d.phase)} from ${esc(fmtTime(d.fromUtc))} (${esc(d.cause)})`).join('; ')}</p>` : '';
  const live = o?.failed ? problem(o.failed) : o?.nowProblem ? `<p class="muted">${esc(o.nowProblem)}</p>` : '';

  $('pane-summary').innerHTML = `
    <p class="sum-intro">${plantStory(s, o, lineStates, good, days)}</p>
    <div class="sum-kpis">${kpis}</div>${live}
    <section class="sum-section"><div class="sum-title"><h2>Process flow, now</h2><span>Product moves left to right while a line runs. A purple badge counts a machine's planted storyline incidents.</span></div>${flows}</section>
    <section class="sum-section"><div class="sum-title"><h2>The week, by line</h2><span>Red: downtime · amber: planned stops · purple: planted storyline moments · blue: now. Hover a marker for details.</span></div>${timeline(s, o, asOf)}</section>
    <div class="sum-split">
      <section><div class="sum-title"><h2>What the demo shows</h2><span>${s.storylines.length} storyline${s.storylines.length === 1 ? '' : 's'} · ${s.incidents.length} planted incident${s.incidents.length === 1 ? '' : 's'}</span></div>${stories}${drifts}</section>
      <section><div class="sum-title"><h2>Records so far</h2><span><code>${esc(s.tablePrefix)}*</code> in flow_data_sim</span></div><div class="sum-records">${records}</div></section>
    </div>
    <div class="sum-split sum-foot">
      <dl class="kv"><dt>Tags</dt><dd><code>${esc(root)}.*</code> · ${num(s.counts.tags)} in Dataset ${s.dataset ? `<code>${esc(s.dataset)}</code>` : '—'}${s.datasetCreated ? ' (365-day purge)' : ''}</dd>
        <dt>Tables</dt><dd><code>${esc(s.tablePrefix)}*</code> · ${num(s.counts.tables)} in <code>flow_data_sim</code></dd>
        <dt>Delivery</dt><dd>${num(s.delivery.appliedSequence)} changes applied · tags through ${esc(fmtTime(s.delivery.tagCursorUtc))} · snapshot ${esc(fmtTime(s.delivery.snapshotUtc))}</dd></dl>
      <div class="downloads">${plantFiles(base)}</div>
    </div>`;
  startFlows();
}

// One paragraph at the top: what the plant is, and its life so far.
const INDUSTRY = { 'food-beverage': 'food and beverage', cpg: 'consumer goods', pharma: 'pharmaceutical', chemicals: 'chemicals' };
function plantStory(s, o, lineStates, good, days) {
  const counts = o?.counts || {}, machines = s.lines.reduce((n, l) => n + l.equipment.length, 0);
  const industry = INDUSTRY[s.audience?.industry] || (s.audience?.industry ? s.audience.industry.replace('-', ' ') : '');
  const list = items => items.length < 2 ? items.join('') : items.slice(0, -1).join(', ') + ' and ' + items[items.length - 1];
  const sentences = [];
  const products = s.products || [];
  sentences.push(`<b>${esc(s.simulation)}</b> is a simulated ${esc(industry || 'manufacturing')} plant with ${s.lines.length} line${s.lines.length === 1 ? '' : 's'} (${list(s.lines.map(l => esc(l.name)))}) and ${machines} machines` +
    (products.length ? `, making ${products.length} product${products.length === 1 ? '' : 's'}: ${products.map(p => esc(p.description)).join('; ')}.` : '.'));
  const unplanned = (o?.downtime || []).filter(d => lc(d.category) !== 'planned').length;
  const life = [];
  if (o?.now && good) life.push(`${num(good)} good cases`);
  if (counts.salesorders !== undefined) life.push(`${num(counts.salesorders)} sales orders`);
  if (counts.downtimeevents !== undefined) life.push(`${num(counts.downtimeevents)} downtime events (${num(unplanned)} unplanned)`);
  if (counts.maintenancetickets !== undefined) life.push(`${num(counts.maintenancetickets)} maintenance tickets`);
  if (counts.qualityholds !== undefined) life.push(`${num(counts.qualityholds)} of ${num(counts.lots)} lots held by Quality`);
  sentences.push(`It has run for ${days} day${days === 1 ? '' : 's'}, since ${esc(fmtTime(s.window.startUtc))}` + (life.length ? `, with ${list(life)} so far.` : '.'));
  if (s.storylines.length) {
    const last = s.incidents.map(i => i.atUtc).sort().pop();
    sentences.push(`Its story: ${list(s.storylines.map(t => esc(lc(t.title))))}` +
      (s.incidents.length ? `, told through ${s.incidents.length} planted moment${s.incidents.length === 1 ? '' : 's'}${last ? `, the last on ${esc(fmtTime(last))}` : ''}.` : '.'));
  }
  const state = lineStates.filter(x => x.state !== undefined);
  if (state.length) {
    const running = state.filter(x => unitState(x.state) === 'run');
    sentences.push(running.length === state.length
      ? `Right now ${state.length === 1 ? 'the line is' : `all ${state.length} lines are`} running${running.every(x => typeof x.speed === 'number') ? `, at ${list(running.map(x => `${fmtValue(x.speed)}`))} cases/min` : ''}.`
      : `Right now ${list(state.map(x => `${esc(x.l.name)} is ${/change|setup|clean|cip/.test(lc(x.state)) ? 'in ' : ''}${esc(lc(x.state))}`))}.`);
  }
  return sentences.join(' ');
}

function flowSvg(s, x, now, planted) {
  const root = s.tagRoot, l = x.l, eq = l.equipment;
  const W = 1100, top = 40, h = 64, gap = 26, x0 = 0;
  const unitW = Math.min(180, (W - gap * (eq.length - 1)) / Math.max(1, eq.length));
  const running = unitState(x.state) === 'run';
  const y = top + h / 2;
  const lastX = x0 + (eq.length - 1) * (unitW + gap) + unitW / 2;
  const units = eq.map((e, i) => {
    const ux = x0 + i * (unitW + gap);
    const status = now[`${root}.${l.id}.${e.id}.Status`]?.[1];
    const st = unitState(status ?? (running ? 'running' : x.state));
    const numeric = Object.keys(now).filter(k => k.startsWith(`${root}.${l.id}.${e.id}.`) && typeof now[k][1] === 'number')
      .sort((a, b) => (a.endsWith('Vibration') ? -1 : 0) - (b.endsWith('Vibration') ? -1 : 0))[0];
    const measure = numeric ? numeric.split('.').pop() : null;
    const detail = numeric ? `${SHORT_MEASURE[measure] || lc(measure)} ${fmtValue(now[numeric][1])}` : lc(status || '');
    const count = planted.get(`${l.id}.${e.id}`) || 0;
    return `<g class="unit ${st}${count ? ' alert' : ''}" transform="translate(${ux},${top})"><title>${esc(e.name)}: ${esc(status || x.state || 'no value yet')}${count ? ` · ${count} planted incident${count > 1 ? 's' : ''}` : ''}</title>
      <rect width="${unitW}" height="${h}" rx="12"/><circle class="led ${st}" cx="${unitW - 14}" cy="14" r="4.5"/>
      <text class="id" x="12" y="20">${esc(e.id)}</text><text class="nm" x="12" y="38">${esc(trimText(e.name, unitW))}</text><text class="val" x="12" y="54">${esc(trimText(detail, unitW))}</text>
      ${count ? `<g transform="translate(-6,-6)"><circle class="badge" r="10"/><text class="badge-t" text-anchor="middle" y="3.5">${count}</text></g>` : ''}</g>`;
  }).join('');
  const path = `M${x0 + unitW / 2} ${y} H${lastX}`;
  const meta = [lc(x.state) || 'no state yet', typeof x.speed === 'number' ? `${fmtValue(x.speed)} cases/min` : null, x.sku].filter(Boolean).map(esc).join(' · ');
  return `<svg class="sum-flow" viewBox="-10 0 ${W + 20} ${top + h + (running ? 12 : 30)}" role="img" aria-label="${esc(l.name)} process flow: ${meta}">
    <text class="line-label" x="0" y="20">${esc(l.id)} · ${esc(l.name)}</text><text class="line-meta" x="${Math.max(110, (l.id.length + l.name.length) * 8 + 30)}" y="20">${meta}</text>
    <path class="pipe${running ? ' on' : ''}" d="${path}"/>${running ? `<path class="stream" d="${path}"/><g class="bottles" data-path="${path}" data-span="${lastX - unitW / 2}"></g>` : ''}
    ${units}
    ${running ? '' : `<text class="paused" x="${(x0 + lastX) / 2}" y="${top + h + 22}" text-anchor="middle">Flow paused: ${esc(lc(x.state) || 'no state yet')}</text>`}</svg>`;
}
function trimText(text, width) { const max = Math.floor((width - 22) / 7.2); return text.length > max ? text.slice(0, max - 1) + '…' : text; }
function startFlows() {
  if (reduceMotion.matches) return;
  document.querySelectorAll('#pane-summary .bottles').forEach(g => {
    const count = Math.max(4, Math.round(Number(g.dataset.span) / 110)), dur = Math.max(5, Number(g.dataset.span) / 110);
    for (let i = 0; i < count; i++) {
      const b = document.createElementNS(SVGNS, 'rect');
      b.setAttribute('class', 'bottle'); b.setAttribute('width', 7); b.setAttribute('height', 12); b.setAttribute('x', -3.5); b.setAttribute('y', -6); b.setAttribute('rx', 2);
      const m = document.createElementNS(SVGNS, 'animateMotion');
      m.setAttribute('dur', dur + 's'); m.setAttribute('repeatCount', 'indefinite'); m.setAttribute('path', g.dataset.path); m.setAttribute('begin', (-i * dur / count) + 's');
      b.appendChild(m); g.appendChild(b);
    }
  });
}

function timeline(s, o, asOf) {
  const t0 = Date.parse(s.window.startUtc), t1 = Math.max(asOf, t0 + 3600e3);
  const pos = ms => Math.min(100, Math.max(0, (ms - t0) / (t1 - t0) * 100)).toFixed(2) + '%';
  const ticks = [];
  for (let d = new Date(t0); d.getTime() <= t1; d.setUTCDate(d.getUTCDate() + 1)) {
    const midnight = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + 1);
    if (midnight < t1) ticks.push(`<span style="left:${pos(midnight)}">${new Date(midnight).toLocaleDateString('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' })}</span>`);
  }
  const marker = (ms, cls, label) => `<span class="ev ${cls}" tabindex="0" style="left:${pos(ms)}" aria-label="${esc(label)}"></span><span class="tip" style="left:${pos(ms)}">${esc(label)}</span>`;
  const rows = s.lines.map(l => {
    const stops = (o?.downtime || []).filter(d => d.line === l.id).map(d => {
      const planned = lc(d.category) === 'planned';   // the category is Planned or Unplanned
      return marker(Date.parse(d.startUtc), planned ? 'planned' : 'down', `${d.equipment || l.id} · ${d.reason || d.category} · ${fmtTime(d.startUtc)}`);
    }).join('');
    const story = s.incidents.filter(i => i.line === l.id).map(i => marker(Date.parse(i.atUtc), 'story', `${i.equipment} · ${i.note} · ${fmtTime(i.atUtc)}`)).join('');
    return `<div class="sum-tl-row"><span class="name">${esc(l.id)} · ${esc(l.name)}</span><div class="lane">${stops}${story}<span class="now" style="left:${pos(asOf)}"></span></div></div>`;
  }).join('');
  return `<div class="sum-tl"><div class="sum-tl-row axis"><span></span><div class="ticks">${ticks.join('')}</div></div>${rows}</div>`;
}

// ----- Time series -----
// One start/end for the whole plant (in UTC, like every time the simulator
// writes), and any number of trend windows that all follow it. Each window
// shows up to 4 of the plant's tags, read from its own Dataset and thinned by
// the server to at most 1,000 points per tag. The windows and their tags are
// remembered per plant in this browser; the range is not.

const TREND = { maxTags: 4, maxDays: 31, colors: ['--tb-blue-500', '--tb-green-500', '--tb-orange-500', '--tb-red-500'] };
const series = { plant: null, tree: null, tags: new Map(), range: null, windows: [], nextId: 1, error: null };

const trendKey = plant => 'flowsim.trends.' + plant;
function saveTrends() { store.set(trendKey(series.plant), JSON.stringify({ windows: series.windows.map(w => ({ tags: w.tags })) })); }
function utcInput(ms) { return new Date(ms).toISOString().slice(0, 16); }
function parseUtcInput(value) { const ms = Date.parse(value + ':00Z'); return Number.isNaN(ms) ? null : ms; }
const shortTag = name => name.split('.').slice(1).join('.') || name;

async function renderSeriesPane(reload = false) {
  if (series.plant !== browse.plant || reload || !series.tree) {
    const plant = browse.plant;
    $('pane-series').innerHTML = '<p class="muted">Loading…</p>';
    try {
      const tree = await api('GET', browseUrl('/series'));
      if (plant !== browse.plant) return;
      const fresh = series.plant !== plant;
      series.plant = plant; series.tree = tree; series.tags = new Map();
      for (const line of tree.lines) {
        line.tags.forEach(t => series.tags.set(t.name, { ...t, group: line.name }));
        line.equipment.forEach(e => e.tags.forEach(t => series.tags.set(t.name, { ...t, group: `${line.name} · ${e.id}` })));
      }
      tree.plantTags.forEach(t => series.tags.set(t.name, { ...t, group: 'Plant' }));
      const minMs = Date.parse(tree.range.minUtc), maxMs = Date.parse(tree.range.maxUtc);
      if (fresh || !series.range) {
        series.range = { start: Math.max(minMs, maxMs - 24 * 3600e3), end: maxMs };
        let saved = null;
        try { saved = JSON.parse(store.get(trendKey(plant)) || 'null'); } catch { saved = null; }
        series.windows = (saved?.windows || []).map(w => ({ id: series.nextId++, tags: (w.tags || []).filter(t => series.tags.has(t)).slice(0, TREND.maxTags) }));
        if (!series.windows.length) {
          const first = tree.lines[0];
          const numbers = first ? [...first.tags, ...first.equipment.flatMap(e => e.tags)].filter(t => t.valueType === 'number') : [];
          series.windows = [{ id: series.nextId++, tags: numbers.slice(0, 2).map(t => t.name) }];
        }
      }
    } catch (e) {
      if (plant === browse.plant) $('pane-series').innerHTML = problem(e);
      return;
    }
  }
  buildSeriesPane();
  drawAllTrends();
}

function buildSeriesPane() {
  const r = series.range;
  $('pane-series').innerHTML = `<div class="timebar">
      <label><span>Start <span class="utc">UTC</span></span><input type="datetime-local" id="tr-start" step="60" value="${utcInput(r.start)}"></label>
      <label><span>End <span class="utc">UTC</span></span><input type="datetime-local" id="tr-end" step="60" value="${utcInput(r.end)}"></label>
      <button type="button" id="tr-apply">Apply</button>
      <div class="quick" role="group" aria-label="Quick ranges">${[['1h', 1], ['8h', 8], ['24h', 24], ['7d', 168]].map(([label, h]) => `<button type="button" data-hours="${h}">Last ${label}</button>`).join('')}<button type="button" data-hours="all">Whole run</button></div>
      <button type="button" class="primary" id="tr-add"><svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12h14"/><path d="M12 5v14"/></svg><span>Add trend</span></button>
    </div>
    <p class="error" id="tr-range-error" hidden></p>
    <p class="muted tr-note">Data from ${esc(fmtTime(series.tree.range.minUtc))} to ${esc(fmtTime(series.tree.range.maxUtc))} in Dataset <code>${esc(series.tree.dataset || '—')}</code>. Up to ${TREND.maxTags} tags per trend and ${TREND.maxDays} days at a time.</p>
    <div class="trends" id="trend-windows">${series.windows.map(windowShell).join('')}</div>`;
}

function windowShell(w, index) {
  return `<section class="card trend" data-window="${w.id}">
    <div class="eyebrow trend-head"><span class="trend-title">Trend ${index + 1}</span><span class="chips" data-chips></span>
      <button type="button" class="ghost small" data-act="pick" aria-expanded="false">Tags…</button>
      <button type="button" class="ghost small icon-only" data-act="close-window" aria-label="Remove Trend ${index + 1}" title="Remove this trend"><svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M18 6 6 18"/><path d="m6 6 12 12"/></svg></button>
    </div>
    <div class="picker" data-picker hidden></div>
    <div class="trend-body" data-body><p class="muted">Loading…</p></div>
  </section>`;
}

function rangeProblem(start, end) {
  const min = Date.parse(series.tree.range.minUtc), latest = Date.now() + 5 * 60e3;
  if (start == null || end == null) return 'Enter a start and an end.';
  if (start >= end) return 'The start must be before the end.';
  if (end - start > TREND.maxDays * 86400e3) return `Choose at most ${TREND.maxDays} days at a time.`;
  if (start < min) return `The plant's data starts at ${fmtTime(series.tree.range.minUtc)}.`;
  if (end > latest) return 'The end cannot be in the future.';
  return null;
}

function setRange(start, end) {
  const why = rangeProblem(start, end);
  $('tr-range-error').hidden = !why;
  $('tr-range-error').textContent = why || '';
  if (why) return;
  series.range = { start, end };
  $('tr-start').value = utcInput(start); $('tr-end').value = utcInput(end);
  drawAllTrends();
}

$('pane-series').addEventListener('click', e => {
  const target = e.target.closest('button');
  if (!target) return;
  if (target.id === 'tr-apply') return setRange(parseUtcInput($('tr-start').value), parseUtcInput($('tr-end').value));
  if (target.dataset.hours) {
    const max = Date.parse(series.tree.range.maxUtc), min = Date.parse(series.tree.range.minUtc);
    const hours = target.dataset.hours === 'all' ? (max - min) / 3600e3 : Number(target.dataset.hours);
    return setRange(Math.max(min, max - Math.min(hours, TREND.maxDays * 24) * 3600e3), max);
  }
  if (target.id === 'tr-add') {
    series.windows.push({ id: series.nextId++, tags: [] });
    saveTrends(); buildSeriesPane(); drawAllTrends();
    const last = $('trend-windows').lastElementChild;
    openPicker(last);
    return;
  }
  const card = target.closest('[data-window]');
  if (!card) return;
  const w = series.windows.find(x => x.id === Number(card.dataset.window));
  if (target.dataset.act === 'close-window') {
    series.windows = series.windows.filter(x => x !== w);
    saveTrends(); buildSeriesPane(); drawAllTrends();
  } else if (target.dataset.act === 'pick') {
    card.querySelector('[data-picker]').hidden ? openPicker(card) : closePicker(card);
  } else if (target.dataset.act === 'remove-tag') {
    w.tags = w.tags.filter(t => t !== target.dataset.tag);
    saveTrends(); drawTrend(w);
  } else if (target.dataset.act === 'picker-done') {
    closePicker(card);
  }
});
$('pane-series').addEventListener('keydown', e => {
  if (e.key === 'Enter' && (e.target.id === 'tr-start' || e.target.id === 'tr-end')) $('tr-apply').click();
  if (e.key === 'Escape') { const card = e.target.closest('[data-window]'); if (card) closePicker(card); }
});

function openPicker(card) {
  const w = series.windows.find(x => x.id === Number(card.dataset.window));
  const picker = card.querySelector('[data-picker]');
  const tagRow = t => `<label class="pick"><input type="checkbox" value="${esc(t.name)}"${w.tags.includes(t.name) ? ' checked' : ''}><span>${esc(shortTag(t.name))}</span><span class="muted">${esc(t.measure)}${t.valueType === 'string' ? ' · text' : ''}</span></label>`;
  const groups = series.tree.lines.map(l => `<div class="pick-group"><div class="pick-title">${esc(l.name)} <code>${esc(l.id)}</code></div>${l.tags.map(tagRow).join('')}
      ${l.equipment.map(e => `<div class="pick-sub">${esc(e.id)} ${esc(e.name)}</div>${e.tags.map(tagRow).join('')}`).join('')}</div>`).join('')
    + (series.tree.plantTags.length ? `<div class="pick-group"><div class="pick-title">Plant</div>${series.tree.plantTags.map(tagRow).join('')}</div>` : '');
  picker.innerHTML = `<div class="picker-top"><label class="filter"><svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/></svg><input type="search" data-pick-filter placeholder="Filter tags…" aria-label="Filter tags"></label>
      <span class="muted" data-pick-count></span><button type="button" class="primary small" data-act="picker-done">Done</button></div>
    <div class="pick-list">${groups}</div>`;
  picker.hidden = false;
  card.querySelector('[data-act="pick"]').setAttribute('aria-expanded', 'true');
  updatePickLimit(picker);
  picker.querySelector('[data-pick-filter]').focus();
}
function updatePickLimit(picker) {
  const boxes = [...picker.querySelectorAll('input[type=checkbox]')];
  const chosen = boxes.filter(b => b.checked).length;
  boxes.forEach(b => { b.disabled = !b.checked && chosen >= TREND.maxTags; b.closest('label').title = b.disabled ? `Up to ${TREND.maxTags} tags per trend` : ''; });
  picker.querySelector('[data-pick-count]').textContent = chosen >= TREND.maxTags ? `Up to ${TREND.maxTags} tags per trend` : `${chosen} of ${TREND.maxTags}`;
}
function closePicker(card) {
  const picker = card.querySelector('[data-picker]');
  if (picker.hidden) return;
  const w = series.windows.find(x => x.id === Number(card.dataset.window));
  const chosen = [...picker.querySelectorAll('input[type=checkbox]:checked')].map(b => b.value);
  picker.hidden = true; picker.innerHTML = '';
  card.querySelector('[data-act="pick"]').setAttribute('aria-expanded', 'false');
  // keep the order tags were already in, then the new ones
  w.tags = [...w.tags.filter(t => chosen.includes(t)), ...chosen.filter(t => !w.tags.includes(t))].slice(0, TREND.maxTags);
  saveTrends(); drawTrend(w);
}
$('pane-series').addEventListener('change', e => {
  if (e.target.matches('.picker input[type=checkbox]')) updatePickLimit(e.target.closest('.picker'));
});
$('pane-series').addEventListener('input', e => {
  if (!e.target.matches('[data-pick-filter]')) return;
  const q = e.target.value.trim().toLowerCase();
  e.target.closest('.picker').querySelectorAll('label.pick').forEach(l => { l.hidden = q && !l.textContent.toLowerCase().includes(q); });
});

function drawAllTrends() { series.windows.forEach(drawTrend); }

async function drawTrend(w) {
  const card = document.querySelector(`[data-window="${w.id}"]`);
  if (!card) return;
  const body = card.querySelector('[data-body]');
  const colorOf = i => getComputedStyle(document.documentElement).getPropertyValue(TREND.colors[i]).trim() || '#31aee4';
  renderChips(card, w, null);
  if (!w.tags.length) { body.innerHTML = '<p class="muted">No tags yet. Click Tags… to pick up to 4.</p>'; return; }
  const seq = w.seq = (w.seq || 0) + 1;
  const { start, end } = series.range;
  const query = w.tags.map(t => 'tag=' + encodeURIComponent(t)).join('&') + `&start=${encodeURIComponent(new Date(start).toISOString())}&end=${encodeURIComponent(new Date(end).toISOString())}`;
  body.classList.add('loading');
  try {
    const data = await api('GET', browseUrl('/trend?' + query));
    if (seq !== w.seq) return;
    w.data = data;
    renderChips(card, w, data);
    body.innerHTML = '';
    body.appendChild(chart(data, colorOf));
  } catch (e) {
    if (seq !== w.seq) return;
    body.innerHTML = problem(e);
  } finally { if (seq === w.seq) body.classList.remove('loading'); }
}

function renderChips(card, w, data) {
  const color = i => `var(${TREND.colors[i]})`;
  card.querySelector('[data-chips]').innerHTML = w.tags.map((t, i) => {
    const d = data?.tags.find(x => x.name === t);
    const note = !d ? '' : d.points.length === 0 ? ' · No values in this range' : d.thinned ? ` · Thinned from ${num(d.rawPoints)} points` : '';
    const info = series.tags.get(t);
    return `<span class="tag-chip" title="${esc(t)}${info ? ` (${esc(info.measure)})` : ''}"><span class="swatch" style="background:${color(i)}"></span>${esc(shortTag(t))}<span class="muted">${esc(note)}</span>
      <button type="button" data-act="remove-tag" data-tag="${esc(t)}" aria-label="Remove ${esc(shortTag(t))}">×</button></span>`;
  }).join('');
}

const STATE_COLORS = { Running: '--run', Run: '--run', Down: '--down', Fault: '--down', Changeover: '--change', Idle: '--idle', Starved: '--idle', Blocked: '--minor', Maintenance: '--maint', Cleaning: '--maint', CIP: '--maint' };
const BAND_PALETTE = ['--ph1', '--ph2', '--ph3', '--ph4', '--ph5', '--ph6'];
function bandColor(value) {
  const css = v => getComputedStyle(document.documentElement).getPropertyValue(v).trim();
  if (STATE_COLORS[value]) return css(STATE_COLORS[value]);
  let h = 0; for (const c of String(value)) h = (h * 31 + c.charCodeAt(0)) >>> 0;
  return css(BAND_PALETTE[h % BAND_PALETTE.length]);
}

function niceTimeStep(spanMs) {
  const steps = [60e3, 5 * 60e3, 15 * 60e3, 30 * 60e3, 3600e3, 2 * 3600e3, 3 * 3600e3, 6 * 3600e3, 12 * 3600e3, 86400e3, 2 * 86400e3, 7 * 86400e3];
  return steps.find(s => spanMs / s <= 6) || steps[steps.length - 1];
}

function chart(data, colorOf) {
  const NS = 'http://www.w3.org/2000/svg';
  const start = Date.parse(data.start), end = Date.parse(data.end);
  const dataEnd = Math.min(end, Date.parse(series.tree.range.maxUtc));
  const numbers = data.tags.map((t, i) => ({ ...t, i })).filter(t => t.valueType === 'number');
  const texts = data.tags.map((t, i) => ({ ...t, i })).filter(t => t.valueType !== 'number');
  const measures = [...new Set(numbers.map(t => t.measure))];
  const axes = measures.slice(0, 2);
  const width = Math.max(320, (document.querySelector('#trend-windows')?.clientWidth || 800) - 34);
  const laneH = 14, lanes = texts.length;
  const m = { l: axes.length ? 54 : 12, r: axes.length > 1 ? 54 : 12, t: axes.length ? 22 : 10, b: 22 };
  const plotH = numbers.length ? 230 - m.t - m.b : 0;
  const height = m.t + plotH + (lanes ? lanes * (laneH + 4) + 4 : 0) + m.b;
  const x = ms => m.l + (ms - start) / (end - start) * (width - m.l - m.r);
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
  svg.setAttribute('width', '100%');
  svg.setAttribute('class', 'trend-svg');
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', `Trend of ${data.tags.map(t => shortTag(t.name)).join(', ')} from ${fmtTime(data.start)} to ${fmtTime(data.end)}`);
  const el = (name, attrs, text) => { const node = document.createElementNS(NS, name); for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v); if (text != null) node.textContent = text; svg.appendChild(node); return node; };

  // y scales per axis measure; further measures share the left axis.
  const scale = {};
  for (const measure of measures) {
    const values = numbers.filter(t => (axes.includes(measure) ? t.measure === measure : !axes.includes(t.measure))).flatMap(t => t.points.map(p => Number(p[1]))).filter(Number.isFinite);
    let lo = Math.min(...values), hi = Math.max(...values);
    if (!values.length) { lo = 0; hi = 1; }
    if (lo === hi) { lo -= 1; hi += 1; }
    const pad = (hi - lo) * 0.06;
    scale[measure] = { lo: lo - pad, hi: hi + pad };
  }
  const axisOf = t => axes.includes(t.measure) ? t.measure : axes[0];
  const y = (t, v) => { const s = scale[axisOf(t)] || { lo: 0, hi: 1 }; return m.t + plotH - (v - s.lo) / (s.hi - s.lo) * plotH; };

  // grid and time axis
  const step = niceTimeStep(end - start);
  const multiDay = end - start > 86400e3;
  for (let tick = Math.ceil(start / step) * step; tick <= end; tick += step) {
    el('line', { x1: x(tick), x2: x(tick), y1: m.t, y2: height - m.b, class: 'grid-line' });
    const iso = new Date(tick).toISOString();
    el('text', { x: x(tick), y: height - 6, class: 'axis-text', 'text-anchor': 'middle' }, multiDay ? `${iso.slice(5, 10)} ${iso.slice(11, 16)}` : iso.slice(11, 16));
  }
  if (numbers.length) {
    axes.forEach((measure, side) => {
      const s = scale[measure];
      for (let k = 0; k <= 4; k++) {
        const v = s.lo + (s.hi - s.lo) * k / 4, yy = m.t + plotH - plotH * k / 4;
        if (side === 0) el('line', { x1: m.l, x2: width - m.r, y1: yy, y2: yy, class: 'grid-line' });
        el('text', { x: side === 0 ? m.l - 6 : width - m.r + 6, y: yy + 3, class: 'axis-text', 'text-anchor': side === 0 ? 'end' : 'start' }, Math.abs(v) >= 1000 ? Math.round(v).toLocaleString() : +v.toPrecision(3));
      }
      el('text', { x: side === 0 ? 4 : width - 4, y: 11, class: 'axis-text axis-title', 'text-anchor': side === 0 ? 'start' : 'end' }, measure + (side === 0 && measures.length > 2 ? ' (+ others)' : ''));
    });
  }

  // numbers: step lines (Historian stores changes; a value holds until the next)
  for (const t of numbers) {
    const pts = t.points.map(p => [Date.parse(p[0]), Number(p[1])]).filter(p => Number.isFinite(p[1]));
    if (!pts.length) continue;
    let d = '';
    pts.forEach(([ms, v], k) => { d += k === 0 ? `M${x(ms)},${y(t, v)}` : `H${x(ms)}V${y(t, v)}`; });
    d += `H${x(Math.max(pts[pts.length - 1][0], dataEnd))}`;
    el('path', { d, class: 'trend-line', stroke: colorOf(t.i) });
  }
  // text: one lane each, colored by value
  texts.forEach((t, lane) => {
    const top = m.t + plotH + 4 + lane * (laneH + 4);
    el('rect', { x: m.l, y: top, width: width - m.l - m.r, height: laneH, class: 'lane-bg' });
    t.points.forEach((p, k) => {
      const from = Date.parse(p[0]), to = k + 1 < t.points.length ? Date.parse(t.points[k + 1][0]) : dataEnd;
      if (to <= from) return;
      const r = el('rect', { x: x(from), y: top, width: Math.max(0.5, x(to) - x(from)), height: laneH, fill: bandColor(p[1]) });
      const title = document.createElementNS(NS, 'title'); title.textContent = `${shortTag(t.name)}: ${p[1]} from ${fmtTime(p[0])}`; r.appendChild(title);
    });
    el('rect', { x: m.l - 6, y: top + 3, width: 4, height: laneH - 6, fill: colorOf(t.i), rx: 1 });
  });

  // hover: a cursor line and each tag's value in force at that time
  const cursor = el('line', { x1: 0, x2: 0, y1: m.t, y2: height - m.b, class: 'cursor-line', visibility: 'hidden' });
  const wrap = document.createElement('div');
  wrap.className = 'chart-wrap';
  wrap.appendChild(svg);
  const readout = document.createElement('div');
  readout.className = 'readout';
  readout.hidden = true;
  wrap.appendChild(readout);
  const valueAt = (t, ms) => { let found = null; for (const p of t.points) { if (Date.parse(p[0]) <= ms) found = p; else break; } return found; };
  svg.addEventListener('mousemove', e => {
    const box = svg.getBoundingClientRect();
    const px = (e.clientX - box.left) * width / box.width;
    if (px < m.l || px > width - m.r) { cursor.setAttribute('visibility', 'hidden'); readout.hidden = true; return; }
    const ms = start + (px - m.l) / (width - m.l - m.r) * (end - start);
    cursor.setAttribute('x1', px); cursor.setAttribute('x2', px); cursor.setAttribute('visibility', 'visible');
    readout.innerHTML = `<b>${esc(new Date(ms).toISOString().slice(0, 19).replace('T', ' '))} UTC</b>` + data.tags.map((t, i) => {
      const p = valueAt(t, ms);
      const v = p == null ? '—' : typeof p[1] === 'number' ? (+p[1].toPrecision(6)).toLocaleString() : String(p[1]);
      return `<span><i style="background:${colorOf(i)}"></i>${esc(shortTag(t.name))}: <b>${esc(v)}</b></span>`;
    }).join('');
    readout.hidden = false;
    readout.style.left = Math.min(box.width - 220, Math.max(0, e.clientX - box.left + 12)) + 'px';
  });
  svg.addEventListener('mouseleave', () => { cursor.setAttribute('visibility', 'hidden'); readout.hidden = true; });
  return wrap;
}

// Charts read their colors when they draw: redraw on a theme change or resize.
new MutationObserver(() => { if (!$('pane-series').hidden && series.tree) drawAllTrends(); })
  .observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => { if (!$('pane-series').hidden && series.tree) drawAllTrends(); });
let trendResize = null;
window.addEventListener('resize', () => { clearTimeout(trendResize); trendResize = setTimeout(() => { if (!$('pane-series').hidden && series.tree) series.windows.forEach(w => { const card = document.querySelector(`[data-window="${w.id}"] [data-body]`); if (card && w.data) { card.innerHTML = ''; card.appendChild(chart(w.data, i => getComputedStyle(document.documentElement).getPropertyValue(TREND.colors[i]).trim())); } }); }, 200); });

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
// Read before anything rewrites the URL (showTab and the interview both write it).
const linkedPlant = hashValue('plant'), linkedSection = hashValue('section'), linkedTable = hashValue('table');
// A link pasted or followed while the page is open (the page itself writes the
// URL with replaceState, which does not fire this).
window.addEventListener('hashchange', () => {
  const plant = hashValue('plant');
  if (!plant) return;
  if (currentTab !== PLANT_TAB + plant) return openPlant(plant, hashValue('section') || undefined, hashValue('table'));
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
    openPlant(linkedPlant, linkedSection || undefined, linkedTable);
    return;
  }
  let open = 'interview';
  if (!linkedInterview) {
    try { if (((await api('GET', '/api/plants')).plants || []).some(p => p.managed)) open = 'plants'; }
    catch { /* open the interview; the Plants view reports its own errors */ }
  }
  showTab(open);
})();
