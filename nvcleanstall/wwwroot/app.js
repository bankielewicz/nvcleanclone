'use strict';
/* CleanDriver wizard — five screens per the approved mockups + rulings R1–R10. */

const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g,
  ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
const api = {
  get: p => fetch(p).then(r => r.json()),
  post: (p, body) => fetch(p, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
  }).then(r => r.json()),
};

const state = {
  gpu: null,
  releases: [],
  tweakDefs: [],
  sourceKind: 'catalog',
  version: null,          // selected catalog version
  localPath: '',
  token: null,
  manifest: null,
  components: [],          // selected component ids
  tweaks: {},              // tweak id -> bool | string
  action: 'install',
  screen: 'source',
  comboOpen: false,
  hiComp: null,
  hiTweak: null,
  expertShown: false,
  pendingPreset: null,     // selection to apply once a manifest is loaded
  pollTimer: null,
};

/* ---------- theme & shell mode ---------- */

function setTheme(t) {
  document.body.setAttribute('data-theme', t);
  $('t-light').classList.toggle('on', t === 'light');
  $('t-dark').classList.toggle('on', t === 'dark');
}
$('t-light').onclick = () => setTheme('light');
$('t-dark').onclick = () => setTheme('dark');
if (new URLSearchParams(location.search).get('shell') === '1') document.body.classList.add('shell');
if (window.matchMedia('(prefers-color-scheme: dark)').matches) setTheme('dark');

/* ---------- navigation ---------- */

const SCREENS = ['source', 'download', 'components', 'tweaks', 'action', 'run'];

const FOOTER = {
  source:     { step: 'Step <b>1</b> of 5 — Driver source', back: null, next: 'Next' },
  download:   { step: 'Step <b>2</b> of 5 — Download', back: null, next: 'Next', nextDisabled: true, note: true }, // R3: Back hidden while downloading
  components: { step: 'Step <b>3</b> of 5 — Components', back: 'source', next: 'Next' },
  tweaks:     { step: 'Step <b>4</b> of 5 — Tweaks', back: 'components', next: 'Next' },
  action:     { step: 'Step <b>5</b> of 5 — Action', back: 'tweaks', next: 'Start' },
  run:        { step: 'Step <b>5</b> of 5 — Action', hidden: true }, // R4: footer hidden while running / after completion
};

function show(screen) {
  state.screen = screen;
  for (const s of SCREENS) $(`screen-${s}`).classList.toggle('active', s === screen);
  const f = FOOTER[screen];
  $('step-label').innerHTML = f.step;
  $('foot-actions').classList.toggle('hidden', !!f.hidden);
  $('btn-back').classList.toggle('hidden', !f.back);
  $('btn-next').textContent = f.next || 'Next';
  $('btn-next').classList.toggle('disabled', !!f.nextDisabled);
  $('next-note').classList.toggle('hidden', !f.note);
}

$('btn-back').onclick = () => {
  const target = FOOTER[state.screen].back;
  if (target) show(target);
};

$('btn-next').onclick = () => {
  if (state.screen === 'source') return leaveSource();
  if (state.screen === 'components') return show('tweaks');
  if (state.screen === 'tweaks') { renderAction(); return show('action'); }
  if (state.screen === 'action') return startExecute();
};

/* ---------- screen 1: driver source ---------- */

function selectSource(kind) {
  state.sourceKind = kind;
  $('opt-catalog').classList.toggle('sel', kind === 'catalog');
  $('opt-local').classList.toggle('sel', kind === 'local');
  $('catalog-body').classList.toggle('disabled-block', kind !== 'catalog');
  $('local-body').classList.toggle('disabled-block', kind !== 'local');
}
$('opt-catalog').querySelector('.opt-head').onclick = () => selectSource('catalog');
$('opt-local').querySelector('.opt-head').onclick = () => selectSource('local');

$('btn-browse').onclick = async () => {
  // No OS file picker from the page: Browse fills in the bundled sample package.
  const r = await api.get('/api/sample-package');
  if (r.path) $('local-path').value = r.path;
};

