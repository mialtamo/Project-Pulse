using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using ProjectPulse.Processor.Services;

namespace ProjectPulse.Processor.Functions;

public sealed class RequestHistoryFunction(IRequestHistoryStore historyStore)
{
    [Function("RequestHistoryList")]
    public async Task<HttpResponseData> ListAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "requests")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var limit = 100;
        var items = await historyStore.ListAsync(limit, cancellationToken);
        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(items), Encoding.UTF8);
        return response;
    }

    [Function("RequestHistoryDetail")]
    public async Task<HttpResponseData> DetailAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "requests/{id}")]
        HttpRequestData request,
        string id,
        CancellationToken cancellationToken)
    {
        var item = await historyStore.GetAsync(id, cancellationToken);
        if (item is null)
        {
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(item), Encoding.UTF8);
        return response;
    }

    [Function("RequestHistoryPage")]
    public async Task<HttpResponseData> PageAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "requests.html")]
        HttpRequestData request)
    {
        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        response.Headers.Add("X-Frame-Options", "DENY");
        response.Headers.Add(
            "Content-Security-Policy",
            "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'");

        const string html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Project Pulse Requests</title>
<style>
:root{
  color-scheme:dark;
  --bg:#05070b;--panel:#0b111a;--panel2:#0f1722;--border:#1c2a3a;
  --text:#e5eefc;--muted:#8295ad;--cyan:#19ddff;--green:#54e39b;
  --amber:#ffc857;--red:#ff627d;--blue:#68a4ff;--purple:#b58cff
}
*{box-sizing:border-box}
body{margin:0;background:linear-gradient(180deg,#05070b,#07101a 55%,#05070b);color:var(--text);font-family:Inter,Segoe UI,system-ui,sans-serif}
header{position:sticky;top:0;z-index:10;padding:18px 24px;border-bottom:1px solid var(--border);background:rgba(5,7,11,.92);backdrop-filter:blur(10px);display:flex;justify-content:space-between;align-items:center}
h1{margin:0;font-size:19px;letter-spacing:.08em}.sub{color:var(--muted);font-size:12px;margin-top:4px}
a{color:var(--cyan);text-decoration:none}
main{padding:20px 24px 40px;max-width:1800px;margin:0 auto}
.toolbar{display:grid;grid-template-columns:minmax(260px,1fr) auto auto auto;gap:10px;align-items:center;margin-bottom:14px}
input,select,button{background:#0b1520;border:1px solid #24405a;color:var(--text);padding:9px 11px;border-radius:7px}
button{cursor:pointer}.muted{color:var(--muted)}
.summary{display:flex;gap:10px;flex-wrap:wrap;margin:0 0 14px}
.pill{padding:5px 9px;border:1px solid var(--border);border-radius:999px;background:#09111a;font-size:12px;color:var(--muted)}
.table-wrap{border:1px solid var(--border);border-radius:10px;overflow:auto;background:var(--panel);box-shadow:0 18px 60px rgba(0,0,0,.25)}
table{width:100%;border-collapse:collapse;min-width:1150px}
th,td{padding:10px 12px;border-bottom:1px solid var(--border);font-size:12px;vertical-align:top}
th{position:sticky;top:0;background:#0c1621;color:#9fb4cf;text-align:left;white-space:nowrap;cursor:pointer;user-select:none}
th:hover{color:#dceaff}
tr.data-row{cursor:pointer}
tr.data-row:hover{background:#0f1a27}
td.id{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;max-width:260px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.tag{display:inline-block;padding:2px 7px;border-radius:999px;font-size:11px;border:1px solid #29445f;white-space:nowrap}
.tag.APIM{color:var(--green)}.tag.ServiceBus{color:var(--amber)}
.status-Completed{color:var(--green)}.status-Buffered{color:var(--amber)}.status-Alerted,.status-WritebackFailed{color:var(--red)}
.expand-row{display:none;background:#07101a}.expand-row.open{display:table-row}
.expand-cell{padding:16px}
.detail-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:10px;margin-bottom:14px}
.card{border:1px solid var(--border);padding:10px;background:#08111a;border-radius:7px}
.label{font-size:10px;color:var(--muted);text-transform:uppercase;letter-spacing:.12em;margin-bottom:4px}
.payload-grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}
.payload-panel{min-width:0}
.payload-title{font-size:12px;font-weight:700;margin:0 0 7px;color:#c7d9f2}
pre{margin:0;white-space:pre-wrap;word-break:break-word;background:#020407;border:1px solid var(--border);padding:12px;border-radius:7px;max-height:420px;overflow:auto;font:12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace}
.json-key{color:var(--blue)}.json-string{color:var(--green)}.json-number{color:var(--amber)}.json-boolean{color:var(--purple)}.json-null{color:var(--red)}
.payload-note{color:var(--amber);font-size:12px;padding:10px;border:1px dashed #6d5728;border-radius:7px}
.empty{padding:28px;text-align:center;color:var(--muted)}
@media(max-width:900px){.toolbar{grid-template-columns:1fr 1fr}.payload-grid{grid-template-columns:1fr}}
</style>
</head>
<body>
<header>
  <div><h1>PROJECT PULSE REQUESTS</h1><div class="sub">Recent APIM and Service Bus activity</div></div>
  <a href="/">Home</a>
</header>
<main>
  <div class="toolbar">
    <input id="search" type="search" placeholder="Search ID, status, reason, code..." oninput="render()">
    <select id="destination" onchange="render()">
      <option value="">All destinations</option>
      <option value="APIM">APIM</option>
      <option value="ServiceBus">Service Bus</option>
    </select>
    <button onclick="load()">Refresh</button>
    <span id="status" class="muted"></span>
  </div>
  <div id="summary" class="summary"></div>
  <div class="table-wrap">
    <table>
      <thead><tr>
        <th data-key="UpdatedAtUtc">Updated ↕</th>
        <th data-key="ClaimCount">Claims ↕</th>
        <th data-key="UniqueId">Unique ID ↕</th>
        <th data-key="Status">Status ↕</th>
        <th data-key="Destination">Destination ↕</th>
        <th data-key="HttpStatusCode">HTTP ↕</th>
        <th data-key="BusinessResponseCode">Business Code ↕</th>
        <th data-key="Reason">Reason ↕</th>
      </tr></thead>
      <tbody id="rows"></tbody>
    </table>
  </div>
</main>
<script>
let items=[]; let sortKey='UpdatedAtUtc'; let sortDir='desc'; const detailCache=new Map();

function groupByBatch(source){
  const groups=new Map();
  for(const item of source){
    const key=item.BatchId||item.RecordId;
    if(!groups.has(key))groups.set(key,[]);
    groups.get(key).push(item);
  }

  return Array.from(groups.values()).map(group=>{
    const representative=group.slice().sort((a,b)=>Date.parse(b.UpdatedAtUtc||0)-Date.parse(a.UpdatedAtUtc||0))[0];
    const uniqueIds=[...new Set(group.map(x=>x.UniqueId).filter(Boolean))];
    return {
      ...representative,
      ClaimCount:group.length,
      UniqueId:uniqueIds.length===1?uniqueIds[0]:uniqueIds.join(', '),
      RecordId:representative.RecordId
    };
  });
}
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function syntaxHighlight(value){
  if(value===null||value===undefined||value==='') return '<span class="muted">No payload recorded</span>';
  let text=String(value);
  try{text=JSON.stringify(JSON.parse(text),null,2)}catch{}
  return esc(text).replace(/(&quot;.*?&quot;)(\s*:)?|\b(true|false)\b|\b(null)\b|-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b/g,(m,str,colon,bool,nul)=>{
    if(str) return '<span class="'+(colon?'json-key':'json-string')+'">'+str+'</span>'+(colon||'');
    if(bool) return '<span class="json-boolean">'+bool+'</span>';
    if(nul) return '<span class="json-null">'+nul+'</span>';
    return '<span class="json-number">'+m+'</span>';
  });
}
function compare(a,b){
  let av=a?.[sortKey], bv=b?.[sortKey];
  if(sortKey==='UpdatedAtUtc'){av=Date.parse(av||0);bv=Date.parse(bv||0)}
  else if(sortKey==='HttpStatusCode'||sortKey==='ClaimCount'){av=Number(av??-1);bv=Number(bv??-1)}
  else{av=String(av??'').toLowerCase();bv=String(bv??'').toLowerCase()}
  if(av<bv)return sortDir==='asc'?-1:1;if(av>bv)return sortDir==='asc'?1:-1;return 0;
}
function filtered(){
  const q=document.getElementById('search').value.trim().toLowerCase();
  const dest=document.getElementById('destination').value;
  return groupByBatch(items).filter(x=>(!dest||x.Destination===dest)&&(!q||[x.UniqueId,x.Status,x.Destination,x.HttpStatusCode,x.BusinessResponseCode,x.Reason,x.BatchId,x.ClaimCount].some(v=>String(v??'').toLowerCase().includes(q)))).sort(compare);
}
function renderSummary(list){
  const apim=list.filter(x=>x.Destination==='APIM').length, sb=list.filter(x=>x.Destination==='ServiceBus').length;
  const completed=list.filter(x=>x.Status==='Completed').length, buffered=list.filter(x=>x.Status==='Buffered').length;
  document.getElementById('summary').innerHTML=
    '<span class="pill">'+list.length+' batch(es) visible</span>'+
    '<span class="pill">'+apim+' APIM</span>'+
    '<span class="pill">'+sb+' Service Bus</span>'+
    '<span class="pill">'+completed+' completed</span>'+
    '<span class="pill">'+buffered+' buffered</span>';
}
function render(){
  const list=filtered(); renderSummary(list);
  const tbody=document.getElementById('rows');
  if(!list.length){tbody.innerHTML='<tr><td colspan="8" class="empty">No matching requests</td></tr>';return}
  tbody.innerHTML=list.map((x,i)=>{
    const key='row-'+i+'-'+esc(x.RecordId);
    return '<tr class="data-row" onclick="toggleDetail(\''+key+'\',\''+esc(x.RecordId)+'\')">'+
      '<td>'+new Date(x.UpdatedAtUtc).toLocaleString()+'</td>'+
      '<td>'+esc(x.ClaimCount)+'</td>'+
      '<td class="id" title="'+esc(x.UniqueId)+'">'+esc(x.UniqueId)+'</td>'+
      '<td class="status-'+esc(x.Status)+'">'+esc(x.Status)+'</td>'+
      '<td><span class="tag '+esc(x.Destination)+'">'+esc(x.Destination)+'</span></td>'+
      '<td>'+esc(x.HttpStatusCode??'')+'</td>'+
      '<td>'+esc(x.BusinessResponseCode??'')+'</td>'+
      '<td>'+esc(x.Reason??'')+'</td></tr>'+
      '<tr id="'+key+'" class="expand-row"><td colspan="8" class="expand-cell"><div class="muted">Loading details...</div></td></tr>';
  }).join('');
}
async function toggleDetail(rowId,recordId){
  const row=document.getElementById(rowId); const opening=!row.classList.contains('open');
  document.querySelectorAll('.expand-row.open').forEach(r=>r.classList.remove('open'));
  if(!opening)return; row.classList.add('open');
  let x=detailCache.get(recordId);
  if(!x){const r=await fetch('/requests/'+encodeURIComponent(recordId),{cache:'no-store'});if(!r.ok){row.querySelector('td').innerHTML='<div class="payload-note">Could not load request details.</div>';return}x=await r.json();detailCache.set(recordId,x)}
  row.querySelector('td').innerHTML=
    '<div class="detail-grid">'+
      '<div class="card"><div class="label">Batch ID</div><div class="id">'+esc(x.BatchId)+'</div></div>'+
      '<div class="card"><div class="label">Claims in Batch</div>'+esc(groupByBatch(items).find(b=>b.BatchId===x.BatchId)?.ClaimCount??1)+'</div>'+
      '<div class="card"><div class="label">Sent to APIM</div>'+(x.SentToApimAtUtc?new Date(x.SentToApimAtUtc).toLocaleString():'N/A')+'</div>'+
      '<div class="card"><div class="label">APIM Response</div>'+(x.ApimResponseAtUtc?new Date(x.ApimResponseAtUtc).toLocaleString():'N/A')+'</div>'+
      '<div class="card"><div class="label">Service Bus</div>'+(x.BufferedToServiceBusAtUtc?new Date(x.BufferedToServiceBusAtUtc).toLocaleString():'N/A')+'</div>'+
    '</div>'+
    (x.PayloadLoggingEnabled
      ? '<div class="payload-grid"><div class="payload-panel"><div class="payload-title">JSON sent to APIM</div><pre>'+syntaxHighlight(x.ApimRequestPayload)+'</pre></div><div class="payload-panel"><div class="payload-title">JSON received from APIM</div><pre>'+syntaxHighlight(x.ApimResponsePayload)+'</pre></div></div>'
      : '<div class="payload-note">Payload display is disabled because LOG_POLL_PAYLOADS=false.</div>');
}
async function load(){
  document.getElementById('status').textContent='Loading...'; detailCache.clear();
  const r=await fetch('/requests',{cache:'no-store'}); items=await r.json();
  document.getElementById('status').textContent=groupByBatch(items).length+' batch(es) loaded from '+items.length+' claim record(s)'; render();
}
document.querySelectorAll('th[data-key]').forEach(th=>th.addEventListener('click',()=>{const k=th.dataset.key;if(sortKey===k)sortDir=sortDir==='asc'?'desc':'asc';else{sortKey=k;sortDir='asc'}render()}));
load(); setInterval(load,30000);
</script>
</body>
</html>
""";

        await response.WriteStringAsync(html, Encoding.UTF8);
        return response;
    }
}
