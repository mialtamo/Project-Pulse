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
        var query = System.Web.HttpUtility.ParseQueryString(request.Url.Query);
        var limit = int.TryParse(query["limit"], out var parsed)
            ? Math.Clamp(parsed, 1, 100)
            : 50;

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
:root{color-scheme:dark;--bg:#05070b;--panel:#0b111a;--border:#1b2b3d;--text:#dbe8ff;--muted:#7f93ad;--ok:#54e39b;--warn:#ffc857;--bad:#ff627d;--cyan:#19ddff}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font-family:Inter,Segoe UI,system-ui,sans-serif}
header{padding:22px 28px;border-bottom:1px solid var(--border);display:flex;justify-content:space-between;align-items:center}
h1{margin:0;font-size:20px;letter-spacing:.08em}.muted{color:var(--muted)}
main{padding:24px 28px}.toolbar{display:flex;gap:10px;align-items:center;margin-bottom:16px}
button{background:#0d1a28;border:1px solid #23415d;color:var(--text);padding:8px 12px;border-radius:6px;cursor:pointer}
table{width:100%;border-collapse:collapse;background:var(--panel);border:1px solid var(--border)}
th,td{text-align:left;padding:10px 12px;border-bottom:1px solid var(--border);font-size:13px}
th{color:#9fb4cf;background:#0c1621}.row{cursor:pointer}.row:hover{background:#0f1b29}
.tag{display:inline-block;padding:2px 7px;border-radius:999px;font-size:11px;border:1px solid #29445f}
.APIM{color:var(--ok)}.ServiceBus{color:var(--warn)}
#detail{margin-top:20px;background:var(--panel);border:1px solid var(--border);padding:18px;display:none}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px;margin-bottom:16px}
.card{border:1px solid var(--border);padding:10px;background:#08101a}.label{font-size:10px;color:var(--muted);text-transform:uppercase;letter-spacing:.12em}
pre{white-space:pre-wrap;word-break:break-word;background:#020407;border:1px solid var(--border);padding:14px;max-height:360px;overflow:auto}
h2,h3{margin-top:0}.payload-note{color:var(--warn);font-size:12px}
</style>
</head>
<body>
<header><div><h1>PROJECT PULSE REQUESTS</h1><div class="muted">Recent APIM and Service Bus activity</div></div><a href="/" style="color:#19ddff">Home</a></header>
<main>
<div class="toolbar"><button onclick="load()">Refresh</button><span id="status" class="muted"></span></div>
<table><thead><tr><th>Updated</th><th>Unique ID</th><th>Status</th><th>Destination</th><th>HTTP</th><th>Business Code</th><th>Reason</th></tr></thead><tbody id="rows"></tbody></table>
<section id="detail"></section>
</main>
<script>
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const pretty=s=>{if(!s)return '';try{return JSON.stringify(JSON.parse(s),null,2)}catch{return s}}
async function load(){
 document.getElementById('status').textContent='Loading...';
 const r=await fetch('/requests?limit=100',{cache:'no-store'}); const items=await r.json();
 document.getElementById('rows').innerHTML=items.map(x=>`<tr class="row" onclick="detail('${esc(x.RecordId)}')"><td>${new Date(x.UpdatedAtUtc).toLocaleString()}</td><td>${esc(x.UniqueId)}</td><td>${esc(x.Status)}</td><td><span class="tag ${esc(x.Destination)}">${esc(x.Destination)}</span></td><td>${esc(x.HttpStatusCode??'')}</td><td>${esc(x.BusinessResponseCode??'')}</td><td>${esc(x.Reason??'')}</td></tr>`).join('');
 document.getElementById('status').textContent=items.length+' request(s)';
}
async function detail(id){
 const r=await fetch('/requests/'+encodeURIComponent(id),{cache:'no-store'}); if(!r.ok)return;
 const x=await r.json(); const d=document.getElementById('detail'); d.style.display='block';
 d.innerHTML=`<h2>${esc(x.UniqueId)}</h2>
 <div class="grid">
 <div class="card"><div class="label">Batch</div>${esc(x.BatchId)}</div>
 <div class="card"><div class="label">Status</div>${esc(x.Status)}</div>
 <div class="card"><div class="label">Destination</div>${esc(x.Destination)}</div>
 <div class="card"><div class="label">HTTP / Business</div>${esc(x.HttpStatusCode??'')} / ${esc(x.BusinessResponseCode??'')}</div>
 <div class="card"><div class="label">Sent to APIM</div>${x.SentToApimAtUtc?new Date(x.SentToApimAtUtc).toLocaleString():'N/A'}</div>
 <div class="card"><div class="label">APIM Response</div>${x.ApimResponseAtUtc?new Date(x.ApimResponseAtUtc).toLocaleString():'N/A'}</div>
 <div class="card"><div class="label">Service Bus</div>${x.BufferedToServiceBusAtUtc?new Date(x.BufferedToServiceBusAtUtc).toLocaleString():'N/A'}</div>
 </div>
 ${x.PayloadLoggingEnabled
 ? `<h3>JSON sent to APIM</h3><pre>${esc(pretty(x.ApimRequestPayload)||'No APIM request payload recorded')}</pre><h3>JSON received from APIM</h3><pre>${esc(pretty(x.ApimResponsePayload)||'No APIM response payload recorded')}</pre>`
 : '<div class="payload-note">Payload display is disabled because LOG_POLL_PAYLOADS=false.</div>'}`;
}
load(); setInterval(load,15000);
</script>
</body>
</html>
""";

        await response.WriteStringAsync(html, Encoding.UTF8);
        return response;
    }
}
