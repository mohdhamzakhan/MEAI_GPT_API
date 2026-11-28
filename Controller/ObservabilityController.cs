// Controller/ObservabilityController.cs
//
//   GET /api/observability/dashboard   HTML page (no data in it; asks for a bearer token)
//   GET /api/observability/summary     outcomes, latency, per-stage timings        [Authorize]
//   GET /api/observability/recent      last N requests with stage-by-stage detail  [Authorize]
//                                      ?take=50&outcome=declined
//
// The dashboard page itself is anonymous so it can load, but every number on it comes from the
// [Authorize]d endpoints; paste a JWT into the box at the top (kept in sessionStorage only).

using MEAI_GPT_API.Observability;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MEAI_GPT_API.Controller
{
    [ApiController]
    [Route("api/observability")]
    public class ObservabilityController : ControllerBase
    {
        private readonly RagTraceStore _store;

        public ObservabilityController(RagTraceStore store) => _store = store;

        [HttpGet("summary")]
        [Authorize]
        public IActionResult Summary() => Ok(_store.Summary());

        [HttpGet("recent")]
        [Authorize]
        public IActionResult Recent([FromQuery] int take = 50, [FromQuery] string? outcome = null) =>
            Ok(_store.Recent(take, outcome));

        [HttpGet("dashboard")]
        [AllowAnonymous]
        public ContentResult Dashboard() => Content(Html, "text/html; charset=utf-8");

        private const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>MEAI RAG observability</title>
<style>
  :root { --bg:#fff; --fg:#1a1a1a; --mut:#666; --card:#f4f4f6; --line:#ddd; --ok:#1e8449; --bad:#c0392b; --warn:#b9770e; }
  @media (prefers-color-scheme: dark) {
    :root { --bg:#121214; --fg:#eee; --mut:#999; --card:#1c1c20; --line:#333; --ok:#4cd08a; --bad:#ff6b5b; --warn:#e6b450; }
  }
  * { box-sizing: border-box; }
  body { margin:0; padding:12px; background:var(--bg); color:var(--fg); font:14px/1.4 system-ui, sans-serif; }
  h1 { font-size:18px; margin:0 0 10px; }
  h2 { font-size:14px; margin:18px 0 6px; color:var(--mut); text-transform:uppercase; letter-spacing:.04em; }
  .row { display:flex; gap:8px; flex-wrap:wrap; }
  input, button, select { font:inherit; padding:6px 8px; border:1px solid var(--line); border-radius:6px; background:var(--card); color:var(--fg); }
  input { flex:1; min-width:180px; }
  .cards { display:grid; grid-template-columns:repeat(auto-fit,minmax(120px,1fr)); gap:8px; }
  .card { background:var(--card); border-radius:8px; padding:10px; }
  .card b { display:block; font-size:20px; }
  .card span { color:var(--mut); font-size:12px; }
  .scroll { overflow-x:auto; }
  table { border-collapse:collapse; width:100%; }
  th, td { text-align:left; padding:5px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
  th { color:var(--mut); font-weight:600; font-size:12px; }
  .answered { color:var(--ok); } .declined, .error, .aborted { color:var(--bad); }
  .clarified, .cache, .smalltalk { color:var(--warn); }
  details summary { cursor:pointer; }
  .stage { font-size:12px; color:var(--mut); margin:2px 0; }
  .zero { color:var(--bad); font-weight:600; }
  #msg { color:var(--bad); margin:6px 0; }
</style>
</head>
<body>
<h1>MEAI RAG observability</h1>
<div class="row">
  <input id="token" type="password" placeholder="Bearer token (JWT)">
  <button onclick="saveToken()">Load</button>
  <select id="filter" onchange="load()">
    <option value="">all outcomes</option><option>answered</option><option>declined</option>
    <option>clarified</option><option>cache</option><option>smalltalk</option><option>error</option><option>aborted</option>
  </select>
</div>
<div id="msg"></div>

<h2>Overview</h2>
<div class="cards" id="cards"></div>

<h2>Stage latency</h2>
<div class="scroll"><table id="stages"></table></div>

<h2>Recent questions</h2>
<div class="scroll"><table id="recent"></table></div>

<script>
const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
$('token').value = sessionStorage.getItem('rag_token') || '';

function saveToken() { sessionStorage.setItem('rag_token', $('token').value.trim()); load(); }

async function api(path) {
  const r = await fetch(path, { headers: { Authorization: 'Bearer ' + ($('token').value.trim()) } });
  if (r.status === 401) throw new Error('401: token missing or expired');
  if (!r.ok) throw new Error(path + ' -> ' + r.status);
  return r.json();
}

async function load() {
  try {
    $('msg').textContent = '';
    const [s, recent] = await Promise.all([
      api('/api/observability/summary'),
      api('/api/observability/recent?take=40&outcome=' + encodeURIComponent($('filter').value))
    ]);

    const o = s.outcomes || {};
    const card = (v, l) => `<div class="card"><b>${esc(v ?? '–')}</b><span>${esc(l)}</span></div>`;
    $('cards').innerHTML =
      card(s.total, 'requests held') + card(s.lastHour, 'last hour') +
      card(o.answered || 0, 'answered') + card(o.declined || 0, 'declined') +
      card(o.clarified || 0, 'clarified') + card(s.latencyMs.p50 + ' ms', 'p50 latency') +
      card(s.latencyMs.p95 + ' ms', 'p95 latency') +
      card(s.answeredWithCentralizedChunks == null ? '–' : s.answeredWithCentralizedChunks + '%', 'answers using Centralized chunks') +
      card(s.avgFinalChunks, 'avg chunks to generation');

    $('stages').innerHTML = '<tr><th>stage</th><th>runs</th><th>avg ms</th><th>p95 ms</th></tr>' +
      (s.stages || []).map(x => `<tr><td>${esc(x.stage)}</td><td>${x.count}</td><td>${x.avgMs}</td><td>${x.p95Ms}</td></tr>`).join('');

    $('recent').innerHTML = '<tr><th>time</th><th>outcome</th><th>ms</th><th>question</th></tr>' +
      recent.map(t => {
        const detail = (t.stages || []).map(st => st.chunks != null
          ? `<div class="stage">${esc(st.name)}: ${st.chunks} chunks, <span class="${st.centralized ? '' : 'zero'}">centralized ${st.centralized}</span> — ${esc((st.sources||[]).join(', '))}</div>`
          : `<div class="stage">${esc(st.name)}: ${st.ms} ms</div>`).join('');
        const tags = Object.entries(t.tags || {}).map(([k, v]) => `${esc(k)}=${esc(v)}`).join(' · ');
        return `<tr><td>${new Date(t.at).toLocaleTimeString()}</td>
          <td class="${esc(t.outcome)}">${esc(t.outcome)}${t.confidence != null ? ' ' + Math.round(t.confidence * (t.confidence <= 1 ? 100 : 1)) + '%' : ''}</td>
          <td>${Math.round(t.totalMs)}</td>
          <td><details><summary>${esc(t.question)}</summary>
            <div class="stage">${esc(t.plant)} · ${esc(t.userId)} · trace ${esc(t.traceId || t.id)}</div>
            ${tags ? `<div class="stage">${tags}</div>` : ''}${t.note ? `<div class="stage">${esc(t.note)}</div>` : ''}${detail}</details></td></tr>`;
      }).join('');
  } catch (e) { $('msg').textContent = e.message; }
}

if ($('token').value) load();
setInterval(() => { if ($('token').value) load(); }, 15000);
</script>
</body>
</html>
""";
    }
}