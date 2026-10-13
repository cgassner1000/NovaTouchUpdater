# NovaTouch Updater — WinForms / .NET 8

Quellcode für eine eigenständige Windows-GUI zur parallelen Verwaltung von Updates der Windows-Dienste `nt.payment*` und `nt.fiscal*`, basierend auf den gelieferten PowerShell-Skripten.

## Voraussetzungen

- Visual Studio 2026 mit Workload **.NET-Desktopentwicklung** und .NET 8 SDK
- Adminrechte auf den Ziel-PCs, Windows PowerShell 5.1 auf dem Steuer-PC
- WinRM / PowerShell-Remoting ist auf den Ziel-PCs eingerichtet und erreichbar
- Die Windows-Dienste `nt.payment*` und/oder `nt.fiscal*` sind korrekt registriert
- Ausreichend Platz für ZIP-Übertragung und temporäres Entpacken auf den Ziel-PCs

Der GUI-Updater selbst nutzt **.NET 8** und startet Windows PowerShell 5.1 als Hintergrundprozess. Der darin eingebettete Worker verwendet `New-PSSession`, `Invoke-Command` und `Copy-Item -ToSession`, analog zur ursprünglichen Lösung. Es wird **kein zusätzlicher Agent** auf den Zielrechnern installiert.

Windows 10 Enterprise LTSC 2019 (1809) und Windows Server 2019 unterstützen .NET 8. Der .NET-8-Support endet am 10. November 2026. Die Windows-Desktopanwendung kann self-contained veröffentlicht werden.

## Projekt öffnen

Visual Studio 2026: `NovaTouchUpdater.csproj` öffnen, NuGet/SDK laden, `F5` starten. Für Release:

```powershell
dotnet publish .\NovaTouchUpdater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false
```

Ergebnis im Ordner `bin\Release\net8.0-windows\win-x64\publish\`.

**Wichtig:** Diese Quellcodeversion konnte in der hier verwendeten Linux-Umgebung nicht mit Visual Studio oder Windows PowerShell ausgeführt bzw. gegen reale Windows-Hosts geprüft werden. Vor einem produktiven Rollout auf Testrechnern testen, insbesondere Paketinhalt, Versionseigenschaften und Dienststarts.

## Bedienung

1. Hostnamen-TXT laden: ein Hostname/IP pro Zeile; Leerzeilen und Zeilen mit `#`/`;` werden ignoriert, Duplikate entfernt.
2. `nt.payment_VERSION*.zip` und/oder `nt.fiscal_VERSION*.zip` wählen (die Auswahlfelder stehen direkt untereinander). Beispiel: `nt.payment_2026.2.9546.5587.zip`.
3. Wenn andere Windows-Anmeldedaten benötigt werden, Benutzername und Passwort eingeben. Sonst wird das aktuelle Windows-Konto genutzt. Option `HTTPS` aktiviert WinRM/TLS.
4. `1. Prüfen + Versionen`: alle Hosts zuerst per WinRM prüfen (Standard: bis zu 40 parallel), dann vorhandene Dienste und deren ProductVersion/FileVersion lesen.
   Der RemoteWorker schließt jede PowerShell-Remotesitzung am Ende der Aktion automatisch (auch bei Fehlern); ein zusätzlicher Schließen-Button ist daher nicht erforderlich.
5. Optional `Force Update` aktivieren: Normalmodus überspringt Dienste mit IST-Version = Zielversion; Force installiert die **ausgewählten** Komponenten trotzdem erneut. Nicht ausgewählte Payment-/Fiscal-Dienste bleiben immer unangetastet. Die Zielversion wird auch bei Force nach Installation verifiziert.
6. `2. Updates starten`: Updates auf den erfolgreichen Scan-Hosts (Standard: 8 gleichzeitig) mit **maximal drei vollständigen Versuchen pro Host**. Die beiden Optionen `Prüfen parallel` und `Update parallel` begrenzen unabhängig voneinander die Zahl gleichzeitig bearbeiteter Rechner (Vorgaben: 40 bzw. 8). Bestätigungsdialog vor dem Start.
7. `Fehler erneut versuchen` berücksichtigt **ausschließlich Hosts mit Ergebnis `Fehler`** (auch Verbindungs- und Scanfehler) und prüft bei Bedarf erst ihre Erreichbarkeit. Erfolgreiche Hosts und bereits aktuelle Hosts werden nicht neu aktualisiert. Der erneute Durchlauf hat wieder bis zu drei Versuche pro Host.
8. Doppelklick auf eine Zeile öffnet das Hostprotokoll; `HTML-Bericht erstellen` öffnet sofort einen eigenständigen, offline nutzbaren Bericht im Standardbrowser – ohne vorherigen Speicherdialog. Über „HTML speichern“ im Bericht oder Strg+S im Browser kann er dauerhaft gespeichert werden; PDF-Export per Drucken ist ebenfalls möglich. SHA256-Werte werden in GUI und Bericht ausgeblendet, die Integritätsprüfung bleibt aktiv.

