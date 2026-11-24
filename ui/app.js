'use strict';
/* PadDeck editor. Vanilla JS, no build step. Talks to the local PadDeck app over a small JSON API. */

// ------------------------------------------------------------------ helpers

const $ = (s) => document.querySelector(s);

function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'text') el.textContent = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'value') el.value = v;
    else if (k === 'checked') el.checked = !!v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat()) if (kid != null && kid !== false) el.append(kid.nodeType ? kid : document.createTextNode(kid));
  return el;
}

const ICONS = {
  plus: '<path d="M5 12h14M12 5v14"/>',
  trash: '<path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6"/>',
  up: '<path d="m18 15-6-6-6 6"/>',
  down: '<path d="m6 9 6 6 6-6"/>',
  play: '<path d="M7 4.5v15l12-7.5z"/>',
  copy: '<rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v1"/>',
  paste: '<rect x="8" y="3" width="8" height="4" rx="1"/><path d="M16 5h2a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h2"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  refresh: '<path d="M20 12a8 8 0 1 1-2.4-5.7L20 8.5M20 4v4.5h-4.5"/>',
  edit: '<path d="M4 20h4L19 9l-4-4L4 16z"/>',
  rec: '<circle cx="12" cy="12" r="5"/>',
};
function icon(name) {
  const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  s.setAttribute('viewBox', '0 0 24 24');
  s.setAttribute('fill', 'none');
  s.setAttribute('stroke', 'currentColor');
  s.setAttribute('stroke-width', '1.75');
  s.setAttribute('stroke-linecap', 'round');
  s.setAttribute('stroke-linejoin', 'round');
  s.setAttribute('aria-hidden', 'true');
  s.innerHTML = ICONS[name];
  return s;
}

async function api(path, body) {
  const opts = { headers: { 'X-PadDeck': '1' } };
  if (body !== undefined) {
    opts.method = 'POST';
    opts.headers['Content-Type'] = 'application/json';
    opts.body = typeof body === 'string' ? body : JSON.stringify(body);
  }
  const r = await fetch(path, opts);
  const text = await r.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* not json */ }
  if (!r.ok) throw new Error((json && json.error) || r.statusText);
  return json;
}

const hex = (n) => '#' + (n >>> 0).toString(16).padStart(6, '0').slice(-6);
function luminance(c) {
  const v = parseInt(c.slice(1), 16);
  const ch = [(v >> 16) & 255, (v >> 8) & 255, v & 255].map((x) => { x /= 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; });
  return 0.2126 * ch[0] + 0.7152 * ch[1] + 0.0722 * ch[2];
}
const clone = (o) => JSON.parse(JSON.stringify(o));

// ------------------------------------------------------------------ device geometry

const ROWS = [9, 8, 7, 6, 5, 4, 3, 2, 1];
const kindOf = (id) => (id === 99 ? 'logo' : Math.floor(id / 10) === 9 ? 'top' : id % 10 === 9 ? 'side' : 'grid');
const TOP_LEGEND = { 91: '↑', 92: '↓', 93: '←', 94: '→', 95: 'Session', 96: 'Drums', 97: 'Keys', 98: 'User' };
function where(id) {
  const r = Math.floor(id / 10), c = id % 10;
  switch (kindOf(id)) {
    case 'logo': return 'Logo light';
    case 'top': return `Top row, button ${c} · #${id}`;
    case 'side': return `Right column, button ${9 - r} · #${id}`;
    default: return `Row ${9 - r}, column ${c} · #${id}`;
  }
}

// ------------------------------------------------------------------ state

const S = {
  cfg: null,
  status: { connected: false, page: 0 },
  autostart: false,
  configPath: '',
  sel: null,
  leds: null,
  clip: null,
  history: [],
  lastSaved: '',
  lists: { audio: 0, obs: 0 },
};

const pageIdx = () => Math.min(Math.max(S.status.page || 0, 0), S.cfg.pages.length - 1);
const curPage = () => S.cfg.pages[pageIdx()];

function binding(id) {
  const key = String(id);
  const p = curPage();
  if (p.buttons[key]) return { b: p.buttons[key], scope: 'page', overrides: !!S.cfg.global[key] };
  if (S.cfg.global[key]) return { b: S.cfg.global[key], scope: 'global' };
  return null;
}
const pageFaders = () => (curPage().autoMixer ? S.status.mixer || [] : curPage().faders);

/** The fader covering a pad and the pad's step along it (1 = 0%, 8 = 100%). */
function faderAt(id) {
  if (kindOf(id) !== 'grid') return null;
  const r = Math.floor(id / 10), c = id % 10;
  for (const f of pageFaders()) {
    if (!f.target) continue;
    if (!f.horizontal && f.column === c) return { f, pos: r };
    if (f.horizontal && f.row === r) return { f, pos: c };
  }
  return null;
}

/** Auto mixer: the top-row pad above an app's column mutes it (unless you've put your own button there). */
function autoMuteFader(id) {
  const p = curPage();
  if (!p.autoMixer || p.autoHorizontal || id < 91 || id > 98 || p.buttons[String(id)]) return null;
  return (S.status.mixer || []).find((f) => f.column === id - 90) || null;
}

// ------------------------------------------------------------------ saving + undo

let saveTimer = 0;
function setSave(text, state) {
  const el = $('#save-state');
  el.textContent = text;
  el.dataset.state = state || '';
}
function commit({ rerender = true } = {}) {
  if (S.lastSaved) {
    S.history.push(S.lastSaved);
    if (S.history.length > 60) S.history.shift();
  }
  if (rerender) render();
  else renderDevice();
  clearTimeout(saveTimer);
  setSave('Saving…');
  saveTimer = setTimeout(saveNow, 300);
}
async function saveNow() {
  const json = JSON.stringify(S.cfg);
  try {
    await api('/api/config', json);
    S.lastSaved = json;
    setSave('Saved');
    setTimeout(() => { if ($('#save-state').textContent === 'Saved') setSave(''); }, 1600);
  } catch (e) {
    setSave('Not saved: ' + e.message, 'error');
  }
}
function undo() {
  const prev = S.history.pop();
  if (!prev) return;
  S.cfg = JSON.parse(prev);
  S.lastSaved = '';
  render();
  clearTimeout(saveTimer);
  saveTimer = setTimeout(saveNow, 50);
  setSave('Undone');
}

// ------------------------------------------------------------------ device rendering

const padEls = {};
function buildDevice() {
  const dev = $('#device');
  dev.textContent = '';
  for (const r of ROWS) {
    for (let c = 1; c <= 9; c++) {
      const id = r * 10 + c;
      const kind = kindOf(id);
      const el = h('button', {
        class: 'pad' + (kind === 'top' || kind === 'side' ? ' edge' : '') + (kind === 'logo' ? ' logo' : ''),
        role: 'gridcell',
        tabindex: '-1',
        'data-id': id,
        'aria-selected': 'false',
        onclick: () => select(id),
        ondblclick: () => { if (kind !== 'logo') testPress(id); },
        onkeydown: (e) => padKey(e, id),
        oncontextmenu: (e) => padMenu(e, id),
        ondragstart: (e) => dragStart(e, id),
        ondragover: (e) => dragOver(e, id),
        ondragleave: () => padEls[id].classList.remove('drop-target', 'drop-swap'),
        ondrop: (e) => dragDrop(e, id),
        ondragend: dragEnd,
      }, h('span', { class: 'pad-label' }));
      padEls[id] = el;
      dev.append(el);
    }
  }
  padEls[81].tabIndex = 0;
}

function configuredColor(id) {
  if (id === 99) return S.cfg.settings.logoColor;
  const fa = faderAt(id);
  if (fa) return fa.f.color;
  const bd = binding(id);
  if (bd && bd.b.color === 'player') return (S.status && S.status.playerColor) || '#64748b';   // follows the playing music app
  return bd ? bd.b.color : '#000000';
}

/** New app pads take their icon's colour. */
function colorFromIcon(b, path) {
  if (!path) return;
  api('/api/apps/color', { path }).then((r) => {
    if (r && r.color) { b.color = r.color; commit(); }
  }).catch(() => {});
}

function renderDevice() {
  if (!S.cfg) return;
  for (const [sid, el] of Object.entries(padEls)) {
    const id = +sid;
    const kind = kindOf(id);
    const live = S.leds && S.leds[id] != null ? hex(S.leds[id]) : configuredColor(id);
    const lit = live && live !== '#000000';
    el.style.setProperty('--led', live);
    el.toggleAttribute('data-lit', lit);
    el.dataset.ink = lit && luminance(live) > 0.42 ? 'dark' : 'light';
    const fa = faderAt(id);
    const fader = fa && fa.f;
    el.classList.toggle('fader-cell', !!fa);
    const bd = fa ? null : binding(id);
    el.draggable = !!bd;
    const label = el.firstChild;
    let text = '';
    if (bd && bd.b.label) text = bd.b.label;
    else if (fa && fa.pos === 1) text = fader.label || fader.target;
    else if (!bd && autoMuteFader(id)) text = 'Mute';
    label.textContent = text;
    const legend = el.querySelector('.legend');
    const want = !text && !lit && (kind === 'top' || kind === 'side') ? (kind === 'top' ? TOP_LEGEND[id] : (id === 19 ? '■' : '›')) : '';
    if (want && !legend) el.append(h('span', { class: 'legend', text: want }));
    else if (!want && legend) legend.remove();
    else if (legend) legend.textContent = want;
    const name = kind === 'logo' ? 'Logo light' : (text || (fader ? `Volume fader: ${fader.target}` : 'Empty')) + ', ' + where(id);
    el.setAttribute('aria-label', name);
    el.setAttribute('aria-selected', String(S.sel === id));
  }
}

function flashPad(id, down) {
  const el = padEls[id];
  if (el) el.classList.toggle('down', down);
}

function padKey(e, id) {
  const r = Math.floor(id / 10), c = id % 10;
  const moves = { ArrowUp: [1, 0], ArrowDown: [-1, 0], ArrowLeft: [0, -1], ArrowRight: [0, 1] };
  if (moves[e.key]) {
    e.preventDefault();
    const nr = Math.min(9, Math.max(1, r + moves[e.key][0]));
    const nc = Math.min(9, Math.max(1, c + moves[e.key][1]));
    select(nr * 10 + nc, true);
  } else if (e.key === 'Enter' && e.ctrlKey && id !== 99) {
    testPress(id);
  }
}

