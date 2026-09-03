/// <summary>Embedded Mate-owned technical chat page (no external CDN).</summary>
public static class MateTechnicalChatPage
{
    public const string Html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8""/>
<meta name=""viewport"" content=""width=device-width, initial-scale=1""/>
<title>Mate Technical Chat</title>
<style>
:root {
  --bg:#12141a; --panel:#1a1e27; --user:#2a4a6e; --assistant:#232833;
  --code:#0d1117; --text:#e8eaed; --muted:#9aa0a6; --accent:#7aa2f7;
  --border:#2f3643; --danger:#f07178; --font:Segoe UI,system-ui,sans-serif;
  --codefont:Cascadia Code,Consolas,ui-monospace,monospace;
  --fs:14px; --cfs:13px; --gap:12px; --maxw:860px;
}
* { box-sizing:border-box; }
html,body { height:100%; margin:0; background:var(--bg); color:var(--text); font:var(--fs)/1.45 var(--font); }
#app { display:flex; flex-direction:column; height:100%; max-width:var(--maxw); margin:0 auto; }
header { padding:10px 14px; border-bottom:1px solid var(--border); display:flex; align-items:center; gap:10px; }
header h1 { font-size:15px; margin:0; font-weight:600; letter-spacing:.02em; }
header .status { color:var(--muted); font-size:12px; margin-left:auto; }
#log { flex:1; overflow:auto; padding:14px; display:flex; flex-direction:column; gap:var(--gap); }
.msg { border:1px solid var(--border); border-radius:10px; padding:10px 12px; background:var(--panel); max-width:100%; }
.msg.user { background:var(--user); align-self:flex-end; }
.msg.assistant { background:var(--assistant); align-self:stretch; }
.msg .meta { font-size:11px; color:var(--muted); margin-bottom:6px; display:flex; gap:8px; }
.msg.failed { border-color:var(--danger); }
.msg.cancelled { opacity:.85; }
.md-p { margin:0 0 .6em; }
.md-p:last-child { margin-bottom:0; }
.md-h { margin:.4em 0 .35em; font-weight:650; }
.md-ul,.md-ol { margin:.35em 0 .6em 1.2em; }
.md-quote { margin:.4em 0; padding:.35em .7em; border-left:3px solid var(--accent); color:var(--muted); }
.md-inline, .codeblock code { font-family:var(--codefont); font-size:var(--cfs); }
.md-inline { background:#0006; padding:1px 5px; border-radius:4px; }
.md-link { color:var(--accent); text-decoration:underline; cursor:default; }
.codeblock { margin:.55em 0; background:var(--code); border:1px solid var(--border); border-radius:8px; overflow:hidden; }
.codeblock .bar { display:flex; align-items:center; gap:8px; padding:6px 8px; border-bottom:1px solid var(--border); font-size:11px; color:var(--muted); }
.codeblock pre { margin:0; padding:10px 12px; overflow:auto; white-space:pre; }
.codeblock code { white-space:pre; }
.codeblock button { margin-left:auto; background:transparent; color:var(--accent); border:1px solid var(--border); border-radius:6px; padding:3px 8px; cursor:pointer; font:inherit; }
.codeblock button:hover { border-color:var(--accent); }
.seg-tool { font-size:12px; color:var(--muted); border:1px dashed var(--border); padding:6px 8px; border-radius:6px; }
.pending { color:var(--muted); font-style:italic; }
#composer { border-top:1px solid var(--border); padding:10px 12px; display:flex; gap:8px; background:var(--panel); }
#composer textarea { flex:1; min-height:64px; max-height:180px; resize:vertical; background:#0e1117; color:var(--text); border:1px solid var(--border); border-radius:8px; padding:8px 10px; font:inherit; }
#composer button { border:1px solid var(--border); background:#1f2a3d; color:var(--text); border-radius:8px; padding:0 14px; cursor:pointer; font:inherit; }
#composer button#cancel { background:#3a2226; color:#ffc9c9; }
#composer button:disabled { opacity:.45; cursor:default; }
#voice {
  border-top:1px solid var(--border); padding:8px 12px; background:#161a22;
  display:flex; flex-wrap:wrap; gap:8px 12px; align-items:center; font-size:12px; color:var(--muted);
}
#voice label { display:flex; align-items:center; gap:6px; }
#voice select, #voice button {
  background:#0e1117; color:var(--text); border:1px solid var(--border); border-radius:6px;
  padding:4px 8px; font:inherit; cursor:pointer;
}
#voice button:hover { border-color:var(--accent); }
#voice .vstatus { margin-left:auto; }
</style>
</head>
<body>
<div id=""app"">
  <header>
    <h1>Mate Technical Chat</h1>
    <span class=""status"" id=""status"">connecting…</span>
  </header>
  <div id=""log""></div>
  <div id=""voice"">
    <label>Speech <input type=""checkbox"" id=""speechEnabled""/></label>
    <span>Provider: <strong id=""speechProvider"">kokoro</strong></span>
    <span class=""vstatus"" id=""speechStatus"">Unknown</span>
    <label>Voice
      <select id=""speechVoice""></select>
    </label>
    <button type=""button"" id=""speechTest"">Test Voice</button>
  </div>
  <div id=""composer"">
    <textarea id=""input"" placeholder=""Message Mate…  Enter send · Shift+Enter newline""></textarea>
    <button id=""cancel"" type=""button"" disabled>Cancel</button>
    <button id=""send"" type=""button"">Send</button>
  </div>
