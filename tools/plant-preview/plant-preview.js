// Shared plant preview renderer.
//
// Used by the review page in this folder (index.html, published as an
// artifact by scripts/render_plant_preview.py) and by the web container's
// interview page (src/IndustrialDataSim.Web/wwwroot), which links this file.
// Plain browser JavaScript with no dependencies, so both pages work air-gapped.
//
//   const data = PlantPreviewRenderer.fromPreview(previewJson, { title, notes });
//   PlantPreviewRenderer.render(rootElement, data);
//
// fromPreview turns the simulator's preview document (`plant preview`, or the
// interview's stored preview; property names in camelCase) into the page
// data: hours since the start instead of timestamps, each breakdown joined to
// its ticket, downtime, status history, parts and signal windows.
// render fills rootElement with the preview sections. Colors come from CSS
// custom properties on :root (--run, --down, --accent …), so the host page
// decides light and dark; plant-preview.css styles the sections.
(function (global) {
  'use strict';

  const MARKUP = `
  <div class="facts" data-pp="facts"></div>

  <section>
    <h2>What each line did</h2>
    <div class="legend" data-pp="legend"></div>
    <div class="panel scroll"><svg class="gantt" data-pp="gantt" role="img" aria-label="Line state timeline with maintenance tickets"></svg></div>
    <p class="note">Diamonds are breakdowns, colored by ticket priority; a filled ring marks a planted story incident. Squares are preventive maintenance. Select a diamond to see that breakdown below.</p>
  </section>

  <section>
    <h2>One breakdown, signals and records together</h2>
    <div class="panel" style="display:grid; gap:16px">
      <div class="incident-head"><h3 data-pp="inc-title">Loading…</h3><div data-pp="inc-chips" style="display:flex; gap:6px; flex-wrap:wrap"></div></div>
      <div class="steps" data-pp="inc-steps"></div>
      <div class="charts" data-pp="inc-charts"></div>
      <div class="cols" data-pp="inc-records"></div>
    </div>
  </section>

  <section data-pp="batch-section" hidden>
    <h2>Batches</h2>
    <p class="note">On batch lines the running time is split into recipe phases (shades of green in the timeline above; hover for the batch and phase). Down time inside a phase stretches it, and the phase resumes where it stopped.</p>
    <div class="legend" data-pp="phase-legend"></div>
    <div class="cols" data-pp="batch-records"></div>
    <div data-pp="batch-compare" hidden style="margin-top:16px"></div>
  </section>

  <section data-pp="cert-section" hidden>
    <h2>Work done without a current certificate</h2>
    <p class="note">Safety-critical tickets assigned to a technician whose certificate had lapsed, and runs started by an operator without a current work-instruction certificate.</p>
    <div class="cols" data-pp="cert-records"></div>
  </section>

  <section>
    <h2>Tables the connector will poll</h2>
    <p>Final state of every table at the end of the preview, with its physical name in <span style="font-family:var(--mono)">flow_data_sim</span>. In the live plant each row appears and changes at its simulated time.</p>
    <div class="tablebar" data-pp="tablebar"></div>
    <div class="tablebox" data-pp="tablebox"></div>
  </section>

  <section data-pp="notes-section" hidden>
    <h2>Notes for the reviewer</h2>
    <ul class="gaps" data-pp="notes"></ul>
  </section>`;

  const NS = 'http://www.w3.org/2000/svg';
  const STATE = { Running: '--run', Changeover: '--change', Down: '--down', Maintenance: '--maint', MinorStop: '--minor', Signoff: '--change', Idle: '--idle' };
  const PRIORITY = { Critical: '--down', High: '--down', Medium: '--change', Low: '--muted' };
  const css = n => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
  const el = (tag, attrs = {}, parent) => { const e = document.createElementNS(NS, tag); for (const k in attrs) e.setAttribute(k, attrs[k]); if (parent) parent.appendChild(e); return e; };
  const esc = x => x == null ? x : String(x).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const fmtIso = s => s ? s.slice(5, 16).replace('T', ' ') + 'Z' : '—';

  // ---------------- preview document → page data ----------------

  function fromPreview(preview, options = {}) {
    const tables = preview.tables;
    const prefix = preview.simulation.toLowerCase() + '_';
    const start = Date.parse(preview.startUtc);
    const hours = s => s ? Math.round((Date.parse(s) - start) / 3600e3 * 1e4) / 1e4 : null;
    const tags = Object.fromEntries(preview.tags.map(t => [t.name, t]));
    const root = preview.tags.length ? preview.tags[0].name.split('.')[0] : preview.simulation;
    const series = (name, a, b) => name in tags ? tags[name].points.map(([t, v]) => [hours(t), v]).filter(([h]) => a <= h && h <= b) : [];

    const tickets = tables[prefix + 'maintenancetickets'] || [];
    const status = tables[prefix + 'maintenanceticketstatus'] || [];
    const downtime = Object.fromEntries((tables[prefix + 'downtimeevents'] || []).map(r => [r.downtime_id, r]));
    const parts = tables[prefix + 'partsusage'] || [];
    const incidents = preview.failures.map(f => {
      const ticket = tickets.find(t => t.ticket_type === 'Corrective' && t.downtime_id && downtime[t.downtime_id] &&
        downtime[t.downtime_id].start_utc === f.failedUtc && downtime[t.downtime_id].line_id === f.line) || null;
      const failed = hours(f.failedUtc);
      const repaired = f.repairedUtc ? hours(f.repairedUtc) : null;
      const window = [Math.max(0, failed - Math.max(f.warningHours, 2) - 1), (repaired ? repaired : failed + 2) + 1.5];
      const line = f.line, unit = f.equipment;
      const trends = { speed: series(`${root}.${line}.Speed`, ...window), vibration: series(`${root}.${line}.${unit}.Vibration`, ...window) };
      // A batch unit has no line speed; show its process temperature instead.
      if (`${root}.${line}.Temperature` in tags) trends.temperature = series(`${root}.${line}.Temperature`, ...window);
      if (`${root}.${line}.${unit}.FillVolume` in tags) trends.fill = series(`${root}.${line}.${unit}.FillVolume`, ...window);
      const records = ticket ? {
        ticket, downtime: downtime[ticket.downtime_id],
        status: status.filter(s => s.ticket_id === ticket.ticket_id),
        parts: parts.filter(p => p.ticket_id === ticket.ticket_id)
      } : {};
      return { line, unit, mode: f.mode, failed, repaired, warning: f.warningHours, planted: f.planted, window, trends, records };
    });
    return {
      simulation: preview.simulation, title: options.title || null, notes: options.notes || [], prefix,
      start: preview.startUtc, end: preview.endUtc, anchor: preview.anchorUtc,
      hours: hours(preview.endUtc), anchorHour: hours(preview.anchorUtc),
      lines: [...new Set(preview.segments.map(s => s.line))].sort(),
      summary: preview.summary,
      segments: preview.segments.map(s => [s.line, hours(s.start), hours(s.end), s.state, s.sku, s.order, s.reason, s.unit, s.batch ?? null, s.phase ?? null]),
      incidents,
      modes: Object.fromEntries((tables[prefix + 'failurecodes'] || []).map(m => [m.failure_code, m.description])),
      pm: tickets.filter(t => t.ticket_type === 'Preventive').map(t => ({ line: t.equipment_id.split('.')[0], start: hours(t.started_utc), id: t.ticket_id })),
      tables, changeCount: preview.changeCount, tagCount: preview.tags.length
    };
  }

  // ---------------- rendering ----------------

  function render(rootElement, D) {
    rootElement.innerHTML = MARKUP;
    const $ = name => rootElement.querySelector(`[data-pp="${name}"]`);
    const START = new Date(D.start);
    const PHASES = [];
    const PHASE = name => { if (!PHASES.includes(name)) PHASES.push(name); return '--ph' + (PHASES.indexOf(name) % 6 + 1); };
    const at = h => new Date(START.getTime() + h * 3600e3);
    const fmt = h => { if (h == null) return '—'; const d = at(h); return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' }) + ' ' + d.toISOString().slice(11, 16) + 'Z'; };

    function facts() {
      const s = D.summary;
      const items = [
        [D.lines.length + (D.lines.length === 1 ? ' line' : ' lines'), D.lines.join(' and ')],
        [Math.round(D.hours / 24) + ' days', fmt(0) + ' to ' + fmt(D.hours)],
        [s.salesOrders, 'Sales orders, ' + s.completedOrders + ' completed'],
        [(s.onTimePercent ?? 0) + '%', 'Completed on time'],
        [s.breakdowns, 'Breakdowns, ' + s.plantedBreakdowns + ' planted'],
        [s.correctiveTickets + ' + ' + s.preventiveTickets, 'Corrective + preventive tickets'],
        [(s.meanMinutesToRepair ?? '—') + ' min', 'Mean repair time'],
        [D.changeCount.toLocaleString(), 'Row inserts and updates'],
        [D.tagCount, 'Tags']
      ];
      if (s.qualitySamples) items.splice(6, 0, [s.qualityHolds + ' of ' + s.qualitySamples, 'Lots held by Quality']);
      if (s.shipments) items.splice(7, 0, [s.shipments, 'Shipments']);
      if (s.batches) items.splice(4, 0, [s.batches.total, 'Batches, ' + s.batches.discharged + ' discharged']);
      $('facts').innerHTML = items.map(([b, t]) => `<div class="fact"><b>${esc(b)}</b><span>${esc(t)}</span></div>`).join('');
      $('legend').innerHTML = Object.entries(STATE).filter(([k]) => k !== 'Signoff' || D.segments.some(g => g[3] === 'Signoff'))
        .map(([k, v]) => `<span><i class="sw" style="background:var(${v})"></i>${k === 'MinorStop' ? 'Minor stop' : k === 'Signoff' ? 'Supervisor sign-off' : k}</span>`).join('');
    }

    function gantt() {
      const svg = $('gantt');
      const W = 1000, left = 46, right = 8, rowH = 30, tickRow = 18, lines = D.lines;
      const H = 26 + lines.length * (rowH + tickRow + 14);
      svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
      const x = h => left + (h / D.hours) * (W - left - right);
      for (let d = 0; d <= D.hours; d += 24) {
        el('line', { x1: x(d), x2: x(d), y1: 18, y2: H - 4, stroke: css('--rule'), 'stroke-width': 1 }, svg);
        if (d < D.hours) el('text', { x: x(d) + 3, y: 12, 'font-size': 10 }, svg).textContent = at(d).toLocaleDateString('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' });
      }
      el('line', { x1: x(D.anchorHour), x2: x(D.anchorHour), y1: 16, y2: H - 2, stroke: css('--accent'), 'stroke-width': 2 }, svg);
      el('text', { x: x(D.anchorHour) - 4, y: H - 4, 'font-size': 10, 'text-anchor': 'end', fill: css('--accent') }, svg).textContent = 'anchor (now)';
      lines.forEach((line, i) => {
        const y = 22 + i * (rowH + tickRow + 14);
        el('text', { x: 0, y: y + rowH / 2 + 4, 'font-size': 12, fill: css('--ink') }, svg).textContent = line;
        let lastBatch = null;
        for (const [l, a, b, state, , , , , batch, phase] of D.segments) if (l === line) {
          // Batch lines: running time is drawn per recipe phase, with a tick at each new batch.
          const fill = state === 'Running' && phase ? css(PHASE(phase)) : css(STATE[state] || '--idle');
          const r = el('rect', { x: x(a), y, width: Math.max(.4, x(b) - x(a)), height: rowH, fill }, svg);
          if (batch) el('title', {}, r).textContent = `${batch} · ${phase}${state === 'Running' ? '' : ' (' + state + ')'} · ${fmt(a)}`;
          if (batch && batch !== lastBatch && state === 'Running') el('line', { x1: x(a), x2: x(a), y1: y - 3, y2: y + rowH + 3, stroke: css('--ink'), 'stroke-width': 1 }, svg);
          if (batch) lastBatch = batch;
        }
        for (const pm of D.pm) if (pm.line === line && pm.start != null) {
          const g = el('g', {}, svg);
          el('rect', { x: x(pm.start) - 4, y: y + rowH + 5, width: 8, height: 8, fill: css('--maint') }, g);
          el('title', {}, g).textContent = `${pm.id} preventive maintenance, ${fmt(pm.start)}`;
        }
        for (const inc of D.incidents) if (inc.line === line) {
          const cx = x(inc.failed), cy = y + rowH + 9;
          const g = el('g', { class: 'marker', tabindex: 0, role: 'button', 'aria-label': `${D.modes[inc.mode]} at ${fmt(inc.failed)}` }, svg);
          const prio = inc.records.ticket ? inc.records.ticket.priority : 'Medium';
          if (inc.planted) el('circle', { cx, cy, r: 8, fill: 'none', stroke: css('--accent'), 'stroke-width': 2 }, g);
          el('path', { d: `M${cx} ${cy - 6} L${cx + 6} ${cy} L${cx} ${cy + 6} L${cx - 6} ${cy} Z`, fill: css(PRIORITY[prio]) }, g);
          el('title', {}, g).textContent = `${D.modes[inc.mode]}, ${fmt(inc.failed)}`;
          g.addEventListener('click', () => show(inc));
          g.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); show(inc); } });
        }
      });
    }

    function chart(container, label, points, inc, opts = {}) {
      const wrap = document.createElement('div');
      wrap.innerHTML = `<div class="chart-label">${esc(label)}</div>`;
      const svg = el('svg', { class: 'chart', viewBox: '0 0 1000 120', preserveAspectRatio: 'none', role: 'img', 'aria-label': label });
      wrap.appendChild(svg); container.appendChild(wrap);
      const [a, b] = inc.window;
      const vals = points.map(p => p[1]);
      let lo = opts.lo ?? Math.min(...vals), hi = opts.hi ?? Math.max(...vals);
      if (!isFinite(lo)) { lo = 0; hi = 1; }
      if (!isFinite(hi)) hi = lo + 1;
      if (hi - lo < 1e-9) { hi += 1; lo -= 1; }
      const x = h => (h - a) / (b - a) * 1000, y = v => 112 - (v - lo) / (hi - lo) * 104;
      if (inc.warning > 0) el('rect', { x: x(Math.max(a, inc.failed - inc.warning)), y: 0, width: x(inc.failed) - x(Math.max(a, inc.failed - inc.warning)), height: 120, fill: css('--warn-band') }, svg);
      if (inc.repaired != null) el('rect', { x: x(inc.failed), y: 0, width: x(inc.repaired) - x(inc.failed), height: 120, fill: css('--down'), opacity: .12 }, svg);
      let d = '', prev = null;
      for (const [h, v] of points) {
        if (prev != null && h - prev > 2 / 60 && opts.gaps) d += ` M${x(h)},${y(v)}`;
        else d += (d ? ' L' : 'M') + `${x(h)},${y(v)}`;
        prev = h;
      }
      el('path', { d, fill: 'none', stroke: css(opts.color || '--accent'), 'stroke-width': 1.6, 'vector-effect': 'non-scaling-stroke' }, svg);
      for (const m of inc.marks) el('line', { x1: x(m.h), x2: x(m.h), y1: 0, y2: 120, stroke: css(m.color), 'stroke-width': 1.2, 'stroke-dasharray': '4 3', 'vector-effect': 'non-scaling-stroke' }, svg);
      el('text', { x: 4, y: 12, 'font-size': 11 }, svg).textContent = (Math.round(hi * 100) / 100).toString();
      el('text', { x: 4, y: 116, 'font-size': 11 }, svg).textContent = (Math.round(lo * 100) / 100).toString();
    }

    function rowsTable(title, rows) {
      if (!rows || !rows.length) return '';
      const cols = Object.keys(rows[0]);
      const v = x => x === null || x === undefined ? '<span style="color:var(--muted)">null</span>' : typeof x === 'string' && /^\d{4}-\d\d-\d\dT/.test(x) ? fmtIso(x) : esc(String(x));
      return `<div style="min-width:0"><div class="chart-label" style="margin-bottom:6px">${esc(title)}</div><div class="scroll"><table><tr>${cols.map(c => `<th>${esc(c)}</th>`).join('')}</tr>${rows.map(r => `<tr>${cols.map(c => `<td>${v(r[c])}</td>`).join('')}</tr>`).join('')}</table></div></div>`;
    }

    function show(inc) {
      const r = inc.records, t = r.ticket, hours = s => s ? (new Date(s) - START) / 3600e3 : null;
      $('inc-title').textContent = `${D.modes[inc.mode] || inc.mode} · ${inc.line} ${inc.unit}`;
      $('inc-chips').innerHTML = (inc.planted ? '<span class="chip planted">Planted story incident</span>' : '<span class="chip">Random breakdown</span>') +
        (t ? `<span class="chip">${esc(t.ticket_id)} · ${esc(t.priority)}</span>` : '');
      const steps = [
        ['Signals warn', inc.warning ? fmt(inc.failed - inc.warning) : '—', inc.warning ? `Vibration starts rising ${inc.warning} h before` : 'No warning period'],
        ['Unit fails', fmt(inc.failed), inc.trends.temperature ? 'The batch phase stops where it is' : 'Line speed drops to zero'],
        ['Detector fires', t ? fmtIso(t.created_utc) : '—', t ? `Downtime ${r.downtime.downtime_id} and ticket opened` : 'After the preview ends'],
        ['Assigned', t ? fmtIso(t.assigned_utc) : '—', t && t.technician_id ? `Technician ${t.technician_id}` : 'Waiting'],
        ['Repair starts', t ? fmtIso(t.started_utc) : '—', 'Technician on site'],
        ['Back in service', t ? fmtIso(t.completed_utc) : '—', r.parts && r.parts.length ? `Parts: ${r.parts.map(p => p.part_id + ' ×' + p.quantity).join(', ')}` : '']
      ];
      $('inc-steps').innerHTML = steps.map(([b, s, e], i) => `<div class="step" style="border-color:var(${i < 2 ? '--down' : i < 3 ? '--change' : '--run'})"><b>${b}</b><span>${esc(s)}</span><em>${esc(e)}</em></div>`).join('');
      inc.marks = [{ h: inc.failed, color: '--down' }];
      if (t) {
        inc.marks.push({ h: hours(t.created_utc), color: '--change' });
        if (t.completed_utc) inc.marks.push({ h: hours(t.completed_utc), color: '--run' });
      }
      const charts = $('inc-charts');
      charts.innerHTML = '';
      if (inc.trends.temperature) chart(charts, `${inc.line}.Temperature (°C): relaxes toward ambient while the batch unit is down`, inc.trends.temperature, inc, { color: '--run' });
      else chart(charts, `${inc.line}.Speed (cases/min)`, inc.trends.speed, inc, { lo: 0, color: '--run' });
      chart(charts, `${inc.line}.${inc.unit}.Vibration (mm/s): shaded band is the warning period`, inc.trends.vibration, inc, { lo: 0, color: '--down' });
      if (inc.trends.fill) chart(charts, `${inc.line}.${inc.unit}.FillVolume (oz, only while filling)`, inc.trends.fill, inc, { color: '--accent', gaps: true });
      $('inc-records').innerHTML = t
        ? rowsTable(D.prefix + 'downtimeevents', [r.downtime]) + rowsTable(D.prefix + 'maintenancetickets', [t]) + rowsTable(D.prefix + 'maintenanceticketstatus', r.status) + rowsTable(D.prefix + 'partsusage', r.parts)
        : '<p>This breakdown is detected after the preview window ends.</p>';
    }

    function certification() {
      const s = D.summary, a = s.uncertifiedAssignments, r = s.uncertifiedRuns;
      if (!a && !r) return;
      $('cert-section').hidden = false;
      $('cert-records').innerHTML =
        (a ? (a.length ? rowsTable('Uncertified safety-critical assignments', a) : '<p>No safety-critical ticket went to an uncertified technician.</p>') : '') +
        (r ? (r.length ? rowsTable('Runs by an uncertified operator', r) : '<p>Every checked run had a certified operator.</p>') : '');
    }

    function batches() {
      const b = D.summary.batches;
      if (!b) return;
      $('batch-section').hidden = false;
      $('phase-legend').innerHTML = PHASES.map(p => `<span><i class="sw" style="background:var(${PHASE(p)})"></i>${esc(p)}</span>`).join('');
      const recipes = b.recipes.map(r => ({ recipe: r.recipe, sku: r.sku, batches: r.batches, mean_cycle_minutes: r.meanCycleMinutes }));
      $('batch-records').innerHTML =
        rowsTable(`${b.total} batches, ${b.discharged} discharged, ${b.interruptedPhases} phases interrupted by a breakdown`, recipes);
      batchComparison(b);
    }

    // Golden batch comparison (summary.batches.comparison, engine phase 4): one row per
    // discharged batch; rows that started inside a drift window are shaded.
    function batchComparison(b) {
      const rows = b.comparison || [];
      if (!rows.length) return;
      const box = $('batch-compare');
      box.hidden = false;
      const drifts = b.drifts || [];
      const inDrift = r => drifts.some(d => d.line === r.line && (!d.recipe || d.recipe === r.recipe) &&
        Date.parse(r.startUtc) >= Date.parse(d.fromUtc) && (!d.untilUtc || Date.parse(r.startUtc) < Date.parse(d.untilUtc)));
      const shade = 'background:color-mix(in srgb, var(--change) 25%, transparent)';
      const num = x => x == null ? '<span style="color:var(--muted)">null</span>' : esc(x);
      const windows = drifts.map(d => `${esc(d.phase)} on ${esc(d.line)}: ${esc(d.cause)}, ${fmtIso(d.fromUtc)} to ${d.untilUtc ? fmtIso(d.untilUtc) : 'still active'}${d.endsWith ? ` (ended by ${esc(d.endsWith)})` : ''}`);
      const offGolden = rows.filter(r => r.golden === false).length;
      box.innerHTML = `<div class="chart-label" style="margin-bottom:6px">Batch comparison · ${rows.length} batches, ${offGolden} off golden</div>` +
        (windows.length ? `<p class="note"><i class="sw" style="${shade}"></i> Shaded rows started inside a drift window. ${windows.join('; ')}.</p>` : '') +
        `<div class="scroll"><table><tr>${['batch', 'recipe', 'start', 'cycle min', 'yield %', 'deviation', 'golden', 'status'].map(c => `<th>${c}</th>`).join('')}</tr>` +
        rows.map(r => `<tr${inDrift(r) ? ` style="${shade}"` : ''}><td>${esc(r.batch)}</td><td>${esc(r.recipe)}</td><td>${fmtIso(r.startUtc)}</td>` +
          `<td>${num(r.cycleMinutes)}</td><td>${num(r.yieldPercent)}</td><td${r.golden === false ? ' style="color:var(--down); font-weight:600"' : ''}>${num(r.deviationScore)}</td>` +
          `<td>${r.golden == null ? '' : r.golden ? 'yes' : 'no'}</td><td>${esc(r.status)}</td></tr>`).join('') + '</table></div>';
    }

    function tables() {
      const bar = $('tablebar'), box = $('tablebox');
      const names = Object.keys(D.tables);
      const order = ['maintenancetickets', 'downtimeevents', 'productionorders', 'salesorders', 'productionruns'].map(p => D.prefix + p);
      names.sort((a, b) => ((order.indexOf(a) + 1 || 99) - (order.indexOf(b) + 1 || 99)) || a.localeCompare(b));
      const select = name => {
        for (const b of bar.children) b.setAttribute('aria-pressed', b.dataset.name === name);
        const rows = D.tables[name];
        box.innerHTML = rowsTable(`${name} · ${rows.length} rows${rows.length > 200 ? ' (first 200 shown)' : ''}`, rows.slice(0, 200)).replace('<div class="scroll">', '<div>');
      };
      bar.innerHTML = names.map(n => `<button class="tab" type="button" data-name="${esc(n)}" aria-pressed="false">${esc(n.replace(D.prefix, ''))} <span style="opacity:.7">${D.tables[n].length}</span></button>`).join('');
      for (const b of bar.children) b.addEventListener('click', () => select(b.dataset.name));
      if (names.length) select(names[0]);
    }

    if (D.notes && D.notes.length) {
      $('notes-section').hidden = false;
      $('notes').innerHTML = D.notes.map(n => `<li>${esc(n)}</li>`).join('');
    }
    facts(); gantt(); batches(); certification(); tables();
    const first = D.incidents.find(i => i.planted) || D.incidents[0];
    if (first) show(first); else $('inc-title').textContent = 'No breakdowns in this window.';
  }

  global.PlantPreviewRenderer = { fromPreview, render };
})(window);