function select(id, focus) {
  const wasPage = S.sel === 'page';
  S.sel = id;
  if (wasPage) renderPages();
  for (const el of Object.values(padEls)) el.tabIndex = -1;
  padEls[id].tabIndex = 0;
  if (focus) padEls[id].focus();
  renderDevice();
  renderInspector();
}

async function testPress(id) {
  try { await api('/api/press', { id }); } catch (e) { setSave(e.message, 'error'); }
}

// ------------------------------------------------------------------ pages

function renderPages() {
  const nav = $('#pages');
  nav.textContent = '';
  const tabs = h('div', { role: 'tablist', 'aria-label': 'Pages', style: 'display:flex;gap:4px;flex-wrap:wrap' });
  S.cfg.pages.forEach((p, i) => {
    tabs.append(h('button', {
      class: 'tab', role: 'tab', 'aria-selected': String(i === pageIdx()),
      title: 'Double-click to rename',
      onclick: () => goPage(i),
      ondblclick: () => renamePage(i),
    }, h('span', { class: 'num', text: String(i + 1) }), p.name || `Page ${i + 1}`));
  });
  nav.append(tabs);
  nav.append(h('button', { class: 'btn ghost small', onclick: addPage }, icon('plus'), 'New page'));
  nav.append(h('div', { class: 'tools' },
    h('button', { class: 'btn ghost small', 'aria-pressed': String(S.sel === 'page'), onclick: selectPage }, icon('edit'), 'Page settings')));
}

function selectPage() {
  S.sel = 'page';
  renderPages();
  renderDevice();
  renderInspector();
}

function renderPageInspector(el) {
  const p = curPage();
  el.append(h('div', { class: 'insp-where', text: `Page ${pageIdx() + 1} of ${S.cfg.pages.length}` }));
  el.append(h('h2', { class: 'insp-title', text: p.name }));

  const name = h('input', { type: 'text', value: p.name, maxlength: 40, 'aria-label': 'Page name' });
  name.addEventListener('change', () => { if (name.value.trim()) { p.name = name.value.trim(); commit(); } });
  el.append(h('div', { class: 'group' }, field('Name', name)));

  const mix = h('div', { class: 'group' }, h('div', { class: 'group-head' }, h('h3', { text: 'Auto mixer' })));
  mix.append(h('label', { class: 'kv' },
    h('span', {}, 'Fill this page with app volume faders', h('small', { text: 'Every app that has sound open gets its own fader, coloured from its icon. Apps appear and disappear on their own.' })),
    h('input', { type: 'checkbox', checked: p.autoMixer, onchange: (e) => { p.autoMixer = e.target.checked; commit(); } })));
  if (p.autoMixer) {
    mix.append(field('Layout', seg(p.autoHorizontal ? 'h' : 'v', [['v', 'Columns'], ['h', 'Rows']], (v) => { p.autoHorizontal = v === 'h'; commit(); }, 'Fader layout'),
      p.autoHorizontal ? 'Each row is an app. Left pad is 0%, right pad is 100%.' : 'Each column is an app. Bottom pad is 0%, top pad is 100%. The top-row button above it mutes it.'));
    mix.append(h('label', { class: 'kv' },
      h('span', {}, 'Also add speakers and mic', h('small', { text: 'Off by default: this page is for app volumes. Uses two of the eight faders.' })),
      h('input', { type: 'checkbox', checked: !!p.autoSystem, onchange: (e) => { p.autoSystem = e.target.checked; commit(); } })));
    mix.append(h('p', { class: 'note', text: 'If more apps have sound open than there are faders, the ones currently playing win.' }));
  }
  el.append(mix);

  el.append(h('div', { class: 'footer-actions' },
    h('button', { class: 'btn danger', type: 'button', onclick: deletePage, disabled: S.cfg.pages.length <= 1 }, icon('trash'), 'Delete this page')));
}

// ------------------------------------------------------------------ right-click menu

function closeMenu() { document.querySelectorAll('.ctx').forEach((m) => m.remove()); }

function padMenu(e, id) {
  e.preventDefault();
  closeMenu();
  if (kindOf(id) === 'logo') return;
  select(id);
  const bd = binding(id);
  const fa = faderAt(id);
  const items = [];
  if (bd || autoMuteFader(id)) items.push(['Try it', () => testPress(id)]);
  if (bd) items.push(['Copy', () => { S.clip = clone(bd.b); setSave('Copied'); renderInspector(); }]);
  if (S.clip && !fa) items.push(['Paste', () => paste(id)]);
  if (bd) items.push(['Reset to blank', () => resetPad(id)], ['Delete mapping', () => clearPad(id), true]);
  if (fa && !curPage().autoMixer) items.push(['Remove fader', () => removeFader(fa.f), true]);
  if (!items.length) return;
  const m = h('div', { class: 'ctx', role: 'menu' }, items.map(([label, fn, danger]) =>
    h('button', { type: 'button', role: 'menuitem', class: danger ? 'danger' : null, text: label, onclick: () => { closeMenu(); fn(); } })));
  document.body.append(m);
  const r = m.getBoundingClientRect();
  m.style.left = Math.min(e.clientX, innerWidth - r.width - 8) + 'px';
  m.style.top = Math.min(e.clientY, innerHeight - r.height - 8) + 'px';
  m.firstChild.focus();
  m.addEventListener('keydown', (ev) => {
    const btns = [...m.querySelectorAll('button')];
    const i = btns.indexOf(document.activeElement);
    if (ev.key === 'Escape') { closeMenu(); padEls[id].focus(); }
    if (ev.key === 'ArrowDown') { ev.preventDefault(); btns[(i + 1) % btns.length].focus(); }
    if (ev.key === 'ArrowUp') { ev.preventDefault(); btns[(i - 1 + btns.length) % btns.length].focus(); }
  });
}
document.addEventListener('click', (e) => { if (!e.target.closest('.ctx')) closeMenu(); });
window.addEventListener('blur', closeMenu);

async function goPage(i) {
  S.status.page = i;
  S.leds = null;
  render();
  try { await api('/api/page', { page: i }); } catch { /* status event will correct us */ }
}

function renamePage(i) {
  const nav = $('#pages');
  const tab = nav.querySelectorAll('.tab')[i];
  if (!tab) return;
  const input = h('input', { class: 'tab-rename', type: 'text', value: S.cfg.pages[i].name, 'aria-label': 'Page name' });
  const done = (ok) => {
    if (ok && input.value.trim()) {
      S.cfg.pages[i].name = input.value.trim().slice(0, 40);
      commit();
    } else renderPages();
  };
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter') done(true); if (e.key === 'Escape') done(false); });
  input.addEventListener('blur', () => done(true));
  tab.replaceWith(input);
  input.focus();
  input.select();
}

function addPage() {
  const n = S.cfg.pages.length;
  S.cfg.pages.push({ name: `Page ${n + 1}`, buttons: {}, faders: [] });
  // Give the new page a switch button in the right column if that slot is free.
  const slot = String(89 - n * 10);
  if (n < 8 && !S.cfg.global[slot] && !S.cfg.pages.some((p) => p.buttons[slot])) {
    S.cfg.global[slot] = { label: `Page ${n + 1}`, color: '#1e293b', activeColor: '#e2e8f0', effect: 'static', mode: 'press',
      actions: [{ type: 'page', op: 'goto', page: n }], offActions: [] };
  }
  commit();
  goPage(n);
}

function deletePage() {
  if (S.cfg.pages.length <= 1) return;
  const i = pageIdx();
  if (!confirm(`Delete the page "${curPage().name}" and every button on it? You can undo with Ctrl+Z.`)) return;
  S.cfg.pages.splice(i, 1);
  const fix = (list) => list.forEach((a) => {
    if (a.type === 'page' && (a.op || 'goto') === 'goto') {
      if ((a.page || 0) === i) a.page = 0;
      else if ((a.page || 0) > i) a.page -= 1;
    }
  });
  const all = [...Object.values(S.cfg.global), ...S.cfg.pages.flatMap((p) => Object.values(p.buttons))];
  all.forEach((b) => { fix(b.actions); fix(b.offActions); });
  commit();
  goPage(Math.max(0, i - 1));
}

// ------------------------------------------------------------------ inspector

const SWATCHES = ['#ef4444', '#f97316', '#f59e0b', '#eab308', '#84cc16', '#22c55e', '#14b8a6', '#06b6d4', '#3b82f6', '#6366f1', '#a855f7', '#ec4899', '#e2e8f0', '#334155'];

function swatchPicker(value, onPick, { allowAuto = false, allowOff = true } = {}) {
  const wrap = h('div', { class: 'swatches', role: 'group' });
  if (allowAuto) wrap.append(h('button', { class: 'sw auto', type: 'button', 'aria-pressed': String(!value), onclick: () => onPick(null), text: 'Brighter' }));
  if (allowOff) wrap.append(h('button', { class: 'sw off', type: 'button', title: 'Off', 'aria-label': 'Off', 'aria-pressed': String(value === '#000000'), onclick: () => onPick('#000000') }));
  for (const c of SWATCHES) {
    wrap.append(h('button', { class: 'sw', type: 'button', style: `background:${c}`, title: c, 'aria-label': c, 'aria-pressed': String((value || '').toLowerCase() === c), onclick: () => onPick(c) }));
  }
  const custom = h('input', { class: 'sw-custom', type: 'color', value: value && value !== '#000000' ? value : '#3b82f6', title: 'Pick any colour', 'aria-label': 'Custom colour' });
  custom.addEventListener('input', () => onPick(custom.value, true));
  custom.addEventListener('change', () => onPick(custom.value));
  wrap.append(custom);
  return wrap;
}

function field(label, control, note) {
  return h('label', { class: 'field' }, h('span', { text: label }), control, note ? h('p', { class: 'note', text: note }) : null);
}
function text(value, onInput, attrs = {}) {
  const el = h('input', { type: 'text', value: value ?? '', ...attrs });
  el.addEventListener('input', () => onInput(el.value));
  el.addEventListener('change', () => commit({ rerender: false }));
  return el;
}
function num(value, onInput, attrs = {}) {
  const el = h('input', { type: 'number', value: value ?? '', ...attrs });
  el.addEventListener('change', () => { onInput(el.value === '' ? null : +el.value); commit({ rerender: false }); });
  return el;
}
function select_(value, options, onChange, attrs = {}) {
  const el = h('select', attrs, options.map(([v, l]) => h('option', { value: v, text: l })));
  el.value = value ?? options[0][0];
  el.addEventListener('change', () => onChange(el.value));
  return el;
}
function seg(value, options, onChange, label) {
  return h('div', { class: 'seg', role: 'group', 'aria-label': label },
    options.map(([v, l]) => h('button', { type: 'button', 'aria-pressed': String(value === v), onclick: () => onChange(v), text: l })));
}