</div>
<script>
const log = document.getElementById('log');
const input = document.getElementById('input');
const sendBtn = document.getElementById('send');
const cancelBtn = document.getElementById('cancel');
const statusEl = document.getElementById('status');
const speechEnabled = document.getElementById('speechEnabled');
const speechProvider = document.getElementById('speechProvider');
const speechStatus = document.getElementById('speechStatus');
const speechVoice = document.getElementById('speechVoice');
const speechTest = document.getElementById('speechTest');
let hasRunning = false;
let speechApplyBusy = false;

function applyTheme(t){
  if(!t) return;
  const r = document.documentElement.style;
  if(t.background) r.setProperty('--bg', t.background);
  if(t.panel) r.setProperty('--panel', t.panel);
  if(t.userBubble) r.setProperty('--user', t.userBubble);
  if(t.assistantBubble) r.setProperty('--assistant', t.assistantBubble);
  if(t.codeBackground) r.setProperty('--code', t.codeBackground);
  if(t.text) r.setProperty('--text', t.text);
  if(t.muted) r.setProperty('--muted', t.muted);
  if(t.accent) r.setProperty('--accent', t.accent);
  if(t.border) r.setProperty('--border', t.border);
  if(t.danger) r.setProperty('--danger', t.danger);
  if(t.fontFamily) r.setProperty('--font', t.fontFamily);
  if(t.codeFontFamily) r.setProperty('--codefont', t.codeFontFamily);
  if(t.fontSizePx) r.setProperty('--fs', t.fontSizePx+'px');
  if(t.codeFontSizePx) r.setProperty('--cfs', t.codeFontSizePx+'px');
  if(t.messageSpacingPx) r.setProperty('--gap', t.messageSpacingPx+'px');
  if(t.maxContentWidthPx) r.setProperty('--maxw', t.maxContentWidthPx+'px');
}

