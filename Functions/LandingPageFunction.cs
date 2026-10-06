using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace ProjectPulse.Processor.Functions;

public sealed class LandingPageFunction
{
    private readonly HttpClient _httpClient = new();

    [Function("PulseLandingPage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{*path}")]
        HttpRequestData request,
        string? path)
    {
        var normalizedPath = (path ?? string.Empty).Trim('/');

        if (!string.IsNullOrEmpty(normalizedPath) &&
            !normalizedPath.Equals("pulse", StringComparison.OrdinalIgnoreCase))
        {
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        var version = WebUtility.HtmlEncode(
            Environment.GetEnvironmentVariable("PROJECT_PULSE_VERSION") ?? "0.0.0");

        var environment = WebUtility.HtmlEncode(
            Environment.GetEnvironmentVariable("PROJECT_PULSE_ENVIRONMENT") ?? "UNKNOWN");

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate");
        response.Headers.Add("Pragma", "no-cache");
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        response.Headers.Add("X-Frame-Options", "DENY");
        response.Headers.Add(
            "Content-Security-Policy",
            "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'");

        var html = $$"""
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="color-scheme" content="dark" />
    <title>Project Pulse</title>
    <style>
        :root {
            --cyan: #19ddff;
            --blue: #3478ff;
            --violet: #8a4dff;
            --text: #d7e6ff;
            --muted: #6f86a8;
            --green: #39ff9a;
            --red: #ff4d6d;
            --amber: #ffc857;
        }

        * { box-sizing: border-box; }

        html, body {
            width: 100%;
            height: 100%;
            margin: 0;
            overflow: hidden;
            background: #000;
            color: var(--text);
            font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
        }

        body::before {
            content: "";
            position: fixed;
            inset: -35%;
            pointer-events: none;
            background:
                radial-gradient(circle at 50% 44%, rgba(25, 221, 255, .08), transparent 28%),
                radial-gradient(circle at 56% 48%, rgba(138, 77, 255, .07), transparent 34%);
            animation: atmosphere 8s ease-in-out infinite alternate;
        }

        .grid {
            position: fixed;
            inset: 0;
            opacity: .10;
            background-image:
                linear-gradient(rgba(74, 121, 255, .18) 1px, transparent 1px),
                linear-gradient(90deg, rgba(74, 121, 255, .18) 1px, transparent 1px);
            background-size: 72px 72px;
            mask-image: radial-gradient(circle at center, #000 0%, transparent 72%);
            pointer-events: none;
        }

        .shell {
            position: relative;
            width: 100%;
            height: 100%;
            display: grid;
            place-items: center;
            padding: 4vh 4vw;
        }

        .hero {
            width: min(980px, 82vw);
            display: flex;
            align-items: center;
            justify-content: center;
            transform: translateY(-1.2vh);
        }

        .logo-wrap {
            position: relative;
            width: 100%;
            animation: float 6s ease-in-out infinite;
        }

        .logo-wrap::before {
            content: "";
            position: absolute;
            inset: 18% 16% 12%;
            z-index: -1;
            background: radial-gradient(ellipse at center,
                rgba(20, 213, 255, .18) 0%,
                rgba(64, 99, 255, .09) 40%,
                rgba(137, 65, 255, .06) 58%,
                transparent 76%);
            filter: blur(28px);
            animation: glow 3.2s ease-in-out infinite alternate;
        }

        .logo {
            display: block;
            width: 100%;
            height: auto;
            object-fit: contain;
            filter: drop-shadow(0 0 18px rgba(41, 197, 255, .13));
            user-select: none;
            -webkit-user-drag: none;
        }

        .status-panel {
            position: fixed;
            top: 24px;
            right: 28px;
            z-index: 20;
            min-width: 390px;
            padding: 16px 18px;
            border: 1px solid rgba(25, 221, 255, .16);
            border-radius: 8px;
            background: rgba(0, 0, 0, .52);
            backdrop-filter: blur(10px);
            box-shadow:
                0 0 30px rgba(25, 221, 255, .05),
                inset 0 0 25px rgba(25, 221, 255, .025);
            font-family: "Courier New", Consolas, monospace;
            font-size: 12px;
            line-height: 1.85;
            color: #8098ba;
        }

        .status-title {
            margin-bottom: 8px;
            color: #d7e6ff;
            font-weight: 700;
            letter-spacing: .18em;
            text-transform: uppercase;
        }

        .status-row {
            white-space: nowrap;
        }

        .status-label {
            color: #8098ba;
        }

        .connected {
            color: var(--green);
            text-shadow: 0 0 9px rgba(57, 255, 154, .68);
        }

        .disconnected {
            color: var(--red);
            text-shadow: 0 0 9px rgba(255, 77, 109, .62);
        }

        .checking {
            color: var(--amber);
            text-shadow: 0 0 8px rgba(255, 200, 87, .35);
        }

        .last-check {
            margin-top: 8px;
            padding-top: 7px;
            border-top: 1px solid rgba(111, 134, 168, .14);
            color: #526783;
            font-size: 10px;
            letter-spacing: .08em;
        }

        .version {
            position: fixed;
            left: 24px;
            bottom: 19px;
            z-index: 5;
            display: flex;
            align-items: center;
            gap: 10px;
            color: var(--muted);
            font-size: 11px;
            font-weight: 600;
            letter-spacing: .16em;
            text-transform: uppercase;
            user-select: none;
        }

        .version .dot {
            width: 6px;
            height: 6px;
            border-radius: 50%;
            background: var(--cyan);
            box-shadow: 0 0 12px rgba(25, 221, 255, .95);
            animation: blink 2.1s ease-in-out infinite;
        }

        .scanline {
            position: fixed;
            left: 0;
            right: 0;
            height: 1px;
            top: -2px;
            background: linear-gradient(
                90deg,
                transparent 10%,
                rgba(25, 221, 255, .22),
                rgba(138, 77, 255, .18),
                transparent 90%);
            box-shadow: 0 0 8px rgba(25, 221, 255, .14);
            animation: scan 8s linear infinite;
            pointer-events: none;
        }

        @keyframes float {
            0%, 100% { transform: translateY(0) scale(1); }
            50% { transform: translateY(-8px) scale(1.003); }
        }

        @keyframes glow {
            from { opacity: .72; transform: scale(.98); }
            to { opacity: 1; transform: scale(1.035); }
        }

        @keyframes blink {
            0%, 100% { opacity: .62; }
            50% { opacity: 1; }
        }

        @keyframes scan {
            from { transform: translateY(0); opacity: 0; }
            8% { opacity: 1; }
            92% { opacity: 1; }
            to { transform: translateY(100vh); opacity: 0; }
        }

        @keyframes atmosphere {
            from { transform: scale(1) rotate(0deg); opacity: .82; }
            to { transform: scale(1.06) rotate(.8deg); opacity: 1; }
        }

        @media (max-width: 700px) {
            .hero { width: min(1100px, 96vw); }

            .version {
                left: 16px;
                bottom: 14px;
                font-size: 9px;
            }

            .status-panel {
                top: 12px;
                right: 12px;
                left: 12px;
                min-width: 0;
                font-size: 10px;
            }
        }

        @media (prefers-reduced-motion: reduce) {
            *, *::before, *::after { animation: none !important; }
        }
    </style>
</head>
<body>
    <div class="grid" aria-hidden="true"></div>
    <div class="scanline" aria-hidden="true"></div>

    <div class="status-panel">
        <div class="status-title">SYSTEM STATUS</div>

        <div class="status-row">
            <span class="status-label">Checking Claims API / SQL..... </span>
            <span id="appApi" class="checking">&lt;CHECKING...&gt;</span>
        </div>

        <div class="status-row">
            <span class="status-label">Checking APIM / Backend....... </span>
            <span id="apim" class="checking">&lt;CHECKING...&gt;</span>
        </div>

        <div class="status-row">
            <span class="status-label">Checking Service Bus.......... </span>
            <span id="serviceBus" class="checking">&lt;CHECKING...&gt;</span>
        </div>

        <div class="last-check" id="lastCheck">
            LAST CHECK: awaiting first health probe
        </div>
    </div>

    <main class="shell">
        <section class="hero" aria-label="Project Pulse">
            <div class="logo-wrap">
                <img class="logo" src="/pulse-logo" alt="Project Pulse" />
            </div>
        </section>
    </main>

    <div class="version" aria-label="Project Pulse version {{version}}, environment {{environment}}">
        <span class="dot" aria-hidden="true"></span>
        <span>Project Pulse&nbsp;&nbsp;v{{version}}&nbsp;&nbsp;•&nbsp;&nbsp;{{environment}}</span>
    </div>

    <script>
        function setStatus(id, connected) {
            const element = document.getElementById(id);

            if (connected) {
                element.className = 'connected';
                element.textContent = '<CONNECTED>';
            } else {
                element.className = 'disconnected';
                element.textContent = '<DISCONNECTED>';
            }
        }

        function setChecking() {
            for (const id of ['appApi', 'apim', 'serviceBus']) {
                const element = document.getElementById(id);
                element.className = 'checking';
                element.textContent = '<CHECKING...>';
            }
        }

        async function checkPulseHealth() {
            setChecking();

            try {
                const response = await fetch('/pulse-health', {
                    method: 'GET',
                    cache: 'no-store'
                });

                if (!response.ok) {
                    throw new Error('Health endpoint returned ' + response.status);
                }

                const data = await response.json();

                setStatus('appApi', data.appApi);
                setStatus('apim', data.apim);
                setStatus('serviceBus', data.serviceBus);

                document.getElementById('lastCheck').textContent =
                    'LAST CHECK: ' + new Date().toLocaleTimeString();
            }
            catch {
                setStatus('appApi', false);
                setStatus('apim', false);
                setStatus('serviceBus', false);

                document.getElementById('lastCheck').textContent =
                    'LAST CHECK: ' + new Date().toLocaleTimeString() + '  |  HEALTH ENDPOINT UNAVAILABLE';
            }
        }

        checkPulseHealth();
        setInterval(checkPulseHealth, 5000);
    </script>
</body>
</html>
""";

        await response.WriteStringAsync(html, Encoding.UTF8);
        return response;
    }

    [Function("PulseHealth")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pulse-health")]
        HttpRequestData request)
    {
        var appApi = await CheckHttpAsync(
            Environment.GetEnvironmentVariable("AppApiBaseUrl"),
            Environment.GetEnvironmentVariable("AppApiHealthPath") ?? "/health");

        var apim = await CheckHttpAsync(
            Environment.GetEnvironmentVariable("ApimBaseUrl"),
            Environment.GetEnvironmentVariable("ApimHealthPath") ?? "/health");

        var serviceBus = await CheckServiceBusAsync(
            Environment.GetEnvironmentVariable("ServiceBusFullyQualifiedNamespace"));

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        response.Headers.Add("Cache-Control", "no-store");

        var json = JsonSerializer.Serialize(new
        {
            appApi,
            apim,
            serviceBus,
            checkedAt = DateTimeOffset.UtcNow
        });

        await response.WriteStringAsync(json, Encoding.UTF8);
        return response;
    }

    [Function("PulseLogo")]
    public async Task<HttpResponseData> Logo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pulse-logo")]
        HttpRequestData request)
    {
        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "image/png");
        response.Headers.Add("Cache-Control", "public, max-age=86400, immutable");
        response.Headers.Add("X-Content-Type-Options", "nosniff");

        var assembly = Assembly.GetExecutingAssembly();

        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name =>
                name.EndsWith(
                    "project-pulse-logo.png",
                    StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        await using var resource = assembly.GetManifestResourceStream(resourceName);

        if (resource is null)
        {
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        await resource.CopyToAsync(response.Body);
        return response;
    }

    private async Task<bool> CheckHttpAsync(string? baseUrl, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return false;
            }

            using var cancellationTokenSource =
                new CancellationTokenSource(TimeSpan.FromSeconds(3));

            var url =
                $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

            using var response = await _httpClient.GetAsync(
                url,
                cancellationTokenSource.Token);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CheckServiceBusAsync(string? host)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            using var client = new TcpClient();

            using var cancellationTokenSource =
                new CancellationTokenSource(TimeSpan.FromSeconds(3));

            await client.ConnectAsync(
                host,
                5671,
                cancellationTokenSource.Token);

            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