const ACTION_TYPES = [
  ['hotkey', 'Press keys'],
  ['type', 'Type text'],
  ['open', 'Open app, file or website'],
  ['command', 'Run a command'],
  ['player', 'Music (whichever app is playing)'],
  ['media', 'Media keys'],
  ['volume', 'Volume'],
  ['audiodevice', 'Switch audio device'],
  ['obs', 'OBS Studio'],
  ['discord', 'Discord call'],
  ['page', 'Switch page'],
  ['sound', 'Play a sound'],
  ['lightshow', 'Light show'],
  ['delay', 'Wait'],
];

const EFFECT_NAMES = [['rainbow', 'Rainbow wave'], ['plasma', 'Plasma'], ['rain', 'Digital rain'], ['sparkle', 'Sparkle'], ['ripple', 'Ripples'], ['fire', 'Fire'], ['breathe', 'Breathe'], ['radar', 'Radar'], ['ocean', 'Ocean (blues, mint and starlight)']];

// --- pickers: installed apps (Start menu, incl. Store apps) and apps that have sound open

function openPicker({ title, placeholder, load, foot, allowCustom }, onPick) {
  const d = $('#picker');
  const q = $('#picker-q');
  const list = $('#picker-list');
  $('#picker-title').textContent = title;
  $('#picker-foot').textContent = foot || '';
  q.value = '';
  q.placeholder = placeholder;
  let items = null;
  let shown = [];
  const choose = (it) => { d.close(); onPick(it); };
  const draw = () => {
    list.textContent = '';
    if (!items) { list.append(h('div', { class: 'picker-empty', text: 'Loading…' })); return; }
    const term = q.value.trim().toLowerCase();
    shown = items.filter((i) => i.label.toLowerCase().includes(term) || (i.value || '').toLowerCase().includes(term)).slice(0, 250);
    if (allowCustom && term && !items.some((i) => i.value.toLowerCase() === term))
      shown.push({ custom: true, label: `Use the app named “${q.value.trim()}”`, value: term.replace(/\.exe$/, ''), name: q.value.trim() });
    if (!shown.length) list.append(h('div', { class: 'picker-empty', text: term ? 'Nothing matches that.' : allowCustom ? 'No apps have sound open right now. Type an app’s name instead, like spotify.' : 'No apps found.' }));
    shown.forEach((it) => list.append(h('button', { type: 'button', role: 'option', class: 'picker-item' + (it.custom ? ' custom' : ''), onclick: () => choose(it) },
      it.color ? h('i', { class: 'dot', style: `background:${it.color}` }) : null,
      h('span', { text: it.label }),
      it.note ? h('small', { text: it.note }) : null)));
  };
  q.oninput = draw;
  q.onkeydown = (e) => {
    if (e.key === 'Enter' && shown[0]) { e.preventDefault(); choose(shown[0]); }
    if (e.key === 'ArrowDown') { e.preventDefault(); list.querySelector('button')?.focus(); }
  };
  list.onkeydown = (e) => {
    const btns = [...list.querySelectorAll('button')];
    const i = btns.indexOf(document.activeElement);
    if (e.key === 'ArrowDown' && i < btns.length - 1) { e.preventDefault(); btns[i + 1].focus(); }
    if (e.key === 'ArrowUp') { e.preventDefault(); (i > 0 ? btns[i - 1] : q).focus(); }
  };
  draw();
  d.showModal();
  q.focus();
  load().then((r) => { items = r; draw(); }).catch(() => { items = []; draw(); });
}

function loadStartApps() {
  if (S.startApps) return Promise.resolve(S.startApps);
  return api('/api/apps').then((l) => {
    const items = l.map((a) => ({ label: a.name, value: a.id }));
    if (items.length) S.startApps = items;
    return items;
  });
}

function pickStartApp(cb) {
  openPicker({
    title: 'Choose an app', placeholder: 'Search your apps',
    load: loadStartApps,
    foot: 'Everything in your Start menu, including apps from the Microsoft Store.',
  }, cb);
}

function pickAudioApp(cb) {
  openPicker({
    title: 'Choose an app', placeholder: 'Search, or type an app name',
    load: () => api('/api/audio/apps').then((l) => l.map((a) => ({ label: a.label, value: a.name, color: a.color, note: a.playing ? 'Playing' : '' }))),
    foot: 'Apps that have sound open right now. The button keeps working after the app restarts.',
    allowCustom: true,
  }, cb);
}

function muteButtonFor(app) {
  return {
    label: `Mute ${app.label || app.name || app.value}`.slice(0, 24),
    color: app.color || '#e2e8f0', activeColor: '#dc2626', effect: 'static', mode: 'press',
    feedback: 'app.mute', feedbackArg: app.value,
    actions: [{ type: 'volume', target: app.value, op: 'togglemute' }], offActions: [],
  };
}

/** "Already open?" behaviour for app buttons: toggle front/minimized (default), just bring to front, or open again. */
function windowOptions(a) {
  const mode = a.window || 'toggle';
  const out = [field('If it’s already open', select_(mode, [
    ['toggle', 'Bring it to the front, or minimize it if it’s in front'],
    ['focus', 'Bring it to the front'],
    ['launch', 'Open it again'],
  ], (v) => { a.window = v === 'toggle' ? undefined : v; commit(); }),
  mode === 'toggle' ? 'Closed: opens it. Minimized: restores it. Behind other windows: brings it forward. In front: minimizes it.' : null)];
  if (mode === 'launch') return out;

  out.push(h('label', { class: 'kv' },
    h('span', {}, 'Hold the pad to close it', h('small', { text: 'Hold for more than half a second. The pad blinks red. The app may ask you to save first.' })),
    h('input', { type: 'checkbox', checked: a.holdClose !== false, onchange: (e) => { a.holdClose = e.target.checked ? undefined : false; commit({ rerender: false }); } })));

  const status = h('p', { class: 'note', role: 'status', text: 'Looking for its window…' });
  const words = { front: 'Found it: open, in front.', behind: 'Found it: open, behind other windows.', minimized: 'Found it: minimized.' };
  const check = () => api('/api/windows/check', { path: a.path, match: a.match || null }).then((r) => {
    status.textContent = words[r.state] || 'It isn’t open right now. Open it and this will find it.';
    status.classList.toggle('warn', !words[r.state]);
  }).catch(() => { status.textContent = ''; });
  check();
  const match = text(a.match, (v) => { a.match = v.trim() || undefined; }, { class: 'mono', placeholder: 'e.g. discord' });
  match.addEventListener('change', check);
  out.push(status, h('details', { class: 'more' },
    h('summary', { text: 'It’s open but not found?' }),
    field('Process name', match, 'The app’s .exe name, as shown in Task Manager’s Details tab. Usually not needed.')));
  return out;
}

/** Join a voice channel: server + channel names (suggested from what Discord shows), or a pasted channel link. */
function joinFields(a) {
  if (!S.discordLists || Date.now() - S.discordListsAt > 8000) {
    S.discordListsAt = Date.now();
    api('/api/discord/lists').then((r) => {
      S.discordLists = r;
      for (const [id, list] of [['dl-dservers', r.servers], ['dl-dchannels', r.channels]]) {
        const dl = document.getElementById(id);
        if (dl) dl.replaceChildren(...list.map((v) => h('option', { value: v })));
      }
    }).catch(() => {});
  }
  const L = S.discordLists || { servers: [], channels: [] };
  const datalist = (id, list) => h('datalist', { id }, ...list.map((v) => h('option', { value: v })));
  const sync = () => {
    const b = S.editing;
    if (b && a.text && (!b.label || b.label === 'Join VC' || b.label === S.lastJoinLabel)) { b.label = a.text.slice(0, 24); S.lastJoinLabel = b.label; }
  };
  return [
    datalist('dl-dservers', L.servers), datalist('dl-dchannels', L.channels),
    field('Server', text(a.target, (v) => { a.target = v.trim() || undefined; }, { list: 'dl-dservers', placeholder: 'e.g. VALO' })),
    field('Voice channel', text(a.text, (v) => { a.text = v.trim() || undefined; sync(); }, { list: 'dl-dchannels', placeholder: 'e.g. Lobby' }),
      'Exactly as Discord shows it. Suggestions are the voice channels of the server Discord has open.'),
    h('details', { class: 'more' },
      h('summary', { text: 'Or paste the channel’s link' }),
      field('Channel link', text(a.path, (v) => { a.path = v.trim() || undefined; }, { class: 'mono', placeholder: 'https://discord.com/channels/…' }),
        'In Discord, right-click the voice channel → Copy Link. Used instead of the names above.')),
  ];
}

function appAction(app) {
  return { type: 'open', path: 'shell:AppsFolder\\' + app.value, text: app.label };
}

function needAudioApps() {
  const now = Date.now();
  if (now - (S.lists.apps || 0) < 5000) return;
  S.lists.apps = now;
  api('/api/audio/apps').then((l) => {
    const changed = JSON.stringify(l) !== JSON.stringify(S.audioApps);
    S.audioApps = l;
    if (changed && typeof S.sel === 'number') renderInspector();
  }).catch(() => {});
}

function needDevices() {
  const now = Date.now();
  if (now - (S.lists.devices || 0) < 8000) return;
  S.lists.devices = now;
  api('/api/audio/devices').then((d) => {
    const changed = JSON.stringify(d) !== JSON.stringify(S.devices);
    S.devices = d;
    if (changed && typeof S.sel === 'number') renderInspector();
  }).catch(() => {});
}

const OBS_OPS = [
  ['scene', 'Switch to scene'], ['preview_scene', 'Preview scene (studio mode)'], ['transition', 'Transition (studio mode)'],
  ['record_toggle', 'Start / stop recording'], ['record_pause', 'Pause / resume recording'],
  ['stream_toggle', 'Start / stop streaming'], ['replay_toggle', 'Start / stop replay buffer'], ['replay_save', 'Save replay clip'],
  ['virtualcam_toggle', 'Start / stop virtual camera'], ['mute_toggle', 'Mute / unmute a source'], ['mute', 'Mute a source'], ['unmute', 'Unmute a source'],
];

