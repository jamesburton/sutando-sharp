using System.Globalization;
using System.Net;
using System.Text;
using Sutando.Workspace;

namespace Sutando.Dashboard;

/// <summary>
/// Renders the dashboard's single-page HTML view. Server-side renders the initial snapshot
/// (so tests can assert against a known string) and includes inline JS that subscribes to the
/// SignalR hub to keep the page live.
/// </summary>
internal static class DashboardHtml
{
    /// <summary>Render the dashboard HTML for the given snapshot.</summary>
    /// <param name="snapshot">Snapshot to embed as the initial page state.</param>
    /// <returns>Fully-formed HTML document text.</returns>
    public static string Render(DashboardSnapshotPayload snapshot)
    {
        var sb = new StringBuilder(4096);
        sb.Append("<!DOCTYPE html>");
        sb.Append("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>sutando — status</title>");
        sb.Append("<style>");
        sb.Append("body{font-family:-apple-system,BlinkMacSystemFont,Segoe UI,sans-serif;background:#0a0a12;color:#e8e8e8;margin:0;padding:24px;line-height:1.5}");
        sb.Append("h1{font-size:18px;margin:0 0 16px;color:#fff;font-weight:500}");
        sb.Append("h2{font-size:14px;margin:24px 0 8px;color:#9aa;font-weight:500;text-transform:uppercase;letter-spacing:0.05em}");
        sb.Append(".card{background:#12121e;border:1px solid #1e1e30;border-radius:8px;padding:16px;margin-bottom:12px}");
        sb.Append(".pill{display:inline-block;padding:2px 8px;border-radius:99px;font-size:12px;background:#1a2e24;color:#4ecca3;margin-right:6px}");
        sb.Append(".pill.idle{background:#1a1e2e;color:#7aa}");
        sb.Append(".pill.degraded{background:#2e1a1a;color:#cc6}");
        sb.Append(".pill.running{background:#1a2e24;color:#4ecca3}");
        sb.Append(".meta{font-size:12px;color:#666;margin-top:4px}");
        sb.Append(".task{padding:8px 0;border-bottom:1px solid #1e1e30}");
        sb.Append(".task:last-child{border-bottom:none}");
        sb.Append(".task .id{font-family:ui-monospace,Menlo,monospace;font-size:12px;color:#7aa}");
        sb.Append(".task .body{margin-top:2px;color:#ccc}");
        sb.Append(".muted{color:#666}");
        sb.Append("</style></head><body>");

        sb.Append("<h1>sutando — status</h1>");

        // Core status card.
        sb.Append("<div class=\"card\" id=\"core-status-card\">");
        sb.Append("<h2>core status</h2>");
        sb.Append("<div id=\"core-status\">");
        RenderCoreStatus(sb, snapshot.CoreStatus);
        sb.Append("</div></div>");

        // Heartbeats.
        sb.Append("<div class=\"card\">");
        sb.Append("<h2>heartbeats</h2>");
        if (snapshot.Heartbeats.Count == 0)
        {
            sb.Append("<div class=\"muted\">(no hosts reporting)</div>");
        }
        else
        {
            foreach (var hb in snapshot.Heartbeats)
            {
                sb.Append("<div class=\"task\">");
                sb.Append("<span class=\"id\">").Append(Encode(hb.Host)).Append(" (pid ").Append(hb.Pid).Append(")</span>");
                sb.Append(" <span class=\"pill ").Append(Encode(hb.Status)).Append("\">").Append(Encode(hb.Status)).Append("</span>");
                sb.Append("<div class=\"meta\">last beat ").Append(FormatEpoch(hb.LastBeatAt)).Append("</div>");
                sb.Append("</div>");
            }
        }
        sb.Append("</div>");

        // Owner activity.
        sb.Append("<div class=\"card\">");
        sb.Append("<h2>owner activity</h2>");
        if (snapshot.OwnerActivity is null)
        {
            sb.Append("<div class=\"muted\">(no activity recorded)</div>");
        }
        else
        {
            sb.Append("<div><span class=\"pill\">").Append(Encode(snapshot.OwnerActivity.Channel)).Append("</span>");
            sb.Append(Encode(snapshot.OwnerActivity.Summary)).Append("</div>");
            sb.Append("<div class=\"meta\">").Append(FormatEpoch(snapshot.OwnerActivity.Ts)).Append("</div>");
        }
        sb.Append("</div>");

        // Tasks.
        sb.Append("<div class=\"card\">");
        sb.Append("<h2>recent tasks (")
            .Append(snapshot.PendingTaskCount.ToString(CultureInfo.InvariantCulture))
            .Append(" pending)</h2>");
        sb.Append("<div id=\"recent-tasks\">");
        if (snapshot.RecentTasks.Count == 0)
        {
            sb.Append("<div class=\"muted\">(no tasks)</div>");
        }
        else
        {
            foreach (var t in snapshot.RecentTasks)
            {
                sb.Append("<div class=\"task\">");
                sb.Append("<span class=\"id\">").Append(Encode(t.Id)).Append("</span>");
                sb.Append(" <span class=\"pill\">").Append(Encode(t.Source)).Append("</span>");
                sb.Append(" <span class=\"pill\">").Append(Encode(t.Priority)).Append("</span>");
                sb.Append("<div class=\"body\">").Append(Encode(t.BodyPreview)).Append("</div>");
                sb.Append("<div class=\"meta\">")
                  .Append(t.Timestamp.ToString("O", CultureInfo.InvariantCulture))
                  .Append("</div>");
                sb.Append("</div>");
            }
        }
        sb.Append("</div></div>");

        sb.Append("<div class=\"meta\" id=\"footer\">snapshot ")
          .Append(snapshot.CapturedAt.ToString("O", CultureInfo.InvariantCulture))
          .Append("</div>");

        sb.Append(LiveScript);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void RenderCoreStatus(StringBuilder sb, CoreStatusPayload? payload)
    {
        if (payload is null)
        {
            sb.Append("<span class=\"pill idle\">unknown</span> ");
            sb.Append("<span class=\"muted\">no signal yet</span>");
            return;
        }
        var status = string.IsNullOrEmpty(payload.Status) ? "idle" : payload.Status;
        sb.Append("<span class=\"pill ").Append(Encode(status)).Append("\">").Append(Encode(status)).Append("</span>");
        if (!string.IsNullOrEmpty(payload.Step))
        {
            sb.Append("<span>").Append(Encode(payload.Step!)).Append("</span>");
        }
        sb.Append("<div class=\"meta\">").Append(FormatEpoch(payload.Ts)).Append("</div>");
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FormatEpoch(long ts)
    {
        if (ts <= 0)
        {
            return "—";
        }
        var when = DateTimeOffset.FromUnixTimeSeconds(ts);
        return when.ToString("O", CultureInfo.InvariantCulture);
    }

    // Loaded from a CDN so we don't need to vendor microsoft-signalr into wwwroot. The script
    // gracefully no-ops if the CDN is unreachable (test environment, air-gapped install) and
    // the page still renders the snapshot the server embedded.
    private const string LiveScript = """
<script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.7/dist/browser/signalr.min.js"></script>
<script>
(function(){
  if (typeof signalR === 'undefined') { return; }
  const conn = new signalR.HubConnectionBuilder()
    .withUrl('/hub/status')
    .withAutomaticReconnect()
    .build();
  conn.on('core_status_changed', function(p){
    const el = document.getElementById('core-status');
    if (!el || !p) return;
    const status = p.status || 'idle';
    el.innerHTML = '<span class="pill ' + status + '">' + status + '</span>'
      + (p.step ? ('<span>' + escape(p.step) + '</span>') : '')
      + '<div class="meta">' + (p.ts ? new Date(p.ts * 1000).toISOString() : '—') + '</div>';
  });
  function refresh(){
    fetch('/snapshot').then(function(r){return r.json();}).then(function(snap){
      // Lightweight refresh — full page reload sidesteps state-management complexity.
      window.location.reload();
    }).catch(function(){});
  }
  conn.on('task_added', refresh);
  conn.on('result_added', refresh);
  function escape(s){var d=document.createElement('div');d.textContent=s;return d.innerHTML;}
  conn.start().catch(function(){ /* offline; snapshot still rendered */ });
})();
</script>
""";
}
