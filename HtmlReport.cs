using System.Net;
using System.Text;

namespace NovaTouchUpdater;

/// <summary>
/// Creates an offline, self-contained HTML report. All host-supplied data is HTML-encoded.
/// </summary>
internal static class HtmlReport
{
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static string Create(IReadOnlyList<HostState> hosts, string paymentTarget, string fiscalTarget, DateTime createdAt, bool forceUpdate)
    {
        int count = hosts.Count;
        int updated = hosts.Count(x => x.Result == "Erfolgreich");
        int current = hosts.Count(x => x.Result == "Bereits aktuell / übersprungen");
        int errors = hosts.Count(x => x.Result == "Fehler");
        int other = count - updated - current - errors;
        int offline = hosts.Count(x => x.Connection == "Nicht erreichbar");
        int finished = updated + current;
        int percent = count == 0 ? 0 : (int)Math.Round(100.0 * finished / count);
        var sb = new StringBuilder(Math.Max(8192, count * 800));

        sb.AppendLine("""
<!doctype html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>NovaTouch 2.5 Updater – Ergebnisbericht</title>
<style>
:root{color-scheme:light;--ink:#1c2d42;--muted:#64758b;--bg:#f3f6fb;--line:#dee6f0;--navy:#142c4a;--ok:#16734b;--okbg:#e8f8ef;--err:#aa2d36;--errbg:#ffedf0;--warn:#855a1c;--warnbg:#fff5df;--blue:#255fa6;--bluebg:#eaf3ff}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.55 -apple-system,BlinkMacSystemFont,"Segoe UI",Arial,sans-serif}
.hero{background:linear-gradient(112deg,#10253e,#23496b);color:#fff;padding:35px max(28px,calc((100vw - 1400px)/2)) 32px}
.hero h1{margin:0;font-size:29px;letter-spacing:-.5px}.hero p{margin:5px 0 0;color:#c5d7ea}.hero .stamp{color:#d6e8fa;font-size:13px;margin-top:18px}
main{max-width:1460px;margin:0 auto;padding:25px 28px 40px}.meta{display:flex;gap:10px;flex-wrap:wrap;margin-bottom:22px}.chip{background:white;border:1px solid var(--line);border-radius:999px;padding:7px 12px;color:var(--muted)}.chip strong{color:var(--ink)}
.stats{display:grid;grid-template-columns:repeat(5,minmax(0,1fr));gap:12px;margin-bottom:16px}
.stat{background:white;border:1px solid var(--line);border-radius:14px;padding:17px 19px;min-width:0;box-shadow:0 3px 15px #1e3b5910}.stat .name{color:var(--muted);font-size:12px;font-weight:650}.stat .num{font-size:30px;font-weight:750;line-height:1.4}.stat.error .num{color:var(--err)}.stat.success .num{color:var(--ok)}
.progress{height:11px;background:#e3eaf3;border-radius:20px;overflow:hidden;margin-bottom:25px}.progress>span{display:block;background:linear-gradient(90deg,#21ad73,#3bbb96);height:100%;border-radius:20px}
.panel{background:#fff;border:1px solid var(--line);border-radius:15px;box-shadow:0 5px 22px #203b5910;overflow:hidden}
.toolbar{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:12px;padding:16px 18px;border-bottom:1px solid var(--line)}.toolbar h2{margin:0;font-size:18px}.controls{display:flex;flex-wrap:wrap;gap:8px;align-items:center}
input,select,button{font:inherit;border:1px solid #cbd6e3;background:#fff;border-radius:9px;padding:9px 11px;color:var(--ink)}input{width:255px;max-width:100%}button{cursor:pointer}button:hover{background:#eef4fa}
.scroll{overflow-x:auto}table{border-collapse:collapse;width:100%;min-width:1440px;font-size:12.5px}thead th{position:sticky;top:0;background:#eef3fa;text-align:left;color:#52677f;font-weight:700;padding:12px 11px;white-space:nowrap;border-bottom:1px solid var(--line)}tbody td{padding:11px;border-bottom:1px solid #e9eef4;vertical-align:top}tbody tr:hover{background:#f9fbfe}tbody tr:last-child td{border-bottom:none}
.framework{width:1%;text-align:center;white-space:nowrap}.framework span{font-size:17px;font-weight:700}.framework .yes{color:var(--ok)}.framework .no{color:var(--err)}.pos-ver{min-width:110px;overflow-wrap:anywhere;font-variant-numeric:tabular-nums}
.attempt{width:1%;white-space:nowrap;text-align:center}.host{font-weight:700;white-space:nowrap}.ver{font-variant-numeric:tabular-nums;white-space:nowrap;width:145px}.ver-head{white-space:normal;min-width:145px;width:145px}.ver .before,.ver .after{display:block}.dim{color:var(--muted)}.before{color:#74859a;font-size:12px}.after{font-weight:700}.arrow{display:none}.ver .after::before{content:"→ ";color:#94a2b2;font-weight:normal}
.status{display:inline-block;padding:3px 8px;border-radius:999px;font-size:11.5px;font-weight:750;white-space:nowrap}.status.ok{background:var(--okbg);color:var(--ok)}.status.bad{background:var(--errbg);color:var(--err)}.status.current{background:var(--bluebg);color:var(--blue)}.status.other{background:var(--warnbg);color:var(--warn)}
.info-cell{min-width:430px;width:45%;overflow-wrap:anywhere}.info-phase{color:var(--ink)}.info-error{color:var(--err);margin-top:4px;white-space:pre-wrap;overflow-wrap:anywhere}.result-cell{white-space:nowrap;text-align:center}.log details{min-width:100px}.log summary{color:#245f9d;cursor:pointer;font-weight:650}.log pre{max-width:470px;max-height:320px;overflow:auto;font-size:11px;background:#13233a;color:#dce6f2;border-radius:8px;padding:11px;white-space:pre-wrap;overflow-wrap:anywhere;margin:8px 0 0}
.footer{color:var(--muted);font-size:12px;margin-top:16px}.empty{padding:24px;text-align:center;color:var(--muted)}
@media(max-width:1000px){.stats{grid-template-columns:repeat(2,minmax(0,1fr))}.hero{padding:26px 22px}main{padding:18px}}
@media print{body{background:white}.hero{background:white;color:black;padding:8px 0 12px}.hero p,.hero .stamp{color:#555}main{padding:0}.stats{grid-template-columns:repeat(5,1fr)}.stat,.panel{box-shadow:none}.controls{display:none}.scroll{overflow:visible}table{min-width:0;font-size:9px}tbody td,thead th{padding:6px 4px}.log{display:none}.before{font-size:9px}.footer{margin-top:8px}}
</style>
</head>
<body>
<header class="hero">
  <h1>NovaTouch Updater</h1>
  <p>Update-Ergebnisbericht · nt.payment / nt.fiscal</p>
""");
        sb.Append("  <div class=\"stamp\">Erstellt: ").Append(E(createdAt.ToString("dd.MM.yyyy HH:mm:ss"))).AppendLine("</div>");
        sb.AppendLine("</header><main><section class=\"meta\" aria-label=\"Zielversionen\">");
        sb.Append("<span class=\"chip\">Payment Target: <strong>").Append(E(Show(paymentTarget))).AppendLine("</strong></span>");
        sb.Append("<span class=\"chip\">Fiscal Target: <strong>").Append(E(Show(fiscalTarget))).AppendLine("</strong></span>");
        sb.Append("<span class=\"chip\">Update-Modus: <strong>").Append(forceUpdate ? "Force Update" : "Normal").AppendLine("</strong></span>");
        sb.Append("<span class=\"chip\">Erfolgsquote: <strong>").Append(percent).AppendLine(" %</strong></span>");
        sb.AppendLine("</section><section class=\"stats\" aria-label=\"Zusammenfassung\">");
        Card(sb, "Hosts gesamt", count, "");
        Card(sb, "Aktualisiert", updated, "success");
        Card(sb, "Bereits aktuell", current, "");
        Card(sb, "Fehler", errors, "error");
        Card(sb, "Weitere / offen", other, "");
        sb.Append("</section><div class=\"progress\" title=\"").Append(finished).Append(" von ").Append(count)
            .Append(" abgeschlossen\"><span style=\"width:").Append(percent).AppendLine("%\"></span></div>");
        sb.AppendLine("""
<section class="panel">
<div class="toolbar">
  <div><h2>Hostübersicht</h2><span class="dim" id="visibleCount"></span></div>
  <div class="controls">
    <input type="search" id="search" placeholder="Host oder Fehler suchen …" aria-label="Hostsuche">
    <select id="filter" aria-label="Statusfilter">
      <option value="all">Alle Ergebnisse</option>
      <option value="bad">Nur Fehler</option>
      <option value="ok">Aktualisiert</option>
      <option value="current">Bereits aktuell</option>
      <option value="other">Weitere / offen</option>
    </select>
    <button type="button" id="saveHtml">HTML speichern</button>
    <button type="button" onclick="window.print()">Drucken / als PDF speichern</button>
  </div>
</div>
<div class="scroll"><table id="hostTable">
<thead><tr>
  <th>Hostname</th><th>Verbindung</th><th class="framework">.NET 4.8</th><th class="ver-head">Payment (vorher → nachher)</th><th class="ver-head">Fiscal (vorher → nachher)</th><th>NovaTouch POS</th>
  <th>Information</th><th>Details</th><th class="attempt">Versuch</th><th>Ergebnis</th>
</tr></thead><tbody>
""");
        foreach (HostState host in hosts.OrderBy(h => h.Host, StringComparer.OrdinalIgnoreCase))
        {
            string kind = host.Result switch
            {
                "Erfolgreich" => "ok",
                "Bereits aktuell / übersprungen" => "current",
                "Fehler" => "bad",
                _ => "other"
            };
            string search = string.Join(' ', new[] { host.Host, host.Connection, DisplayText.Clean(host.PaymentBefore), DisplayText.Clean(host.Payment),
                DisplayText.Clean(host.FiscalBefore), DisplayText.Clean(host.Fiscal), DisplayText.DotNet48Description(host.DotNet48Installed),
                DisplayText.Clean(host.PosVersion), DisplayText.Clean(host.Phase),
                host.Result, DisplayText.Clean(host.Error) }).ToLowerInvariant();
            sb.Append("<tr data-kind=\"").Append(kind).Append("\" data-search=\"").Append(E(search)).AppendLine("\">");
            sb.Append("<td class=\"host\">").Append(E(host.Host)).AppendLine("</td>");
            sb.Append("<td>").Append(E(host.Connection)).AppendLine("</td>");
            sb.Append("<td class=\"framework\"><span class=\"")
                .Append(host.DotNet48Installed switch { true => "yes", false => "no", null => "dim" })
                .Append("\" title=\"").Append(E(DisplayText.DotNet48Description(host.DotNet48Installed)))
                .Append("\" aria-label=\"").Append(E(DisplayText.DotNet48Description(host.DotNet48Installed)))
                .Append("\">").Append(DisplayText.DotNet48(host.DotNet48Installed)).AppendLine("</span></td>");
            VersionCell(sb, host.PaymentBefore, host.Payment);
            VersionCell(sb, host.FiscalBefore, host.Fiscal);
            sb.Append("<td class=\"pos-ver\">").Append(E(DisplayText.Clean(host.PosVersion))).AppendLine("</td>");
            // Der aktuelle Schritt und die bisherigen Fehler-/Informationstexte teilen sich eine Spalte.
            sb.Append("<td class=\"info-cell\">");
            if (!string.IsNullOrWhiteSpace(host.Phase))
                sb.Append("<div class=\"info-phase\">").Append(E(DisplayText.Clean(host.Phase))).Append("</div>");
            if (!string.IsNullOrWhiteSpace(host.Error) && !host.Error.Equals(host.Phase, StringComparison.OrdinalIgnoreCase))
                sb.Append("<div class=\"info-error\">").Append(E(DisplayText.Clean(host.Error))).Append("</div>");
            sb.AppendLine("</td>");
            sb.Append("<td class=\"log\"><details><summary>Protokoll</summary><pre>");
            sb.Append(E(host.Log.Count == 0 ? "Kein Hostprotokoll vorhanden." : string.Join(Environment.NewLine, host.Log.Select(DisplayText.Clean))));
            sb.AppendLine("</pre></details></td>");
            sb.Append("<td class=\"attempt\">").Append(host.Attempt > 0 ? host.Attempt.ToString() + "/3" : "–").AppendLine("</td>");
            sb.Append("<td class=\"result-cell\"><span class=\"status ").Append(kind).Append("\">")
                .Append(E(host.Result == "Bereits aktuell / übersprungen" ? "Aktuell" : host.Result))
                .AppendLine("</span></td></tr>");
        }
        sb.AppendLine("""
</tbody></table></div><div class="empty" id="empty" hidden>Keine passenden Hosts gefunden.</div></section>
""");
        sb.Append("<p class=\"footer\">Hosts mit WinRM-Verbindungsfehlern: ").Append(offline)
            .Append(" · Hinweis: Der Bericht bildet den Zustand zum Exportzeitpunkt ab. ")
            .Append(".NET 4.8: ✓ = 4.8 oder neuer installiert, ✗ = nicht installiert, – = nicht geprüft. ")
            .Append("Eine fehlende NovaTouch-POS-Installation wird nicht als Fehler gewertet. ")
            .AppendLine("Die Anzeige „Erfolgreich“ beruht auf der Versions- und Dienststatusprüfung durch den Updater.</p>");
        sb.AppendLine("""
</main>
<script>
(() => {
  const search = document.getElementById('search');
  const filter = document.getElementById('filter');
  const rows = [...document.querySelectorAll('#hostTable tbody tr')];
  const visibleCount = document.getElementById('visibleCount');
  const empty = document.getElementById('empty');
  function apply() {
    const q = search.value.trim().toLocaleLowerCase('de');
    const kind = filter.value;
    let visible = 0;
    for (const row of rows) {
      const match = (kind === 'all' || row.dataset.kind === kind) && (!q || row.dataset.search.includes(q));
      row.hidden = !match;
      if (match) visible++;
    }
    visibleCount.textContent = visible + ' von ' + rows.length + ' Hosts angezeigt';
    empty.hidden = visible !== 0;
  }
  search.addEventListener('input', apply);
  filter.addEventListener('change', apply);
  document.getElementById('saveHtml').addEventListener('click', () => {
    // Eigenständigen HTML-Bericht aus dem Browser sichern, ohne externen Server.
    const html = '<!doctype html>\n' + document.documentElement.outerHTML;
    const blob = new Blob([html], { type: 'text/html;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = 'NovaTouchUpdate-' + new Date().toISOString().replace(/\D/g, '').slice(0, 14) + '.html';
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1500);
  });
  apply();
})();
</script>
</body></html>
""");
        return sb.ToString();
    }

    private static string Show(string version) => string.IsNullOrWhiteSpace(version) ? "Nicht ausgewählt" : version;

    private static void Card(StringBuilder sb, string label, int value, string css)
    {
        sb.Append("<article class=\"stat ").Append(css).Append("\"><div class=\"name\">")
            .Append(E(label)).Append("</div><div class=\"num\">").Append(value).AppendLine("</div></article>");
    }

    private static void VersionCell(StringBuilder sb, string before, string after)
    {
        sb.Append("<td class=\"ver\"><span class=\"before\">").Append(E(DisplayText.Clean(before)))
            .Append("</span><span class=\"arrow\">→</span><span class=\"after\">")
            .Append(E(DisplayText.Clean(after))).AppendLine("</span></td>");
    }
}