const FEEDBACK = [
  ['', 'Automatic'],
  ['mic.mute', 'Microphone is muted'],
  ['master.mute', 'Speakers are muted'],
  ['app.mute', 'An app is muted'],
  ['obs.record', 'OBS is recording'],
  ['obs.stream', 'OBS is streaming'],
  ['obs.replay', 'OBS replay buffer is on'],
  ['obs.virtualcam', 'OBS virtual camera is on'],
  ['obs.scene', 'An OBS scene is live'],
  ['obs.mute', 'An OBS source is muted'],
  ['audio.device', 'An audio device is in use'],
  ['discord.mute', 'Discord mic is muted'],
  ['discord.deafen', 'Discord is deafened'],
  ['discord.camera', 'Discord camera is on'],
  ['discord.screen', 'Sharing your screen on Discord'],
  ['discord.call', 'In a Discord call'],
  ['player.playing', 'Music is playing'],
  ['player.shuffle', 'Music shuffle is on'],
  ['player.repeat', 'Music repeat is on'],
  ['player.mute', 'The music app is muted'],
  ['player.level', 'Music volume is at least…'],
];

const PLAYER_OPS = [
  ['playpause', 'Play / pause'], ['next', 'Next song'], ['prev', 'Previous song'],
  ['shuffle', 'Shuffle on / off'], ['repeat', 'Repeat: all → one → off'],
  ['seek', 'Jump back / forward'], ['app', 'Bring the music app up / minimize it'], ['like', 'Like / favourite the song'],
  ['volup', 'Music app louder'], ['voldown', 'Music app quieter'], ['setvol', 'Set the music app’s volume'], ['mute', 'Mute / unmute the music app'],
];
const PLAYER_LIGHT = { playpause: 'player.playing', shuffle: 'player.shuffle', repeat: 'player.repeat', mute: 'player.mute', setvol: 'player.level' };

const DISCORD_OPS = [
  ['mute', 'Mute / unmute your mic'],
  ['deafen', 'Deafen / undeafen'],
  ['camera', 'Turn camera on / off'],
  ['screen', 'Share your screen / stop sharing'],
  ['disconnect', 'Hang up (leave the call)'],
  ['decline', 'Decline an incoming call'],
  ['chat', 'Open / close the voice channel’s chat'],
  ['join', 'Join a voice channel'],
];
const DISCORD_LIGHT = { mute: 'discord.mute', deafen: 'discord.deafen', camera: 'discord.camera', screen: 'discord.screen', disconnect: 'discord.call' };

function newAction(type) {
  switch (type) {
    case 'media': return { type, op: 'playpause' };
    case 'volume': return { type, target: 'master', op: 'togglemute' };
    case 'obs': return { type, op: 'scene', target: '' };
    case 'page': return { type, op: 'goto', page: 0 };
    case 'sound': return { type, op: 'play', value: 100 };
    case 'delay': return { type, ms: 250 };
    case 'audiodevice': return { type, target: 'output', op: 'next' };
    case 'lightshow': return { type, op: 'next' };
    case 'discord': return { type, op: 'mute' };
    case 'player': return { type, op: 'playpause' };
    default: return { type };
  }
}

function needLists(kind) {
  const now = Date.now();
  if (now - S.lists[kind] < 8000) return;
  S.lists[kind] = now;
  if (kind === 'audio') {
    api('/api/audio/sessions').then((names) => {
      const dl = $('#dl-audio');
      dl.textContent = '';
      for (const n of ['master', 'mic', 'focused', ...names]) dl.append(h('option', { value: n }));
    }).catch(() => {});
  } else if (S.status.obsConnected) {
    api('/api/obs/scenes').then((names) => { const dl = $('#dl-scenes'); dl.textContent = ''; names.forEach((n) => dl.append(h('option', { value: n }))); }).catch(() => {});
    api('/api/obs/inputs').then((names) => { const dl = $('#dl-inputs'); dl.textContent = ''; names.forEach((n) => dl.append(h('option', { value: n }))); }).catch(() => {});
  }
}

// --- hotkey recorder

const CODE_NAMES = {
  Space: 'Space', Enter: 'Enter', Escape: 'Esc', Tab: 'Tab', Backspace: 'Backspace', Delete: 'Delete', Insert: 'Insert',
  Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
  PrintScreen: 'PrintScreen', Pause: 'Pause', CapsLock: 'CapsLock', ScrollLock: 'ScrollLock', NumLock: 'NumLock', ContextMenu: 'Apps',
  Minus: '-', Equal: '=', BracketLeft: '[', BracketRight: ']', Backslash: '\\', Semicolon: ';', Quote: "'", Comma: ',', Period: '.', Slash: '/', Backquote: '`',
  NumpadAdd: 'NumAdd', NumpadSubtract: 'NumSubtract', NumpadMultiply: 'NumMultiply', NumpadDivide: 'NumDivide', NumpadDecimal: 'NumDecimal', NumpadEnter: 'Enter',
  MediaPlayPause: 'MediaPlayPause', MediaTrackNext: 'MediaNext', MediaTrackPrevious: 'MediaPrev', MediaStop: 'MediaStop',
  AudioVolumeUp: 'VolumeUp', AudioVolumeDown: 'VolumeDown', AudioVolumeMute: 'VolumeMute',
};
function keyName(code) {
  if (/^Key[A-Z]$/.test(code)) return code.slice(3);
  if (/^Digit\d$/.test(code)) return code.slice(5);
  if (/^F\d{1,2}$/.test(code)) return code;
  if (/^Numpad\d$/.test(code)) return 'Num' + code.slice(6);
  return CODE_NAMES[code] || null;
}

function hotkeyField(a) {
  const input = text(a.keys, (v) => { a.keys = v; }, { class: 'mono', placeholder: 'Ctrl+Shift+M', list: 'dl-keys', 'aria-label': 'Keys' });
  const rec = h('button', { class: 'btn rec', type: 'button', 'aria-pressed': 'false', title: 'Record a shortcut from your keyboard' }, icon('rec'), 'Record');
  let recording = false;
  const stop = () => { recording = false; rec.setAttribute('aria-pressed', 'false'); rec.lastChild.textContent = 'Record'; window.removeEventListener('keydown', onKey, true); };
  const onKey = (e) => {
    e.preventDefault();
    e.stopPropagation();
    const mods = [];
    if (e.ctrlKey) mods.push('Ctrl');
    if (e.shiftKey) mods.push('Shift');
    if (e.altKey) mods.push('Alt');
    if (e.metaKey) mods.push('Win');
    if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) { input.value = mods.join('+') + '+…'; return; }
    if (e.code === 'Escape' && mods.length === 0) { input.value = a.keys || ''; stop(); return; }
    const k = keyName(e.code);
    if (!k) return;
    a.keys = [...mods, k].join('+');
    input.value = a.keys;
    stop();
    commit({ rerender: false });
  };
  rec.addEventListener('click', () => {
    if (recording) { stop(); return; }
    recording = true;
    rec.setAttribute('aria-pressed', 'true');
    rec.lastChild.textContent = 'Press keys…';
    window.addEventListener('keydown', onKey, true);
  });
  return field('Keys', h('div', { class: 'field-row' }, input, rec), 'F13 to F24 don’t exist on most keyboards, so they never clash. Bind them in OBS, Discord or games.');
}

function pathField(label, a, kind, placeholder) {
  const input = text(a.path, (v) => { a.path = v; }, { placeholder, 'aria-label': label });
  const browse = h('button', {
    class: 'btn icon', type: 'button', title: 'Browse', 'aria-label': 'Browse for a file',
    onclick: async () => {
      try {
        const r = await api('/api/pick', { kind });
        if (r && r.path) { a.path = r.path; input.value = r.path; commit({ rerender: false }); }
      } catch (e) { setSave(e.message, 'error'); }
    },
  }, icon('folder'));
  return field(label, h('div', { class: 'field-row' }, input, browse));
}