Die Hostübersicht zeigt nur die **IST-Versionen** von Payment und Fiscal. Die für alle Hosts identischen Zielversionen stehen als **Payment Target** und **Fiscal Target** bei der ZIP-Auswahl und werden weiterhin automatisch aus dem ZIP-Dateinamen gelesen.

Die Spaltenreihenfolge lautet: **Hostname | Verbindung | Payment Ist | Fiscal Ist | Information | Versuch | Ergebnis**. Die breite Spalte „Information“ zeigt den aktuellen Bearbeitungsschritt **zusätzlich** zu vorhandenen Fehler- und Informationstexten an. „Versuch“ und „Ergebnis“ sind kompakt; „Ergebnis“ steht ganz rechts. Im HTML-Bericht bleibt der aufklappbare Protokollbereich als zusätzliche Spalte **vor** „Versuch“ und „Ergebnis“ erhalten.

## HTML-Bericht

Der HTML-Bericht wird beim Klick auf `HTML-Bericht erstellen` zunächst in einem temporären Vorschaupfad abgelegt und automatisch im Standardbrowser geöffnet (kein Internetzugang nötig). Der Button „HTML speichern“ im Bericht speichert die HTML-Datei auf Wunsch. Enthalten: Datum/Uhrzeit, Zielversionen, Gesamt-/Erfolg-/Fehlerzahlen, Hostliste inklusive Vorher-/Nachher-Versionen, Suche/Statusfilter sowie detaillierte Hostprotokolle. Die dynamischen Inhalte werden HTML-escaped. Ein Bericht erfasst den aktuellen Zustand der Anwendung zum Zeitpunkt des Exports.

## Ablauf je Host

1. WinRM-Sitzung aufbauen.
2. Aus der Registry des jeweiligen Dienstes `ImagePath` ermitteln; EXE-Versionen auslesen.
3. Nur bei abweichender Zielversion (oder bei aktiviertem Force Update): ZIP-Datei über PowerShell-Remoting auf `C:\install\Novacom` kopieren (mit SHA256-Prüfung, bereits gleiche Datei wird nicht erneut kopiert).
4. ZIP in temporären Unterordner am Zielrechner entpacken.
5. Betroffene Dienste stoppen, Inhalt in den Installationsordner kopieren, Dienste (außer deaktivierte) starten.
6. Produkt-/Dateiversion und Zustand der gestarteten Dienste erneut kontrollieren.
7. Bei Fehlern maximal zwei weitere Male mit kurzer Wartezeit erneut versuchen. Bereits aktuelle Dienste werden nicht erneut überschrieben.
8. Status und Fehlermeldung pro Host anzeigen und protokollieren.

## Grenzen und Sicherheitsaspekte