function renderCombo() {
  const rel = state.releases.find(r => r.version === state.version);
  $('combo-ver').textContent = rel.version;
  $('combo-tag').textContent = rel.channel;
  $('combo-tag').className = 'tag' + (rel.channel === 'Beta' ? ' beta' : '');
  $('combo-date').textContent = rel.releaseDate;
  $('combo').classList.toggle('open', state.comboOpen);

  const menu = $('combo-menu');
  menu.innerHTML = '';
  const latest = state.releases[0]?.version;
  for (const r of state.releases) {
    const row = document.createElement('div');
    row.className = 'row' + (r.version === state.version ? ' on' : '');
    row.innerHTML =
      `<span class="ver">${r.version}</span>` +
      `<span class="tag${r.channel === 'Beta' ? ' beta' : ''}">${r.channel}</span>` +
      `<span class="date">${r.releaseDate}</span>` +
      (r.version === latest ? '<span class="anno latest">Latest</span>' : '') +
      (state.gpu && r.version === state.gpu.installedDriverVersion ? '<span class="anno installed">Installed</span>' : '') +
      (r.version === state.version ? '<span class="check"><svg viewBox="0 0 16 16"><path d="M3 8 L6.5 11.5 L13 4.5" fill="none" stroke="currentColor" stroke-width="1.8"/></svg></span>' : '');
    row.onclick = () => { state.version = r.version; state.comboOpen = false; renderCombo(); };
    menu.appendChild(row);
  }
}
$('combo-field').onclick = () => { state.comboOpen = !state.comboOpen; renderCombo(); };

async function leaveSource() {
  if (state.sourceKind === 'catalog') {
    const dl = await api.post('/api/download', { version: state.version });
    if (dl.error) return alert(dl.error);
    show('download');
    pollDownload(dl.jobId);
  } else {
    const path = $('local-path').value.trim();
    if (!path) return alert('Enter the path of a package folder or .zip first.');
    const r = await api.post('/api/package', { kind: 'local', path });
    if (r.error) return alert(r.error);
    adoptManifest(r);
  }
}

/* ---------- screen 2: download ---------- */

function pollDownload(jobId) {
  const rel = state.releases.find(r => r.version === state.version);
  $('dl-sub').textContent = `${rel.version} ${rel.channel} — ${rel.sizeMB} MB`;
  $('dl-total').textContent = `${rel.sizeMB} MB`;
  let cancelled = false;
  // GAP-02: tell the server to abort a real download (deletes the partial file);
  // harmless no-op for the simulated mock-path job.
  $('btn-cancel-dl').onclick = () => { cancelled = true; api.post(`/api/download/${jobId}/cancel`, {}); show('source'); };

  const tick = async () => {
    if (cancelled || state.screen !== 'download') return;
    const j = await api.get(`/api/jobs/${jobId}`);
    $('dl-fill').style.width = `${Math.round(j.progress * 100)}%`;
    $('dl-done').textContent = `${j.doneMB} MB`;
    $('dl-speed').textContent = j.speed || '';
    if (j.status === 'failed') { alert('Download failed: ' + (j.error || 'unknown error')); return show('source'); }
    // HARD-04: the server can cancel a real download on its own (Jobs.cs sets "cancelled"
    // and deletes the .part). Terminal, and silent: a cancel is a cancel whoever started
    // it, and R3 already puts cancel back on screen 1. Failure alerts because it is news.
    if (j.status === 'cancelled') return show('source');
    if (j.status === 'done') {
      const r = await api.post('/api/package', { kind: 'catalog', version: state.version });
      if (r.error) return alert(r.error);
      if (!cancelled && state.screen === 'download') adoptManifest(r);
      return;
    }
    setTimeout(tick, 150);
  };
  tick();
}

/* ---------- screen 3: components ---------- */

function adoptManifest(r) {
  state.token = r.token;
  state.manifest = r.manifest;
  // GAP-02: live downloads carry a sample component list (real package parsing is
  // not yet implemented); label it so it isn't mistaken for the real package.
  $('comp-sample-note').classList.toggle('hidden', !state.manifest.sampleComponents);
  const comps = state.manifest.components;
  state.components = comps.filter(c => c.required || c.recommended).map(c => c.id);
  if (state.pendingPreset) {
    applySelection(state.pendingPreset);
    state.pendingPreset = null;
  }
  state.hiComp = comps.find(c => !c.required)?.id || comps[0].id;
  renderComponents();
  show('components');
}

function compById(id) { return state.manifest.components.find(c => c.id === id); }

function setComponent(id, on, panelMsgs) {
  const c = compById(id);
  if (!c || c.required) return;
  const sel = new Set(state.components);
  if (on) {
    sel.add(id);
    const missing = (c.dependsOn || []).filter(d => !sel.has(d));
    if (missing.length) {
      missing.forEach(d => sel.add(d));
      panelMsgs.push(`<b>Requires:</b> ${esc(missing.map(d => compById(d).name).join(', '))} — these will be selected automatically.`);
    }
  } else {
    sel.delete(id);
    const dependents = state.manifest.components
      .filter(o => sel.has(o.id) && (o.dependsOn || []).includes(id));
    if (dependents.length) {
      dependents.forEach(o => sel.delete(o.id));
      panelMsgs.push(`<b>Required by:</b> ${esc(dependents.map(o => o.name).join(', '))} — deselected along with it.`);
    }
  }
  state.components = state.manifest.components.map(c2 => c2.id).filter(i => sel.has(i));
}