function actionBody(a) {
  switch (a.type) {
    case 'hotkey': return [hotkeyField(a)];
    case 'type': {
      const ta = h('textarea', { 'aria-label': 'Text to type', placeholder: 'Text typed into whatever window is focused' });
      ta.value = a.text || '';
      ta.addEventListener('input', () => { a.text = ta.value; });
      ta.addEventListener('change', () => commit({ rerender: false }));
      return [field('Text', ta)];
    }
    case 'open': {
      const choose = () => pickStartApp((app) => {
        Object.assign(a, appAction(app));
        a.args = undefined;
        const b = S.editing;
        if (b && (!b.label || b.label === 'Open' || b.label === 'Open app')) b.label = app.label.slice(0, 24);
        commit();
        if (b) colorFromIcon(b, a.path);
      });
      if (a.path && a.path.startsWith('shell:AppsFolder\\')) {
        return [field('App', h('div', { class: 'app-chosen' },
          h('b', { text: a.text || a.path.slice(17) }),
          h('button', { class: 'btn small', type: 'button', onclick: choose }, 'Change'))), ...windowOptions(a)];
      }
      return [
        h('div', { class: 'field' }, h('span', { text: 'App' }),
          h('button', { class: 'btn primary', type: 'button', style: 'justify-self:start', onclick: choose }, icon('plus'), 'Choose an app')),
        pathField('Or a file, folder or website', a, 'app', 'C:\\…\\file.pdf or https://…'),
        a.path ? field('Arguments (optional)', text(a.args, (v) => { a.args = v || undefined; }, { class: 'mono' })) : null,
        ...(/\.(exe|lnk)$/i.test(a.path || '') ? windowOptions(a) : []),
      ];
    }
    case 'command': return [field('Command', text(a.path, (v) => { a.path = v; }, { class: 'mono', placeholder: 'e.g. shutdown /s /t 60' }), 'Runs hidden with cmd.exe.')];
    case 'media': return [field('Control', select_(a.op, [['playpause', 'Play / pause'], ['next', 'Next track'], ['prev', 'Previous track'], ['stop', 'Stop'], ['volup', 'System volume up'], ['voldown', 'System volume down'], ['mute', 'System mute']], (v) => { a.op = v; commit({ rerender: false }); }))];
    case 'volume': {
      needLists('audio');
      needAudioApps();
      const pick = (v) => { a.target = v; commit(); };
      const chips = h('div', { class: 'chips', role: 'group', 'aria-label': 'Quick picks' },
        [['master', 'Speakers', null], ['mic', 'Mic', null], ['focused', 'App in front', null],
          ...(S.audioApps || []).map((x) => [x.name, x.label, x.color])].map(([v, l, c]) =>
          h('button', { type: 'button', class: 'chip-btn', 'aria-pressed': String((a.target || '').toLowerCase() === v), onclick: () => pick(v) },
            c ? h('i', { style: `background:${c}` }) : null, l)));
      const out = [
        field('What to control', text(a.target, (v) => { a.target = v; }, { list: 'dl-audio', placeholder: 'or type an app name, like spotify' })),
        chips,
        field('Do', select_(a.op, [['togglemute', 'Mute / unmute'], ['mute', 'Mute'], ['unmute', 'Unmute'], ['up', 'Turn up'], ['down', 'Turn down'], ['set', 'Set to a level']], (v) => { a.op = v; commit(); })),
      ];
      if (['up', 'down', 'set'].includes(a.op)) out.push(field(a.op === 'set' ? 'Level (%)' : 'Step (%)', num(a.value ?? (a.op === 'set' ? 50 : 5), (v) => { a.value = v; }, { min: 0, max: 100 })));
      return out;
    }
    case 'obs': {
      needLists('obs');
      const out = [field('Do', select_(a.op, OBS_OPS, (v) => { a.op = v; commit(); }))];
      if (['scene', 'preview_scene'].includes(a.op)) out.push(field('Scene', text(a.target, (v) => { a.target = v; }, { list: 'dl-scenes', placeholder: 'Scene name, exactly as in OBS' })));
      if (['mute_toggle', 'mute', 'unmute'].includes(a.op)) out.push(field('Source', text(a.target, (v) => { a.target = v; }, { list: 'dl-inputs', placeholder: 'e.g. Mic/Aux' })));
      if (!S.status.obsConnected) out.push(h('p', { class: 'note warn' }, 'OBS isn’t connected. ', h('button', { type: 'button', onclick: openSettings, text: 'Set it up in Settings' })));
      return out;
    }
    case 'page': {
      const out = [field('Go to', select_(a.op || 'goto', [['goto', 'A specific page'], ['next', 'Next page'], ['prev', 'Previous page']], (v) => { a.op = v; commit(); }))];
      if ((a.op || 'goto') === 'goto') out.push(field('Page', select_(String(a.page || 0), S.cfg.pages.map((p, i) => [String(i), `${i + 1}. ${p.name}`]), (v) => { a.page = +v; commit({ rerender: false }); })));
      return out;
    }
    case 'sound': return [
      pathField('Sound file', a, 'sound', 'mp3, wav or wma'),
      field('When pressed', select_(a.op || 'play', [['play', 'Play from the start'], ['toggle', 'Play, or stop if playing'], ['stopall', 'Stop all sounds']], (v) => { a.op = v; commit({ rerender: false }); })),
      field('Volume (%)', num(a.value ?? 100, (v) => { a.value = v; }, { min: 0, max: 100 })),
    ];
    case 'delay': return [field('Wait (milliseconds)', num(a.ms ?? 250, (v) => { a.ms = v; }, { min: 0, max: 60000, step: 50 }))];
    case 'audiodevice': {
      needDevices();
      const input = a.target === 'input';
      const list = (S.devices && S.devices[input ? 'inputs' : 'outputs']) || [];
      const out = [
        field('Which', seg(input ? 'input' : 'output', [['output', 'Output (speakers)'], ['input', 'Input (mic)']], (v) => { a.target = v; a.path = undefined; a.text = undefined; commit(); }, 'Device type')),
        field('Do', select_(a.op || 'next', [['next', 'Switch to the next device'], ['prev', 'Switch to the previous device'], ['set', 'Switch to a specific device']], (v) => { a.op = v; commit(); })),
      ];
      if (a.op === 'set') {
        if (!a.path && list.length) { a.path = list[0].id; a.text = list[0].name; setTimeout(() => commit({ rerender: false })); }
        const opts = list.map((d) => [d.id, d.name]);
        if (a.path && !list.some((d) => d.id === a.path)) opts.unshift([a.path, `${a.text || 'Saved device'} (not connected)`]);
        if (!opts.length) opts.push(['', S.devices ? 'No devices found' : 'Loading devices…']);
        out.push(field('Device', select_(a.path || opts[0][0], opts, (v) => { a.path = v; a.text = (list.find((d) => d.id === v) || {}).name; commit({ rerender: false }); })));
      } else {
        out.push(h('p', { class: 'note', text: `Goes round your ${list.length || 'connected'} ${input ? 'microphones' : 'output devices'} in order. Apps follow the new default straight away.` }));
      }
      return out;
    }
    case 'player': {
      const op = a.op || 'playpause';
      const out = [field('Do', select_(op, PLAYER_OPS, (v) => {
        a.op = v;
        a.value = v === 'seek' ? 10 : v === 'setvol' ? 50 : ['volup', 'voldown'].includes(v) ? 5 : undefined;
        const b = S.editing;
        if (b && (!b.feedback || b.feedback.startsWith('player.'))) {
          b.feedback = PLAYER_LIGHT[v];
          b.feedbackArg = v === 'setvol' ? String(a.value) : undefined;
        }
        commit();
      }))];
      if (op === 'seek') out.push(field('Seconds (negative goes back)', num(a.value ?? 10, (v) => { a.value = v; }, { min: -600, max: 600, step: 5 })));
      if (['volup', 'voldown'].includes(op)) out.push(field('Step (%)', num(a.value ?? 5, (v) => { a.value = v; }, { min: 1, max: 50 })));
      if (op === 'setvol') out.push(field('Volume (%)', num(a.value ?? 50, (v) => { a.value = v; const b = S.editing; if (b && b.feedback === 'player.level') b.feedbackArg = String(v); }, { min: 0, max: 100, step: 5 })));
      out.push(h('p', { class: 'note', text: 'Controls whichever app is playing (Spotify, Apple Music, a browser…) through Windows’ own media controls. Works in the background, with no shortcuts to set up.' }));
      return out;
    }
    case 'discord': return [
      field('Do', select_(a.op || 'mute', DISCORD_OPS, (v) => {
        a.op = v;
        // Light the pad from Discord's real state, when there is one for this action.
        const b = S.editing;
        if (b && (!b.feedback || b.feedback.startsWith('discord.'))) b.feedback = DISCORD_LIGHT[v];
        if (v === 'screen' && !a.target) a.target = 'Screen 1';
        commit();
      })),
      ...(a.op === 'join' ? joinFields(a) : []),
      ...((a.op || 'mute') === 'screen' ? [
        field('What to share on a tap', text(a.target, (v) => { a.target = v.trim() || undefined; }, { placeholder: 'Screen 1' }),
          'A screen (“Screen 1”, “Screen 2”) or an app’s name as Discord’s picker shows it. Hold the pad to open the picker instead. Tap again to stop sharing.'),
        h('div', { class: 'chips', role: 'group', 'aria-label': 'Quick picks' },
          ['Screen 1', 'Screen 2'].map((v) => h('button', { type: 'button', class: 'chip-btn', 'aria-pressed': String((a.target || 'Screen 1') === v), onclick: () => { a.target = v; commit(); } }, v))),
      ] : []),
      h('p', { class: 'note', text: (a.op === 'chat' || a.op === 'screen')
        ? 'Presses Discord’s own buttons. Brings Discord up, because it only redraws while it’s on screen.'
        : 'Presses Discord’s own button, so nothing needs setting up in Discord. Works while Discord is in the background or in the tray.' }),
    ];
    case 'lightshow': return [
      field('Do', select_(a.op || 'next', [['next', 'Start, or switch to the next effect'], ['toggle', 'Start or stop'], ['stop', 'Stop'], ['brighter', 'Make the show brighter'], ['dimmer', 'Make the show dimmer'],
        ...EFFECT_NAMES.map(([v, l]) => ['play:' + v, 'Play ' + l])], (v) => { a.op = v; commit({ rerender: false }); })),
      h('p', { class: 'note', text: 'Pressing any other pad stops the show.' }),
    ];
  }
  return [];
}

function actionList(list, title, note) {
  const wrap = h('div', { class: 'group' }, h('div', { class: 'group-head' }, h('h3', { text: title })));
  if (note) wrap.append(h('p', { class: 'group-note', text: note }));
  const box = h('div', { class: 'actions' });
  if (list.length === 0) box.append(h('div', { class: 'empty-actions', text: 'Nothing happens yet. Add an action below.' }));
  list.forEach((a, i) => {
    const head = h('div', { class: 'action-head' },
      h('span', { class: 'idx', text: String(i + 1) }),
      select_(a.type, ACTION_TYPES, (v) => { list[i] = newAction(v); commit(); }, { 'aria-label': `Action ${i + 1} type` }),
      h('button', { class: 'btn', type: 'button', title: 'Move up', 'aria-label': 'Move up', disabled: i === 0, onclick: () => { [list[i - 1], list[i]] = [list[i], list[i - 1]]; commit(); } }, icon('up')),
      h('button', { class: 'btn', type: 'button', title: 'Move down', 'aria-label': 'Move down', disabled: i === list.length - 1, onclick: () => { [list[i + 1], list[i]] = [list[i], list[i + 1]]; commit(); } }, icon('down')),
      h('button', { class: 'btn', type: 'button', title: 'Remove', 'aria-label': 'Remove action', onclick: () => { list.splice(i, 1); commit(); } }, icon('trash')));
    box.append(h('div', { class: 'action' }, head, ...actionBody(a)));
  });
  wrap.append(box);
  const add = select_('', [['', '+ Add an action'], ...ACTION_TYPES], (v) => { if (v) { list.push(newAction(v)); commit(); } }, { 'aria-label': 'Add an action' });
  wrap.append(h('div', { class: 'add-row' }, add));
  return wrap;
}

function ensureBinding(id, scope = 'page') {
  const key = String(id);
  const existing = binding(id);
  if (existing) return existing.b;
  const b = { color: '#3b82f6', effect: 'static', mode: 'press', actions: [], offActions: [] };
  if (scope === 'global') S.cfg.global[key] = b; else curPage().buttons[key] = b;
  return b;
}