- **Kein automatisches Rollback**: Werden während des Kopierens Dateien verändert und der Vorgang scheitert, wiederholt der Updater den Versuch. Nach endgültigem Fehler muss der betroffene Host ggf. manuell repariert werden.
- ZIP-Inhalt wird direkt über vorhandene Installation kopiert. Der Aufbau der Pakete muss deshalb dem der bisherigen `Expand-Archive`-Lösung entsprechen.
- Dienstneustarts unterbrechen die laufenden Dienste. Vor Produktiveinsatz Wartungsfenster berücksichtigen.
- `Abbrechen` stoppt das Einplanen weiterer Hosts. Ein bereits gestarteter **Update-Schritt** darf zunächst Stop/Kopie/Start abschließen, damit Dienste nicht mitten im Update hängen bleiben.
- `Verbindung` testet die tatsächlich benötigte WinRM-Anmeldung (nicht nur ICMP-Ping); somit werden Hosts mit blockiertem ICMP nicht unnötig aussortiert.
- Das Kennwort wird nicht in Argumentlisten oder Logdateien gespeichert. Für die Übertragung vom GUI-Prozess an den lokalen Worker wird JSON über dessen **StandardInput** verwendet; dabei ist das Kennwort temporär als String im Speicher. Für besonders restriktive Sicherheitsumgebungen wären integrierte Windows-Anmeldung / Credential Manager vorzuziehen.
- Die Zahl gleichzeitig laufender PowerShell-Prozesse kann relevante CPU-/RAM-Last auf dem Steuer-PC und Netzwerk-Last erzeugen. Mit 10–20 Prüfungen und 3–8 Updates beginnen, erst nach Messung erhöhen.
- `RemoteWorker.ps1` ist als Resource in der EXE eingebettet und wird beim Start temporär auf den Steuer-PC entpackt und beim Beenden gelöscht.
- Das hier ist ein **Quellcode-Prototyp**, kein bereits auf echten Systemen validiertes Produktionswerkzeug.

## Logdateien

`%LOCALAPPDATA%\NovaTouchUpdater\Logs\Update-YYYYMMDD-HHMMSS.log`

## Dateiaufbau

- `Program.cs` — WinForms-Einstieg
- `MainForm.cs` — GUI, Phasen- und Parallelitätssteuerung, Retry, Export
- `WorkerRunner.cs` — PowerShell-Prozessausführung / JSON-Protokoll
- `RemoteWorker.ps1` — WinRM-Probe, Service-Inventar, ZIP-Verteilung, Update und Verifikation
- `NovaTouchUpdater.csproj` — .NET-8-WinForms-Projekt

## Alternative zu PowerShell-Remoting

Die **aktuelle Implementierung benötigt WinRM** auf den Zielhosts. Eine .NET-GUI ersetzt nicht automatisch das Remote-Ausführungsprotokoll. Falls WinRM unerwünscht ist, sind z. B. Windows-Dateifreigaben (SMB) + Remote-Service-Control/Tasks (RPC) oder ein selbst entwickelter Windows-Update-Agent mit HTTPS-Kommunikation möglich. SMB/RPC erfordert ebenfalls administrative Freigaben, Firewall- und Berechtigungskonfiguration. Für zahlreiche, auch sporadisch erreichbare Hosts ist ein lokaler Agent mit Status-Persistenz und HTTPS-Polling langfristig robuster; er muss jedoch zunächst auf allen Zielhosts ausgerollt werden. SSH ist ebenfalls möglich, erfordert aber einen aktivierten SSH-Server pro Zielhost und ist kein unmittelbarer Vorteil gegenüber WinRM im Windows-Umfeld.

## Spaltenbreiten selbst ändern

**Datei:** `MainForm.cs`, Methode **`ApplyColumnWeights()`** (unterhalb von `ConstructUi()`).
Hier stehen die sieben Werte von `FillWeight` (Summe = 100):

`hostname=11`, `connection=9`, `payment=6`, `fiscal=6`,
`information=55`, `attempt=5`, `result=8`.

Die Spalten werden erst im `Shown`-Ereignis, nach dem initialen Fensterlayout,
auf `Fill` umgestellt. Dadurch sind Payment Ist und Fiscal Ist bei gleichem
`FillWeight` **gleich breit**; vorher konnten die Breiten während des Anlegens
aufgrund von `MinimumWidth` und automatischen Größenänderungen abweichen.
Nach dem Ändern der Werte neu kompilieren.

Bei sehr schmalen Fenstern hat `MinimumWidth` Vorrang, die Prozentanteile
können dann abweichen. Das HTML-Layout wird getrennt in `HtmlReport.cs`
über CSS gesteuert.

### Windows-Release-Build

`Build-win64.cmd` im Projektordner ausführen (Visual Studio / .NET-8-SDK notwendig).
Damit wird eine eigenständige `win-x64`-EXE veröffentlicht.

### Single EXE Build

Im Projektordner 
dotnet publish .\NovaTouchUpdater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o ".\publish-single"