function renderComponents(depMsg) {
  const list = $('comp-list');
  list.innerHTML = '';
  for (const c of state.manifest.components) {
    const on = state.components.includes(c.id);
    const row = document.createElement('div');
    row.className = 'crow' + (state.hiComp === c.id ? ' hi' : '');
    row.innerHTML =
      `<button class="cb${on ? ' on' : ''}${c.required ? ' locked' : ''}" ${c.required ? 'disabled' : ''} role="checkbox" aria-checked="${on}" aria-label="${esc(c.name)}">` +
      '<svg viewBox="0 0 16 16"><path d="M3 8 L6.5 11.5 L13 4.5" fill="none" stroke="currentColor" stroke-width="2"/></svg></button>' +
      `<span class="cname">${esc(c.name)}${c.required ? ' <span class="req">(required)</span>' : ''}</span>` +
      `<span class="csize">${esc(c.sizeMB)} MB</span>`;
    row.onclick = () => { state.hiComp = c.id; renderComponents(); };
    if (!c.required) {
      row.querySelector('.cb').onclick = (e) => {
        e.stopPropagation();
        state.hiComp = c.id;
        const msgs = [];
        setComponent(c.id, !on, msgs);
        renderComponents(msgs.join('<br>'));
      };
    }
    list.appendChild(row);
  }

  const total = state.manifest.components.reduce((s, c) => s + c.sizeMB, 0);
  const selSum = state.manifest.components.filter(c => state.components.includes(c.id))
    .reduce((s, c) => s + c.sizeMB, 0);
  $('size-sel').textContent = `${selSum} MB`;
  $('size-total').textContent = `of ${total} MB`;

  const hi = compById(state.hiComp);
  $('comp-rp-title').textContent = hi.name;
  $('comp-rp-desc').textContent = hi.description || '';
  const dep = $('comp-rp-dep');
  if (depMsg) {
    dep.innerHTML = depMsg; dep.classList.remove('hidden');
  } else if ((hi.dependsOn || []).length) {
    const names = esc(hi.dependsOn.map(d => compById(d).name).join(', '));
    dep.innerHTML = `<b>Requires:</b> ${names} — these will be selected automatically.`;
    dep.classList.remove('hidden');
  } else {
    dep.classList.add('hidden');
  }
}

$('btn-recommended').onclick = () => {
  state.components = state.manifest.components
    .filter(c => c.required || c.recommended).map(c => c.id);
  renderComponents();
};

/* ---------- screen 4: tweaks ---------- */

function tweakOn(id) { return state.tweaks[id] === true; }