const PRESETS = [
  { name: 'Keyboard shortcut', desc: 'Press a key combo', make: () => ({ label: 'Shortcut', color: '#f59e0b', actions: [{ type: 'hotkey', keys: '' }] }) },
  { name: 'Open an app', desc: 'Pick by name, Store apps too',
    pick: (id) => pickStartApp((app) => {
      const b = ensureBinding(id);
      const act = appAction(app);
      Object.assign(b, { label: app.label.slice(0, 24), color: '#334155', actions: [act] });
      commit();
      colorFromIcon(b, act.path);
    }) },
  { name: 'Music controls', desc: 'Follows whichever app is playing', make: () => ({ label: 'Play / pause', color: '#0369a1', activeColor: '#22c55e', feedback: 'player.playing', actions: [{ type: 'player', op: 'playpause' }] }) },
  { name: 'Mute an app', desc: 'Pick a running app',
    pick: (id) => pickAudioApp((app) => { Object.assign(ensureBinding(id), muteButtonFor(app)); commit(); }) },
  { name: 'Mute microphone', desc: 'Lights red while muted', make: () => ({ label: 'Mic', color: '#22c55e', activeColor: '#ef4444', feedback: 'mic.mute', actions: [{ type: 'volume', target: 'mic', op: 'togglemute' }] }) },
  { name: 'Join a voice channel', desc: 'Discord: any server, any channel', make: () => ({ label: 'Join VC', color: '#5865f2', actions: [{ type: 'discord', op: 'join' }] }) },
  { name: 'Push to talk', desc: 'Holds a key while pressed', make: () => ({ label: 'PTT', color: '#334155', activeColor: '#22c55e', mode: 'hold', actions: [{ type: 'hotkey', keys: 'F13' }] }) },
  { name: 'Play / pause', desc: 'Music and video', make: () => ({ label: 'Play', color: '#3b82f6', actions: [{ type: 'media', op: 'playpause' }] }) },
  { name: 'OBS scene', desc: 'Lights while live', make: () => ({ label: 'Scene', color: '#334155', activeColor: '#e2e8f0', feedback: 'obs.scene', feedbackArg: '', actions: [{ type: 'obs', op: 'scene', target: '' }] }) },
  { name: 'Sound clip', desc: 'Soundboard button', make: () => ({ label: 'Sound', color: '#ec4899', actions: [{ type: 'sound', op: 'play', value: 100 }] }) },
  { name: 'Switch page', desc: 'More buttons on other pages', make: () => ({ label: 'Page', color: '#1e293b', activeColor: '#e2e8f0', actions: [{ type: 'page', op: 'goto', page: 0 }] }) },
  { name: 'Switch speakers', desc: 'Next output device', make: () => ({ label: 'Output', color: '#14b8a6', actions: [{ type: 'audiodevice', target: 'output', op: 'next' }] }) },
  { name: 'Switch mic', desc: 'Next input device', make: () => ({ label: 'Input', color: '#f59e0b', actions: [{ type: 'audiodevice', target: 'input', op: 'next' }] }) },
  { name: 'Light show', desc: 'Start / next effect', make: () => ({ label: 'Light show', color: '#7c3aed', effect: 'pulse', actions: [{ type: 'lightshow', op: 'next' }] }) },
  { name: 'Light show brighter', desc: 'While a show runs', make: () => ({ label: 'Show brighter', color: '#a78bfa', actions: [{ type: 'lightshow', op: 'brighter' }] }) },
  { name: 'Light show dimmer', desc: 'While a show runs', make: () => ({ label: 'Show dimmer', color: '#3b2a6b', actions: [{ type: 'lightshow', op: 'dimmer' }] }) },
];

function renderInspector() {
  const el = $('#inspector');
  el.textContent = '';
  if (!S.cfg) return;
  const id = S.sel;
  if (id === 'page') { renderPageInspector(el); return; }

  if (id == null) {
    el.append(h('div', { class: 'insp-empty' },
      h('h2', { text: 'Pick a pad to set it up' }),
      h('p', { text: 'Every button on the Launchpad works here, including the top row and the right column.' }),
      h('ul', { class: 'legend-list' },
        h('li', {}, h('i', { class: 'k-grid' }), h('div', {}, h('b', { text: '64 grid pads. ' }), 'Shortcuts, apps, sounds, OBS. Turn any column into a volume fader.')),
        h('li', {}, h('i', { class: 'k-edge' }), h('div', {}, h('b', { text: 'Top row and right column. ' }), '16 more buttons. Set them for one page or every page.')),
        h('li', {}, h('i', { class: 'k-page' }), h('div', {}, h('b', { text: 'Pages. ' }), 'Each page is a whole new set of buttons. The right column switches pages by default.'))),
      h('p', { class: 'note', style: 'margin-top:24px' }, 'Shortcuts: Ctrl+C and Ctrl+V copy a pad, Delete clears it, Ctrl+Z undoes, arrow keys move around the grid.')));
    return;
  }

  el.append(h('div', { class: 'insp-where', text: where(id) }));

  if (id === 99) {
    el.append(h('h2', { class: 'insp-title', text: 'Logo light' }));
    el.append(h('div', { class: 'group' }, field('Colour', swatchPicker(S.cfg.settings.logoColor, (c, live) => { S.cfg.settings.logoColor = c; live ? renderDevice() : commit(); })),
      h('p', { class: 'note', text: 'The logo can’t be pressed. It blinks amber while actions are paused.' })));
    return;
  }

  const fa = faderAt(id);
  if (fa) { renderFader(el, fa.f); return; }

  const bd = binding(id);
  if (!bd) {
    const am = autoMuteFader(id);
    el.append(h('h2', { class: 'insp-title', text: am ? `Mute ${am.label || am.target}` : 'Empty' }));
    if (am) el.append(h('p', { class: 'group-note', text: 'The auto mixer uses this pad to mute the app below it. Pick something here to use the pad for something else instead.' }));
    const presets = h('div', { class: 'presets' }, PRESETS.map((p) => h('button', {
      class: 'preset', type: 'button',
      onclick: () => {
        if (p.pick) { p.pick(id); return; }
        const b = Object.assign(ensureBinding(id), p.make());
        b.offActions = b.offActions || [];
        if (b.actions.some((a) => a.type === 'page')) b.actions[0].page = pageIdx();
        commit();
      },
    }, h('b', { text: p.name }), h('span', { text: p.desc }))));
    el.append(h('div', { class: 'group' }, h('div', { class: 'group-head' }, h('h3', { text: 'Start from' })), presets));
    const extra = h('div', { class: 'footer-actions' },
      h('button', { class: 'btn', type: 'button', onclick: () => { ensureBinding(id); commit(); } }, icon('plus'), 'Blank button'));
    if (kindOf(id) === 'grid' && !curPage().autoMixer) {
      const col = id % 10, row = Math.floor(id / 10);
      extra.append(
        h('button', { class: 'btn', type: 'button', onclick: () => addFader(false, col) }, `Column ${col} → fader`),
        h('button', { class: 'btn', type: 'button', onclick: () => addFader(true, row) }, `Row ${9 - row} → fader`));
    }
    if (S.clip) extra.append(h('button', { class: 'btn', type: 'button', onclick: () => paste(id) }, icon('paste'), 'Paste'));
    el.append(extra);
    return;
  }

  const b = bd.b;
  S.editing = b;
  el.append(h('h2', { class: 'insp-title', text: b.label || 'Untitled button' }));

  // Where it applies
  const scopeGroup = h('div', { class: 'group' },
    field('Applies to', seg(bd.scope, [['page', `This page (${curPage().name})`], ['global', 'Every page']], (v) => moveScope(id, v), 'Applies to')));
  if (bd.overrides) scopeGroup.append(h('p', { class: 'note', text: 'This replaces an every-page button in the same spot.' }));
  el.append(scopeGroup);

  // Look
  const look = h('div', { class: 'group' }, h('div', { class: 'group-head' }, h('h3', { text: 'Look' })));
  look.append(field('Label (shown in the editor)', text(b.label, (v) => { b.label = v || undefined; el.querySelector('.insp-title').textContent = v || 'Untitled button'; renderDevice(); }, { maxlength: 24 })));
  look.append(field('Colour', swatchPicker(b.color, (c, live) => { b.color = c; live ? renderDevice() : commit(); })));
  if ((b.actions || []).some((a) => a.type === 'player') || b.color === 'player') {
    look.append(h('label', { class: 'kv' },
      h('span', {}, 'Colour of the music app that’s playing', h('small', { text: 'Green for Spotify, red for Apple Music, and so on.' })),
      h('input', { type: 'checkbox', checked: b.color === 'player', onchange: (e) => { b.color = e.target.checked ? 'player' : '#334155'; commit(); } })));
  }
  look.append(field('Colour when active', swatchPicker(b.activeColor || null, (c, live) => { b.activeColor = c || undefined; live ? renderDevice() : commit(); }, { allowAuto: true })));
  look.append(field('Animation', select_(b.effect || 'static', [['static', 'Solid'], ['flash', 'Blink'], ['pulse', 'Pulse'], ['flash-active', 'Blink while active'], ['pulse-active', 'Pulse while active']], (v) => { b.effect = v; commit({ rerender: false }); })));
  el.append(look);

  // Behaviour
  const beh = h('div', { class: 'group' }, h('div', { class: 'group-head' }, h('h3', { text: 'Behaviour' })));
  beh.append(seg(b.mode || 'press', [['press', 'Press'], ['toggle', 'Toggle'], ['hold', 'Hold']], (v) => { b.mode = v; commit(); }, 'Behaviour'));
  const modeNote = { press: 'Runs the actions every time you press.', toggle: 'Alternates between two sets of actions. Stays lit while on.', hold: 'Keys stay held down while your finger is on the pad. Good for push-to-talk.' }[b.mode || 'press'];
  beh.append(h('p', { class: 'note', text: modeNote }));
  const fbRow = h('div', { style: 'margin-top:12px' });
  fbRow.append(field('Light up when', select_(b.feedback || '', FEEDBACK, (v) => { b.feedback = v || undefined; if (!['app.mute', 'obs.scene', 'obs.mute'].includes(v)) b.feedbackArg = undefined; commit(); }),
    !b.feedback ? 'Automatic: toggles light while on, page buttons light while their page shows.' : null));
  if (b.feedback === 'app.mute') { needLists('audio'); fbRow.append(field('App', text(b.feedbackArg, (v) => { b.feedbackArg = v; }, { list: 'dl-audio', placeholder: 'e.g. discord' }))); }
  if (b.feedback === 'obs.scene') { needLists('obs'); fbRow.append(field('Scene', text(b.feedbackArg, (v) => { b.feedbackArg = v; }, { list: 'dl-scenes' }))); }
  if (b.feedback === 'obs.mute') { needLists('obs'); fbRow.append(field('Source', text(b.feedbackArg, (v) => { b.feedbackArg = v; }, { list: 'dl-inputs' }))); }
  if (b.feedback === 'audio.device') {
    needDevices();
    const all = S.devices ? [...S.devices.outputs.map((d) => [d.id, `Output: ${d.name}`]), ...S.devices.inputs.map((d) => [d.id, `Input: ${d.name}`])] : [];
    if (!all.length) all.push(['', 'Loading devices…']);
    if (!b.feedbackArg && all[0][0]) { b.feedbackArg = all[0][0]; setTimeout(() => commit({ rerender: false })); }
    fbRow.append(field('Device', select_(b.feedbackArg || '', all, (v) => { b.feedbackArg = v; commit({ rerender: false }); })));
  }
  beh.append(fbRow);
  el.append(beh);

  // Actions
  const mode = b.mode || 'press';
  el.append(actionList(b.actions, mode === 'toggle' ? 'When turned on' : mode === 'hold' ? 'While held' : 'When pressed',
    mode === 'hold' ? 'Key presses stay down until you let go. Other actions run once.' : null));
  if (mode === 'toggle') el.append(actionList(b.offActions, 'When turned off', 'Leave empty to run the same actions again.'));
  if (mode === 'hold') el.append(actionList(b.offActions, 'After letting go'));

  el.append(h('div', { class: 'footer-actions' },
    h('button', { class: 'btn primary', type: 'button', onclick: () => testPress(id) }, icon('play'), 'Try it'),
    h('button', { class: 'btn', type: 'button', onclick: () => { S.clip = clone(b); renderInspector(); setSave('Copied'); } }, icon('copy'), 'Copy'),
    S.clip ? h('button', { class: 'btn', type: 'button', onclick: () => paste(id) }, icon('paste'), 'Paste') : null));
  el.append(h('div', { class: 'footer-actions', style: 'border-top:0;padding-top:8px' },
    h('button', { class: 'btn', type: 'button', title: 'Wipe its settings but keep a blank button here', onclick: () => resetPad(id) }, icon('refresh'), 'Reset to blank'),
    h('button', { class: 'btn danger', type: 'button', title: bd.scope === 'global' ? 'Removes it from every page' : 'Removes it. The pad goes dark.', onclick: () => clearPad(id) }, icon('trash'), 'Delete mapping')));
  if (bd.overrides) el.append(h('p', { class: 'note', text: 'Deleting this brings back the every-page button underneath.' }));
}