function esc(s){
  return String(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}

function renderSegments(segs){
  if(!segs || !segs.length) return '';
  let html = '';
  for(const s of segs){
    if(s.kind === 'Reasoning' || s.kind === 'Control') continue;
    if(s.kind === 'CodeBlock'){
      const lang = s.language ? esc(s.language) : 'code';
      const body = s.text || '';
      html += `<div class=""codeblock""><div class=""bar""><span>${lang}</span><button type=""button"" data-copy=""1"">Copy</button></div><pre><code></code></pre></div>`;
      // fill code via textContent after insert
    } else if(s.kind === 'Tool'){
      html += s.safeHtml || `<div class=""seg-tool"">${esc(s.text||'')}</div>`;
    } else {
      html += s.safeHtml || `<p class=""md-p"">${esc(s.text||'')}</p>`;
    }
  }
  return html;
}

function fillCodeBlocks(root, segs){
  if(!segs) return;
  const blocks = root.querySelectorAll('.codeblock');
  let bi = 0;
  for(const s of segs){
    if(s.kind !== 'CodeBlock') continue;
    const el = blocks[bi++];
    if(!el) break;
    const code = el.querySelector('code');
    code.textContent = s.text || '';
    const btn = el.querySelector('button[data-copy]');
    btn.onclick = async () => {
      try { await navigator.clipboard.writeText(s.text || ''); btn.textContent = 'Copied'; setTimeout(()=>btn.textContent='Copy', 1200); }
      catch { btn.textContent = 'Copy failed'; }
    };
  }
}

function render(state){
  applyTheme(state.theme);
  hasRunning = !!state.hasRunning;
  statusEl.textContent = hasRunning ? 'running…' : 'ready';
  sendBtn.disabled = hasRunning;
  cancelBtn.disabled = !hasRunning;
  const stick = log.scrollTop + log.clientHeight >= log.scrollHeight - 40;
  log.innerHTML = '';
  for(const e of (state.entries||[])){
    const div = document.createElement('div');
    div.className = 'msg ' + (e.speaker||'').toLowerCase();
    if(e.state === 'Failed') div.classList.add('failed');
    if(e.state === 'Cancelled') div.classList.add('cancelled');
    const meta = document.createElement('div');
    meta.className = 'meta';
    meta.innerHTML = `<span>${esc(e.speaker)}</span><span>${esc(e.state)}</span>`;
    div.appendChild(meta);
    const body = document.createElement('div');
    if(e.speaker === 'Assistant'){
      if(e.state === 'Running' || e.state === 'Pending'){
        if(e.segments && e.segments.length){
          body.innerHTML = renderSegments(e.segments);
          fillCodeBlocks(body, e.segments);
        } else {
          body.innerHTML = `<div class=""pending"">Waiting for response…</div>`;
        }
      } else if(e.state === 'Failed'){
        let failHtml = `<div class=""pending"" style=""color:var(--danger)"">${esc(e.failureMessage||'Failed')}</div>`;
        if(e.segments && e.segments.length){
          body.innerHTML = renderSegments(e.segments) + failHtml;
          fillCodeBlocks(body, e.segments);
        } else if(e.plainText && e.plainText !== e.failureMessage){
          body.textContent = e.plainText;
          const note = document.createElement('div');
          note.className = 'pending';
          note.style.color = 'var(--danger)';
          note.textContent = e.failureMessage || 'Failed';
          body.appendChild(note);
        } else {
          body.innerHTML = failHtml;
        }
      } else if(e.state === 'Cancelled'){
        if(e.segments && e.segments.length){
          body.innerHTML = renderSegments(e.segments) + `<div class=""pending"">Cancelled</div>`;
          fillCodeBlocks(body, e.segments);
        } else if(e.plainText){
          body.textContent = e.plainText;
          const note = document.createElement('div');
          note.className = 'pending';
          note.textContent = 'Cancelled';
          body.appendChild(note);
        } else {
          body.innerHTML = `<div class=""pending"">Cancelled</div>`;
        }
      } else {
        body.innerHTML = renderSegments(e.segments);
        fillCodeBlocks(body, e.segments);
      }
    } else {
      body.textContent = e.plainText || '';
    }
    div.appendChild(body);
    log.appendChild(div);
  }
  if(stick) log.scrollTop = log.scrollHeight;
}

async function refresh(){
  const r = await fetch('/api/state');
  const j = await r.json();
  render(j);
}

function connectSse(){
  try {
    const es = new EventSource('/api/events');
    es.addEventListener('state', ev => {
      try { render(JSON.parse(ev.data)); } catch {}
    });
    es.onerror = () => { statusEl.textContent = 'reconnecting…'; };
  } catch {
    setInterval(refresh, 1000);
  }
}

async function send(){
  const text = input.value;
  if(!text || !text.trim()) return;
  if(hasRunning) return;
  sendBtn.disabled = true;
  statusEl.textContent = 'sending…';
  try {
    const r = await fetch('/api/send', {
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body: JSON.stringify({ text })
    });
    let body = null;
    try { body = await r.json(); } catch {}
    if(!r.ok || !body || body.ok !== true){
      const err = (body && body.error) ? body.error : ('HTTP ' + r.status);
      statusEl.textContent = 'send failed: ' + err;
      // Preserve typed text — host did not accept the Send.
      return;
    }
    input.value = '';
    await refresh();
  } catch (e) {
    statusEl.textContent = 'send failed';
    // Preserve typed text on network/host failure.
  } finally {
    if(!hasRunning) sendBtn.disabled = false;
  }
}

async function cancel(){
  try {
    await fetch('/api/cancel', { method:'POST' });
  } catch {}
  await refresh();
}

sendBtn.onclick = send;
cancelBtn.onclick = cancel;
input.addEventListener('keydown', e => {
  if(e.key === 'Enter' && !e.shiftKey){
    e.preventDefault();
    send();
  }
});

refresh().then(connectSse);
refreshSpeech();
setInterval(refreshSpeech, 4000);

async function refreshSpeech(){
  try {
    const r = await fetch('/api/speech/state');
    const j = await r.json();
    renderSpeech(j);
  } catch {
    speechStatus.textContent = 'Unavailable';
  }
}

function renderSpeech(s){
  if(!s) return;
  speechApplyBusy = true;
  speechEnabled.checked = !!s.speechOutputEnabled;
  speechProvider.textContent = s.providerId || 'kokoro';
  speechStatus.textContent = s.status || 'Unknown';
  if(s.lastError) speechStatus.textContent = (s.status || 'Unavailable') + ' — ' + s.lastError;
  const voices = s.voices || [];
  const cur = s.selectedVoice || '';
  const prev = speechVoice.value;
  speechVoice.innerHTML = '';
  if(!voices.length && cur){
    const opt = document.createElement('option');
    opt.value = cur; opt.textContent = cur; speechVoice.appendChild(opt);
  }
  for(const v of voices){
    const opt = document.createElement('option');
    opt.value = v.id; opt.textContent = v.name || v.id;
    speechVoice.appendChild(opt);
  }
  if(cur) speechVoice.value = cur;
  else if(prev) speechVoice.value = prev;
  speechApplyBusy = false;
}

async function applySpeech(patch){
  try {
    const r = await fetch('/api/speech/config', {
      method:'POST',
      headers:{'Content-Type':'application/json'},
      body: JSON.stringify(patch)
    });
    const j = await r.json();
    if(j && j.speech) renderSpeech(j.speech);
    else await refreshSpeech();
  } catch {
    speechStatus.textContent = 'Unavailable';
  }
}

speechEnabled.onchange = () => {
  if(speechApplyBusy) return;
  applySpeech({ speechOutputEnabled: !!speechEnabled.checked });
};
speechVoice.onchange = () => {
  if(speechApplyBusy) return;
  applySpeech({ selectedVoice: speechVoice.value });
};
speechTest.onclick = async () => {
  try { await fetch('/api/speech/test', { method:'POST' }); } catch {}
};
</script>
</body>
</html>";
}