function renderTweaks() {
  const list = $('tweak-list');
  list.innerHTML = '';

  const addSection = label => {
    const h = document.createElement('div');
    h.className = 'sec-head';
    h.innerHTML = `<span class="label">${label}</span>`;
    list.appendChild(h);
  };

  const addRow = (t) => {
    const on = tweakOn(t.id);
    const row = document.createElement('div');
    row.className = 'trow' + (state.hiTweak === t.id ? ' hi' : '');
    row.innerHTML =
      `<button class="cb${on ? ' on' : ''}" role="checkbox" aria-checked="${on}" aria-label="${t.name}">` +
      '<svg viewBox="0 0 16 16"><path d="M3 8 L6.5 11.5 L13 4.5" fill="none" stroke="currentColor" stroke-width="2"/></svg></button>' +
      `<span class="tname">${t.name}</span>` +
      (t.experimental ? '<span class="badge">Experimental</span>' : '');
    row.onclick = () => { state.hiTweak = t.id; renderTweaks(); };
    row.querySelector('.cb').onclick = (e) => {
      e.stopPropagation();
      state.hiTweak = t.id;
      state.tweaks[t.id] = !on;
      renderTweaks();
    };
    list.appendChild(row);

    if (t.sub) {
      const parentOn = tweakOn(t.id);
      const subOn = state.tweaks[t.sub.id] === true;
      const sub = document.createElement('div');
      sub.className = 'trow sub' + (parentOn ? '' : ' disabled-block');
      sub.innerHTML =
        `<button class="cb${subOn ? ' on' : ''}" role="checkbox" aria-checked="${subOn}" aria-label="${t.sub.name}">` +
        '<svg viewBox="0 0 16 16"><path d="M3 8 L6.5 11.5 L13 4.5" fill="none" stroke="currentColor" stroke-width="2"/></svg></button>' +
        `<span class="tname">${t.sub.name}</span>`;
      sub.querySelector('.cb').onclick = (e) => {
        e.stopPropagation();
        state.tweaks[t.sub.id] = !subOn;
        renderTweaks();
      };
      list.appendChild(sub);
    }

    if (t.conflictsWith && on && tweakOn(t.conflictsWith.tweak)) {
      const warn = document.createElement('div');
      warn.className = 'warn-banner';
      warn.textContent = t.conflictsWith.warning;
      list.appendChild(warn);
    }

    if (t.params && on) {
      const cur = state.tweaks[t.params.id] || t.params.default;
      const segRow = document.createElement('div');
      segRow.className = 'seg-row';
      segRow.innerHTML = `<span class="lbl">${t.params.name}:</span>`;
      const seg = document.createElement('div');
      seg.className = 'seg';
      for (const v of t.params.values) {
        const s = document.createElement('span');
        s.className = v === cur ? 'on' : '';
        s.textContent = v;
        s.onclick = () => { state.tweaks[t.params.id] = v; renderTweaks(); };
        seg.appendChild(s);
      }
      segRow.appendChild(seg);
      list.appendChild(segRow);
    }
  };

  addSection('Installation tweaks');
  state.tweakDefs.filter(t => t.category === 'install').forEach(addRow);

  const switchRow = document.createElement('div');
  switchRow.className = 'switch-row';
  switchRow.innerHTML =
    `<div class="sw${state.expertShown ? ' on' : ''}"><div class="knob"></div></div>` +
    '<span class="tname">Show expert tweaks</span>';
  switchRow.onclick = () => { state.expertShown = !state.expertShown; renderTweaks(); };
  list.appendChild(switchRow);

  if (state.expertShown) {
    addSection('Expert tweaks');
    state.tweakDefs.filter(t => t.category === 'expert').forEach(addRow);
  }

  const hi = state.tweakDefs.find(t => t.id === state.hiTweak) || state.tweakDefs[0];
  $('tweak-rp-title').textContent = hi.name;
  $('tweak-rp-desc').innerHTML = hi.description +
    (hi.learnMore ? ` <a href="${hi.learnMore}" target="_blank" rel="noreferrer">Learn more</a>` : '');
}

/* ---------- screen 5: action ---------- */

function renderAction() {
  for (const card of $('agrid').children) {
    card.classList.toggle('sel', card.dataset.action === state.action);
    card.onclick = () => { state.action = card.dataset.action; renderAction(); };
  }
  const needsPath = state.action === 'extract' || state.action === 'package';
  $('out-row').classList.toggle('disabled', !needsPath);
}
$('btn-out-browse').onclick = () => $('out-path').focus();

async function startExecute() {
  const selection = { components: state.components, tweaks: state.tweaks };
  const r = await api.post('/api/execute', {
    token: state.token,
    action: state.action,
    selection,
    outputPath: $('out-path').value.trim() || null,
  });
  if (r.error) return alert(r.error);

  const titles = {
    install: 'Installing customized driver package…',
    silent: 'Silent install running…',
    extract: 'Extracting customized package…',
    package: 'Building self-contained package…',
  };
  $('run-title').textContent = titles[state.action];
  $('run-status').classList.add('hidden');
  $('run-banner').classList.add('hidden');
  $('run-log').innerHTML = '';
  $('run-fill').style.width = '0%';
  $('run-bar').classList.toggle('hidden', state.action === 'silent'); // silent: log only
  show('run');
  pollRun(r.jobId);
}