function renderFader(el, f) {
  const auto = curPage().autoMixer;
  const where_ = f.horizontal ? `Row ${9 - f.row}` : `Column ${f.column}`;
  el.append(h('div', { class: 'insp-where', text: auto ? `Auto mixer · ${where_}` : `${where_} · volume fader` }));
  el.append(h('h2', { class: 'insp-title', text: auto ? f.label || f.target : `${where_} volume fader` }));
  const dir = f.horizontal ? 'The left pad sets 0% and the right pad sets 100%.' : 'The bottom pad sets 0% and the top pad sets 100%.';
  const g = h('div', { class: 'group' });
  g.append(h('p', { class: 'group-note', text: `${dir} Lit pads show the current level, and turn red while muted.` }));
  if (auto) {
    g.append(h('p', { class: 'note', text: `Controls ${f.target}. This page fills itself with whatever apps have sound open, so there's nothing to set up.` }));
    g.append(h('div', { class: 'footer-actions', style: 'border-top:0;padding-top:12px' },
      h('button', { class: 'btn primary', type: 'button', onclick: () => {
        S.clip = muteButtonFor({ label: f.label, value: f.target, color: f.color });
        setSave(`Mute button for ${f.label || f.target} copied. Click any pad, then Ctrl+V`);
      } }, icon('copy'), `Make a mute button for ${f.label || f.target}`),
      h('button', { class: 'btn', type: 'button', onclick: selectPage }, icon('edit'), 'Auto mixer settings')));
    g.append(h('p', { class: 'note', text: 'The mute button can go on any page, for example next to your other controls.' }));
    el.append(g);
    return;
  }
  needLists('audio');
  g.append(field('What to control', text(f.target, (v) => { f.target = v; renderDevice(); }, { list: 'dl-audio', placeholder: 'master, mic, focused or an app' }), 'Use the app’s process name, like spotify, discord or chrome.'));
  g.append(field('Colour', swatchPicker(f.color, (c, live) => { f.color = c; live ? renderDevice() : commit(); }, { allowOff: false })));
  el.append(g);
  const muteId = String(f.horizontal ? f.row * 10 + 9 : 90 + f.column);
  const footer = h('div', { class: 'footer-actions' });
  if (!curPage().buttons[muteId]) {
    footer.append(h('button', { class: 'btn', type: 'button', onclick: () => {
      curPage().buttons[muteId] = { label: 'Mute', color: '#52525b', activeColor: '#dc2626', effect: 'static', mode: 'press',
        feedback: f.target === 'master' ? 'master.mute' : f.target === 'mic' ? 'mic.mute' : 'app.mute', feedbackArg: f.target,
        actions: [{ type: 'volume', target: f.target, op: 'togglemute' }], offActions: [] };
      commit();
    } }, f.horizontal ? 'Add a mute button at the end of the row' : 'Add a mute button above it'));
  }
  footer.append(h('button', { class: 'btn danger', type: 'button', onclick: () => removeFader(f) }, icon('trash'), 'Remove fader'));
  el.append(footer);
}

function addFader(horizontal, n) {
  const p = curPage();
  const crossing = p.faders.filter((f) => !!f.horizontal !== horizontal);
  const covered = [1, 2, 3, 4, 5, 6, 7, 8].map((k) => String(horizontal ? n * 10 + k : k * 10 + n)).filter((k) => p.buttons[k]);
  const warn = [];
  if (crossing.length) warn.push(`the ${horizontal ? 'column' : 'row'} faders on this page (they'd cross)`);
  if (covered.length) warn.push(`${covered.length} button${covered.length > 1 ? 's' : ''} in that ${horizontal ? 'row' : 'column'}`);
  if (warn.length && !confirm(`This replaces ${warn.join(' and ')}. Ctrl+Z undoes it. Continue?`)) return;
  p.faders = p.faders.filter((f) => !!f.horizontal === horizontal);
  covered.forEach((k) => delete p.buttons[k]);
  p.faders.push(horizontal
    ? { horizontal: true, row: n, column: 1, target: 'master', color: '#22c55e' }
    : { horizontal: false, column: n, row: 1, target: 'master', color: '#22c55e' });
  commit();
}

function removeFader(f) {
  curPage().faders = curPage().faders.filter((x) => x !== f);
  commit();
}

function resetPad(id) {
  const key = String(id);
  const bd = binding(id);
  if (!bd) return;
  const blank = { color: '#3b82f6', effect: 'static', mode: 'press', actions: [], offActions: [] };
  if (bd.scope === 'page') curPage().buttons[key] = blank; else S.cfg.global[key] = blank;
  commit();
  setSave('Reset. Ctrl+Z to undo');
}

function moveScope(id, scope) {
  const key = String(id);
  const bd = binding(id);
  if (!bd || bd.scope === scope) return;
  if (scope === 'global') { S.cfg.global[key] = bd.b; delete curPage().buttons[key]; }
  else { curPage().buttons[key] = clone(bd.b); }
  commit();
}

function clearPad(id) {
  const key = String(id);
  const bd = binding(id);
  if (!bd) return;
  if (bd.scope === 'page') delete curPage().buttons[key]; else delete S.cfg.global[key];
  commit();
  setSave('Deleted. Ctrl+Z to undo');
}

function paste(id) {
  if (!S.clip || kindOf(id) === 'logo') return;
  const key = String(id);
  const bd = binding(id);
  const copy = clone(S.clip);
  if (bd && bd.scope === 'global') S.cfg.global[key] = copy; else curPage().buttons[key] = copy;
  commit();
}

// ------------------------------------------------------------------ drag and drop

/** Pads a button can be dropped on: anything but the logo and pads covered by a fader. */
const droppable = (id) => kindOf(id) !== 'logo' && !faderAt(id);

function dragStart(e, id) {
  const bd = binding(id);
  if (!bd || faderAt(id)) { e.preventDefault(); return; }
  S.drag = id;
  closeMenu();
  e.dataTransfer.effectAllowed = 'copyMove';
  e.dataTransfer.setData('text/plain', String(id));
  padEls[id].classList.add('dragging');
}

function dragOver(e, id) {
  if (S.drag == null || id === S.drag || !droppable(id)) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = e.ctrlKey ? 'copy' : 'move';
  const el = padEls[id];
  el.classList.add('drop-target');
  el.classList.toggle('drop-swap', !e.ctrlKey && !!binding(id));
}

function dragDrop(e, id) {
  e.preventDefault();
  const from = S.drag;
  dragEnd();
  if (from == null || from === id || !droppable(id)) return;
  movePad(from, id, e.ctrlKey);
}

function dragEnd() {
  S.drag = null;
  for (const el of Object.values(padEls)) el.classList.remove('dragging', 'drop-target', 'drop-swap');
}

/**
 * Move a button to another pad (swapping if that pad is taken), or copy it with Ctrl held.
 * A button stays in the scope it came from: this page, or every page.
 */
function movePad(from, to, copy) {
  const src = binding(from);
  if (!src) return;
  const dict = src.scope === 'page' ? curPage().buttons : S.cfg.global;
  const a = String(from), b = String(to);
  const there = dict[b];
  if (copy) dict[b] = clone(src.b);
  else {
    if (there) dict[a] = there; else delete dict[a];
    dict[b] = src.b;
  }
  S.sel = to;
  commit();
  const name = src.b.label ? `“${src.b.label}”` : 'Button';
  const hidden = src.scope === 'global' && curPage().buttons[b];
  setSave(hidden
    ? `${name} moved, but this page has its own button on that pad, so it shows on other pages only. Ctrl+Z to undo`
    : copy ? `${name} copied. Ctrl+Z to undo`
    : there ? `Swapped with “${there.label || 'button'}”. Ctrl+Z to undo` : `${name} moved. Ctrl+Z to undo`);
}

// ------------------------------------------------------------------ settings dialog

function openSettings() {
  renderSettings();
  const d = $('#settings');
  if (!d.open) d.showModal();
}

function renderSettings() {
  const s = S.cfg.settings;
  const body = $('#settings-body');
  body.textContent = '';

  const dev = h('div', { class: 'group' }, h('div', { class: 'group-head' }, h('h3', { text: 'Launchpad' })));
  dev.append(h('p', { class: 'group-note', text: S.status.connected ? `${S.status.device} is connected.` : S.status.status || 'Not connected.' }));
  const bright = h('input', { type: 'range', min: 5, max: 100, value: s.brightness, 'aria-label': 'LED brightness' });
  const brightOut = h('span', { class: 'mono', text: s.brightness + '%' });
  bright.addEventListener('input', () => { brightOut.textContent = bright.value + '%'; });
  bright.addEventListener('change', () => { s.brightness = +bright.value; commit({ rerender: false }); });
  dev.append(field('LED brightness', h('div', { class: 'field-row' }, bright, brightOut)));
  dev.append(h('label', { class: 'kv' }, h('span', {}, 'Flash pads while pressed', h('small', { text: 'A quick visual confirmation on the device.' })),
    h('input', { type: 'checkbox', checked: s.pressFlash, onchange: (e) => { s.pressFlash = e.target.checked; commit({ rerender: false }); } })));
  dev.append(h('div', {},
    h('button', { class: 'btn', type: 'button', onclick: () => api('/api/rescan', {}).catch(() => {}) }, icon('refresh'), 'Reconnect')));
  if (S.status.ports && S.status.ports.length) dev.append(h('p', { class: 'path', text: S.status.ports.join(' · ') }));
  body.append(dev);

  const obs = h('div', { class: 'group' }, h('div', { class: 'group-head' }, h('h3', { text: 'OBS Studio' })));
  obs.append(h('p', { class: 'group-note', text: 'In OBS, open Tools, then WebSocket Server Settings, and turn the server on. Copy the password from Show Connect Info.' }));
  obs.append(h('label', { class: 'kv' }, h('span', {}, 'Connect to OBS', h('small', { text: S.cfg.settings.obsEnabled ? (S.status.obsStatus || '') : 'Off' })),
    h('input', { type: 'checkbox', checked: s.obsEnabled, onchange: (e) => { s.obsEnabled = e.target.checked; commit({ rerender: false }); setTimeout(renderSettings, 400); } })));
  obs.append(field('Address', text(s.obsUrl, (v) => { s.obsUrl = v; }, { class: 'mono', placeholder: 'ws://127.0.0.1:4455' })));
  const pw = h('input', { type: 'password', value: s.obsPassword || '', autocomplete: 'off', 'aria-label': 'OBS WebSocket password' });
  pw.addEventListener('change', () => { s.obsPassword = pw.value || undefined; commit({ rerender: false }); });
  obs.append(field('Password', pw));
  body.append(obs);

  const show = h('div', { class: 'group' }, h('div', { class: 'group-head' }, h('h3', { text: 'Light show' })));
  show.append(h('p', { class: 'group-note', text: 'Start it from the bottom-right pad. The two pads above it make the show brighter or dimmer.' }));
  show.append(h('label', { class: 'kv' }, h('span', {}, 'Start by itself when the pads are idle', h('small', { text: 'Off by default. It never starts while this editor is open.' })),
    h('input', { type: 'checkbox', checked: !!s.idleAuto, onchange: (e) => { s.idleAuto = e.target.checked; if (s.idleAuto && !s.idleSeconds) s.idleSeconds = 120; commit({ rerender: false }); renderSettings(); } })));
  if (s.idleAuto) {
    show.append(field('Start after', select_(String(s.idleSeconds || 120), [['30', '30 seconds'], ['60', '1 minute'], ['120', '2 minutes'], ['300', '5 minutes'], ['600', '10 minutes'], ['1800', '30 minutes']],
      (v) => { s.idleSeconds = +v; commit({ rerender: false }); })));
  }
  const sb = h('input', { type: 'range', min: 10, max: 100, step: 5, value: s.showBrightness ?? 100, 'aria-label': 'Light show brightness' });
  const sbOut = h('span', { class: 'mono', text: (s.showBrightness ?? 100) + '%' });
  sb.addEventListener('input', () => { sbOut.textContent = sb.value + '%'; });
  sb.addEventListener('change', () => { s.showBrightness = +sb.value; commit({ rerender: false }); });
  show.append(field('Light show brightness', h('div', { class: 'field-row' }, sb, sbOut)));
  show.append(field('Effect', select_(s.idleEffect || 'cycle', [['cycle', 'Cycle through all (new one every 30 s)'], ...EFFECT_NAMES], (v) => { s.idleEffect = v; commit({ rerender: false }); })));
  show.append(h('label', { class: 'kv' }, h('span', {}, 'First press only wakes the pads', h('small', { text: 'So you don’t fire an action by accident when the lights are covering your layout.' })),
    h('input', { type: 'checkbox', checked: s.wakePressOnly !== false, onchange: (e) => { s.wakePressOnly = e.target.checked; commit({ rerender: false }); } })));
  show.append(h('div', { class: 'field-row', style: 'justify-content:flex-start' },
    h('button', { class: 'btn', type: 'button', onclick: () => api('/api/lightshow', { op: (s.idleEffect && s.idleEffect !== 'cycle') ? 'play:' + s.idleEffect : 'next' }).catch(() => {}) }, icon('play'), 'Play this effect'),
    h('button', { class: 'btn ghost', type: 'button', onclick: () => api('/api/lightshow', { op: 'next' }).catch(() => {}) }, 'Next effect'),
    h('button', { class: 'btn ghost', type: 'button', onclick: () => api('/api/lightshow', { op: 'stop' }).catch(() => {}) }, 'Stop')));
  show.append(h('p', { class: 'note', text: 'Any pad stops the show. The “Light show” preset puts a start / next-effect button on a pad.' }));
  body.append(show);

  const sys = h('div', { class: 'group' }, h('div', { class: 'group-head' }, h('h3', { text: 'Windows' })));
  sys.append(h('label', { class: 'kv' }, h('span', {}, 'Start with Windows', h('small', { text: 'Runs quietly in the tray. Closing this window doesn’t stop your pads.' })),
    h('input', { type: 'checkbox', checked: S.autostart, onchange: async (e) => {
      try { const r = await api('/api/autostart', { enabled: e.target.checked }); S.autostart = r.autostart; } catch (err) { setSave(err.message, 'error'); }
    } })));
  sys.append(field('Key tap length (ms)', num(s.tapMs, (v) => { s.tapMs = v ?? 20; }, { min: 1, max: 500 }), 'How long a shortcut is held. Raise it if a game misses quick taps.'));
  sys.append(h('p', { class: 'path', text: 'Config file: ' + S.configPath }));
  body.append(sys);
}

// ------------------------------------------------------------------ status + live events

function renderStatus() {
  const st = S.status;
  const chip = $('#pad-chip');
  chip.dataset.state = st.connected ? 'on' : 'warn';
  $('#pad-chip-text').textContent = st.connected ? st.device || 'Launchpad connected' : 'Launchpad not connected';
  const obs = $('#obs-chip');
  obs.hidden = !(S.cfg && S.cfg.settings.obsEnabled);
  obs.dataset.state = st.obsConnected ? 'on' : 'warn';
  $('#obs-chip-text').textContent = st.obsConnected ? 'OBS connected' : 'OBS offline';
  const showChip = $('#show-chip');
  showChip.hidden = !st.show;
  if (st.show) $('#show-chip-text').textContent = 'Light show: ' + ((EFFECT_NAMES.find((e) => e[0] === st.show) || [])[1] || st.show);
  const pause = $('#pause-btn');
  pause.setAttribute('aria-pressed', String(!!st.paused));
  pause.lastElementChild.textContent = st.paused ? 'Paused' : 'Pause';

  const banner = $('#banner');
  banner.textContent = '';
  if (S.offline) {
    banner.append(h('strong', { text: 'Lost contact with PadDeck.' }), ' It may have been closed. Reconnecting…');
  } else if (!st.connected && st.status) {
    banner.append(h('strong', { text: 'Launchpad not connected.' }), ' ' + st.status.replace(/^No Launchpad found\.\s*/, ''),
      h('button', { class: 'btn small', type: 'button', onclick: () => api('/api/rescan', {}).catch(() => {}) }, 'Try again'));
  }
  banner.hidden = banner.childNodes.length === 0;
  document.body.classList.toggle('has-banner', !banner.hidden);
}

function render() {
  if (!S.cfg) return;
  renderStatus();
  renderPages();
  renderDevice();
  renderInspector();
}

function connectEvents() {
  const es = new EventSource('/api/events');
  es.onopen = () => { if (S.offline) { S.offline = false; load(); } };
  es.onerror = () => { S.offline = true; renderStatus(); };
  es.onmessage = (e) => {
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    if (m.type === 'leds') { S.leds = m.c; renderDevice(); }
    else if (m.type === 'press') {
      flashPad(m.id, m.down);
      if (m.down && $('#follow-press').checked && S.sel !== m.id && !document.querySelector('dialog[open]')) select(m.id);
    } else if (m.type === 'status') {
      const pageChanged = S.cfg && m.status.page !== S.status.page;
      const mixerChanged = JSON.stringify(m.status.mixer) !== JSON.stringify(S.status.mixer);
      S.status = m.status;
      // The brightness pads on the Launchpad change this setting directly; keep our copy in step.
      if (S.cfg && m.status.showBrightness != null) S.cfg.settings.showBrightness = m.status.showBrightness;
      if (pageChanged) render();
      else {
        renderStatus();
        if (mixerChanged) { renderDevice(); if (typeof S.sel === 'number' && kindOf(S.sel) !== 'logo') renderInspector(); }
      }
    }
  };
}

async function load() {
  try {
    const st = await api('/api/state');
    S.cfg = st.config;
    S.status = st.status;
    S.autostart = st.autostart;
    S.configPath = st.configPath;
    S.lastSaved = JSON.stringify(S.cfg);
    render();
  } catch (e) {
    S.offline = true;
    renderStatus();
    setTimeout(load, 2000);
  }
}

// ------------------------------------------------------------------ wiring

document.addEventListener('keydown', (e) => {
  const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName);
  if (e.ctrlKey && e.key.toLowerCase() === 'z' && !typing) { e.preventDefault(); undo(); }
  if (typing || typeof S.sel !== 'number' || document.querySelector('dialog[open], .ctx')) return;
  if (e.ctrlKey && e.key.toLowerCase() === 'c') { const bd = binding(S.sel); if (bd) { S.clip = clone(bd.b); setSave('Copied'); renderInspector(); } }
  if (e.ctrlKey && e.key.toLowerCase() === 'v') paste(S.sel);
  if (e.key === 'Delete' && S.sel !== 99) clearPad(S.sel);
});

$('#settings-btn').addEventListener('click', openSettings);
$('#settings-close').addEventListener('click', () => $('#settings').close());
$('#settings').addEventListener('close', () => render());
$('#pause-btn').addEventListener('click', () => api('/api/pause', { paused: !S.status.paused }).catch(() => {}));
$('#show-chip').addEventListener('click', () => api('/api/lightshow', { op: 'stop' }).catch(() => {}));

$('#picker-close').addEventListener('click', () => $('#picker').close());

buildDevice();
load().then(() => {
  connectEvents();
  setTimeout(() => loadStartApps().catch(() => {}), 1500);   // warm the app picker so it opens instantly
});