function winPath(p) { return (p || '').replace(/\//g, '\\'); }

function pollRun(jobId) {
  let shown = 0;
  const tick = async () => {
    const j = await api.get(`/api/jobs/${jobId}`);
    const log = $('run-log');
    for (; shown < j.log.length; shown++) {
      const ln = document.createElement('div');
      const e = j.log[shown];
      ln.className = 'ln' + (e.cls ? ` ${e.cls}` : '');
      ln.textContent = e.text;
      log.appendChild(ln);
    }
    log.scrollTop = log.scrollHeight;
    $('run-fill').style.width = `${Math.round(j.progress * 100)}%`;

    if (j.status === 'running') return setTimeout(tick, 200);

    if (j.status === 'done') {
      $('run-status').classList.remove('hidden');
      const texts = {
        install: `Installation complete — receipt written to <code>${esc(winPath(shortPath(j.receipt)))}</code>`,
        silent: `Silent install complete — receipt written to <code>${esc(winPath(shortPath(j.receipt)))}</code>`,
        extract: `Extraction complete — package written to <code>${esc(winPath(shortPath(j.outDir)))}</code>`,
        package: `Package built — written to <code>${esc(winPath(shortPath(j.outDir)))}</code>`,
      };
      $('banner-text').innerHTML = texts[j.type];
      $('banner-caption').classList.toggle('hidden', !j.rebootRecommended);
      $('run-banner').classList.remove('hidden');
      $('btn-open-folder').onclick = () => api.post('/api/open-folder', { path: j.outDir || null });
    }
  };
  tick();
}

// show paths relative to the app directory when possible (mockup style: output\receipt-….json)
function shortPath(p) {
  if (!p) return '';
  const m = p.replace(/\\/g, '/').match(/(output\/.+|presets\/.+)$/);
  return m ? m[1] : p;
}

$('btn-finish').onclick = () => {
  state.token = null;
  state.manifest = null;
  state.components = [];
  show('source');
  renderCombo();
};

/* ---------- presets ---------- */

function applySelection(sel) {
  if (state.manifest && Array.isArray(sel.components)) {
    const valid = new Set(state.manifest.components.map(c => c.id));
    state.components = sel.components.filter(id => valid.has(id));
    for (const c of state.manifest.components) if (c.required && !state.components.includes(c.id)) state.components.push(c.id);
  }
  state.tweaks = { ...(sel.tweaks || {}) };
  if (state.tweakDefs.some(t => t.category === 'expert' && (state.tweaks[t.id] === true))) {
    state.expertShown = true;
  }
  if (state.manifest) renderComponents();
  renderTweaks();
}

function openPopover(html) {
  const pop = $('preset-pop');
  pop.innerHTML = html;
  pop.classList.remove('hidden');
  return pop;
}
document.addEventListener('click', (e) => {
  const pop = $('preset-pop');
  if (!pop.classList.contains('hidden') &&
      !pop.contains(e.target) &&
      e.target.id !== 'btn-save-preset' && e.target.id !== 'btn-load-preset') {
    pop.classList.add('hidden');
  }
});

$('btn-save-preset').onclick = () => {
  const pop = openPopover(
    '<span class="label">Save preset</span>' +
    '<input id="preset-name" placeholder="Preset name">' +
    '<button class="btn primary sm" id="preset-save-go">Save</button>');
  pop.querySelector('#preset-save-go').onclick = async () => {
    const name = pop.querySelector('#preset-name').value.trim();
    if (!name) return;
    const r = await api.post('/api/presets', {
      name, selection: { components: state.components, tweaks: state.tweaks },
    });
    pop.classList.add('hidden');
    if (r.error) alert(r.error);
  };
};

$('btn-load-preset').onclick = async () => {
  const r = await api.get('/api/presets');
  const rows = r.presets.length
    ? r.presets.map(p => `<div class="prow" data-name="${esc(p.name)}">${esc(p.name)}</div>`).join('')
    : '<div class="pempty">No presets saved yet.</div>';
  const pop = openPopover(`<span class="label">Load preset</span><div class="plist">${rows}</div>`);
  for (const el of pop.querySelectorAll('.prow')) {
    el.onclick = async () => {
      const preset = await api.get(`/api/presets/${encodeURIComponent(el.dataset.name)}`);
      pop.classList.add('hidden');
      if (preset.error) return alert(preset.error);
      if (state.manifest) applySelection(preset.selection);
      else { state.pendingPreset = preset.selection; applySelection(preset.selection); }
    };
  }
};

/* ---------- boot ---------- */

(async function boot() {
  const [sys, cat, tw] = await Promise.all([
    api.get('/api/system'), api.get('/api/catalog'), api.get('/api/tweaks'),
  ]);
  state.gpu = sys;
  state.releases = cat.releases;
  state.tweakDefs = tw.tweaks;

  // GAP-01: mark the mock-fallback state (GPU not matched / offline) so the sample
  // catalog isn't mistaken for live NVIDIA releases. Only shown when source is mock.
  $('catalog-source-note').classList.toggle('hidden', cat.source !== 'mock');

  $('gpu-name').textContent = sys.name;
  $('gpu-driver').textContent = sys.installedDriverVersion;
  $('gpu-via').textContent = sys.isSimulated ? 'simulated device' : 'detected via system query';

  state.version = state.releases[0]?.version;
  for (const t of state.tweakDefs) {
    if (t.default) state.tweaks[t.id] = true;
    if (t.sub && t.sub.default) state.tweaks[t.sub.id] = true;
  }
  state.hiTweak = state.tweakDefs[0]?.id;
  renderCombo();
  renderTweaks();
  show('source');
})();
