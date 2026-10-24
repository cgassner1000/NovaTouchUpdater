# NovaTouch Updater — WinForms / .NET 8

Quellcode für eine eigenständige Windows-GUI zur parallelen Verwaltung von Updates der Windows-Dienste `nt.payment*`, `nt.fiscal*` und aller Instanzen `nt3_server_suite*`, basierend auf den gelieferten PowerShell-Skripten.

## Voraussetzungen

- Visual Studio 2026 mit Workload **.NET-Desktopentwicklung** und .NET 8 SDK
- Adminrechte auf den Ziel-PCs, Windows PowerShell 5.1 auf dem Steuer-PC
- WinRM / PowerShell-Remoting ist auf den Ziel-PCs eingerichtet und erreichbar
- Die vorhandenen Windows-Dienste `nt.payment*`, `nt.fiscal*` und/oder `nt3_server_suite*` sind korrekt registriert
- Ausreichend Platz für ZIP-Übertragung und temporäres Entpacken auf den Ziel-PCs

Der GUI-Updater selbst nutzt **.NET 8** und startet Windows PowerShell 5.1 als Hintergrundprozess. Der darin eingebettete Worker verwendet `New-PSSession`, `Invoke-Command` und `Copy-Item -ToSession`, analog zur ursprünglichen Lösung. Es wird **kein zusätzlicher Agent** auf den Zielrechnern installiert.

Mit **Config speichern** bei der Hostliste können die aktuell geladenen Hosts, der Client-Zielpfad, WinRM-Benutzer, Passwort sowie HTTPS-Option und HTTPS-Port als JSON gespeichert werden. Auch durch den Tabellenfilter ausgeblendete Hosts gehören zur gespeicherten Liste. Der Speicherdialog schlägt `config.json` neben der EXE vor. Diese Datei wird beim nächsten Start automatisch geladen und ist in Git ignoriert. Andere Dateinamen und Ordner können für Sicherungskopien gewählt werden; für das automatische Laden muss die Datei `config.json` neben der EXE liegen.

Das Passwort wird beim Speichern mit **Windows DPAPI / LocalMachine** verschlüsselt und als `EncryptedPassword` abgelegt; `PasswordProtectionScope` kennzeichnet den Schutz. **Alle Windows-Benutzer mit Lesezugriff auf die Config können das Passwort auf demselben Rechner verwenden.** Die Config muss dafür in einem für diese Benutzer lesbaren Ordner liegen. Auf einem anderen Rechner muss das Passwort neu eingegeben und dort gespeichert werden. Das folgende Beispiel enthält noch kein Passwort: Passwort in der Anwendung eingeben und anschließend **Config speichern** verwenden.

```json
{
  "RemotePath": "C:\\install\\Novacom",
  "Username": "DOMAIN\\support",
  "PasswordProtectionScope": "LocalMachine",
  "EncryptedPassword": "",
  "UseSsl": false,
  "HttpsPort": 5986,
  "Hosts": ["POS01", "POS02"]
}
```

Alte Configs mit dem Klartextfeld `Password` bleiben lesbar. Beim Speichern wird dieses Feld entfernt und durch das verschlüsselte Passwort ersetzt. Auch die bisherige benutzergebundene DPAPI-Config bleibt unter dem ursprünglichen Benutzer lesbar: **Einmal mit diesem Benutzer öffnen und erneut speichern**, damit sie anschließend von den anderen Benutzern desselben Rechners verwendet werden kann. Ist ein vorhandenes verschlüsseltes Passwort beschädigt oder nicht entschlüsselbar, werden die anderen Einstellungen trotzdem geladen und eine erneute Passworteingabe angefordert. Eine fehlgeschlagene Speicherung lässt eine vorhandene Datei unverändert. Fehlt `config.json`, startet die Anwendung mit leerer Hostliste und ohne vorausgefüllte Zugangsdaten.

Lokale Tests für Speicherung, DPAPI-Verschlüsselung, Migration und Schreibfehler (nur künstliche Zugangsdaten und Testdateien unter `obj`, keine vorhandene `config.json` wird verändert):

```powershell
dotnet run --project tests\ConfigStore.Tests\ConfigStore.Tests.csproj -c Release
```

Windows 10 Enterprise LTSC 2019 (1809) und Windows Server 2019 unterstützen .NET 8. Der .NET-8-Support endet am 10. November 2026. Die Windows-Desktopanwendung kann self-contained veröffentlicht werden.

## Projekt öffnen

Visual Studio 2026: `NovaTouchUpdater.csproj` öffnen, NuGet/SDK laden, `F5` starten. Für Release:

```powershell
dotnet publish .\NovaTouchUpdater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false
```

Ergebnis im Ordner `bin\Release\net8.0-windows\win-x64\publish\`.

Build und lokale Tests sind unter Windows ausführbar. Die automatisierten Update-Tests verwenden Testordner und simulierte Dienste/WinRM-Aufrufe; sie ersetzen keinen Update-Test auf einem echten Testclient.

## Bedienung

Rechts neben **parallele Remote-Updates** erscheint **Neue Version verfügbar**, wenn ein neueres stabiles [GitHub-Release](https://github.com/cgassner1000/NovaTouchUpdater/releases/latest) mit passender EXE und SHA256-Prüfsumme vorliegt. Der Hinweis steht ganz rechts; bei wenig Platz erhält er eine eigene rechtsbündige Zeile. Die Prüfung läuft beim Start und anschließend alle 30 Minuten im Hintergrund. Ohne Internet oder bei einer fehlgeschlagenen GitHub-Abfrage erscheint kein störender Startdialog.

Ein Klick fragt mit **Ja/Nein**, ob die neue Updater-Version verwendet werden soll. Nach **Ja** wird die eigenständige EXE mit Fortschrittsanzeige heruntergeladen und anhand von Größe und SHA256 geprüft. **Abbrechen** beendet die Vorbereitung, ohne die bisherige EXE zu verändern. Erst nach einem erfolgreichen Download und einem bereiten lokalen Updatehelfer schließt sich das Fenster. Der Helfer wartet auf das Programmende, ersetzt die EXE am bisherigen Pfad und öffnet die neue Version. Der Dateiname bleibt erhalten, damit Verknüpfungen weiter funktionieren. Die alte EXE wird bis zum erfolgreichen Startaufruf als Sicherung behalten und danach entfernt; schlägt der Austausch oder Startaufruf fehl, bleibt die bisherige EXE erhalten bzw. wird wiederhergestellt. `config.json` und andere Dateien im Programmordner bleiben unverändert. Nicht gespeicherte Einstellungen gehen beim Neustart verloren, worauf die Bestätigung hinweist.

Das automatische Aktualisieren setzt die **eigenständige Release-EXE** und Schreibzugriff auf ihren Ordner voraus. Ein Visual-Studio-/DLL-Build wird nicht durch eine einzelne EXE ersetzt. Während eines Remote-Durchlaufs ist der Hinweis deaktiviert; während der Vorbereitung des Updater-Neustarts können keine neuen Remote-Aktionen gestartet werden. Fehler nach dem Schließen werden vom Helfer mit Meldung und Protokollpfad angezeigt.

Lokale Tests verwenden simulierte HTTP-Antworten, Testdateien und simulierte Programmstarts; sie aktualisieren weder den laufenden Updater noch Remote-Clients:

```powershell
dotnet run --project tests\AppRelease.Tests\AppRelease.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\SelfUpdate.Tests.ps1
```

Die Oberfläche berücksichtigt die Windows-Anzeigeskalierung pro Monitor (PerMonitorV2). Buttonleisten und Optionen brechen bei wenig Platz um; Zeilenhöhen richten sich nach dem Inhalt. Bei großen Schriften oder schmalen Fenstern stehen Dateiauswahl und Einstellungen untereinander. Reicht die Bildschirmfläche nicht aus, lassen sich die Inhalte im Haupt- und WinRM-Fenster scrollen.

Zur Prüfung der Darstellung die Anwendung bei 100 %, 125 %, 150 % und 200 % Windows-Skalierung öffnen, das Fenster verkleinern und wieder vergrößern sowie bei mehreren Monitoren zwischen den Bildschirmen verschieben.

1. Hostnamen-TXT laden: ein Hostname/IP pro Zeile; Leerzeilen und Zeilen mit `#`/`;` werden ignoriert, Duplikate entfernt.
2. `nt.payment_VERSION*.zip`, `nt.fiscal_VERSION*.zip` und/oder `nt.server.suite-VERSION.zip` wählen (die Auswahlfelder stehen direkt untereinander). Beispiele: `nt.payment_2026.2.9546.5587.zip`, `nt.server.suite-2026.4.9792.zip`. Die Server-Suite-Auswahl steht unter Fiscal; ihre ZIP muss die gefüllten Ordner `apps\nt3-server-suite` und `installer\python\portable_python` enthalten.
   **Eine einzige ZIP genügt:** Nur Payment, nur Fiscal oder nur Server Suite können unabhängig aktualisiert werden. Dienste und Installationsdateien der nicht ausgewählten Komponenten bleiben auch bei Force unverändert. Bei einer ausgewählten Server-Suite-ZIP werden alle zugehörigen Instanzen des Hosts berücksichtigt.
3. Den `Zielpfad auf den Clients` bei Bedarf anpassen (Standard: `C:\install\Novacom`). Dort liegen die Update-ZIPs und temporären Update-Dateien. Wenn andere Windows-Anmeldedaten benötigt werden, Benutzername und Passwort eingeben. Sonst wird das aktuelle Windows-Konto genutzt. Option `HTTPS` aktiviert WinRM/TLS. Im Feld **HTTPS-Port** kann der Port zwischen 1 und 65535 eingestellt werden, beispielsweise **443** statt des Standards **5986**. Die Einstellung gilt für Prüfen, Upload, Updates, Befehle, Test WinRM und Verbindungsüberwachung. Ohne HTTPS wird weiterhin **HTTP/5985** verwendet. Optional setzt `"HttpsPort": 443` zusammen mit `"UseSsl": true` in `config.json` diese Werte beim Start; ohne Portangabe bleibt 5986 vorbelegt. Die Einstellung wählt den vorhandenen WinRM-Endpunkt aus und ändert keine Listener oder Firewallregeln auf dem Client.
4. `Installationsdateien nicht übertragen` überspringt den ZIP-Transfer. Die lokal ausgewählten ZIPs werden weiterhin für Zielversion und SHA256 verwendet. Die passenden ZIP-Dateien müssen im eingestellten Zielpfad auf jedem Client unter demselben Dateinamen liegen; der Updater prüft sie vor dem Entpacken auf identische SHA256-Prüfsumme. Installierte Dienstversionen werden weiterhin mit den Zielversionen aus den lokalen ZIP-Dateinamen verglichen.
5. `1. Prüfen + Versionen`: alle Hosts zuerst per WinRM prüfen (Standard: bis zu 40 parallel), dann .NET Framework 4.8, die installierte NovaTouch-POS-Version sowie vorhandene Dienste lesen. Payment/Fiscal verwenden ProductVersion/FileVersion der Dienst-EXE. Die Server-Suite-Version stammt je Instanz ausschließlich aus `apps\nt3-server-suite\version.txt`; die Version der Python-EXE wird dafür nicht verwendet. Diese Quelle gilt auch für Tabelle, HTML-Bericht, Versionsvergleich und Verifikation nach einem Update.
   Fehlende Payment-, Fiscal- oder Server-Suite-Dienste sind beim Prüfen kein Fehler, auch wenn alle fehlen. In den jeweiligen IST-Spalten und im HTML-Bericht steht **Nicht installiert**; .NET und POS werden trotzdem geprüft. Jeder Dienst mit dem Präfix `nt3_server_suite` wird einzeln erfasst, einschließlich des Namens ohne weiteren Suffix. Unterschiedliche Server-Suite-Versionen bleiben sichtbar; der Tooltip nennt alle Instanzen mit Dienstnamen und Pfaden. Bei registrierter Suite-Instanz ohne `version.txt` steht **Version unbekannt**, damit ein Update fehlende Dateien und Zielordner ergänzen kann. Verbindungsfehler und andere Lesefehler bleiben Fehler. Nach einem fehlgeschlagenen Scan steht bei den Dienstversionen **–** statt einer vermeintlich fehlenden Installation.
   Der RemoteWorker schließt jede PowerShell-Remotesitzung am Ende der Aktion automatisch (auch bei Fehlern); ein zusätzlicher Schließen-Button ist daher nicht erforderlich.
6. Optional `Force Update` aktivieren: Normalmodus überspringt Komponenten mit IST-Version = Zielversion; Force installiert die **ausgewählten** Komponenten trotzdem erneut. Nicht ausgewählte Komponenten bleiben unangetastet. Sobald eine Server-Suite-Instanz abweicht, werden alle Server-Suite-Instanzen des Hosts gemeinsam aktualisiert. Eine dreiteilige Suite-Zielversion wie `2026.4.9792` akzeptiert auch einen passenden vierteiligen Eintrag in `version.txt` wie `2026.4.9792.0`. Die Zielversion wird auch bei Force nach Installation verifiziert.
7. `2. Updates starten`: Updates auf den erfolgreichen Scan-Hosts (Standard: 8 gleichzeitig) mit **maximal drei vollständigen Versuchen pro Host**. Die beiden Optionen `Prüfen parallel` und `Update parallel` begrenzen unabhängig voneinander die Zahl gleichzeitig bearbeiteter Rechner (Vorgaben: 40 bzw. 8). Bestätigungsdialog vor dem Start.
8. `Fehler erneut versuchen` berücksichtigt Hosts mit Prüf-/Updatefehlern und prüft bei Bedarf erst ihre Erreichbarkeit. Eine früher fehlgeschlagene Prüfung oder ein fehlgeschlagenes Update bleibt auch nach einem erfolgreichen Dienst- oder Terminalneustart wiederholbar. Ein fehlgeschlagener Befehl allein löst dagegen keinen Updateversuch aus. Erfolgreich aktualisierte und bereits aktuelle Hosts werden nicht neu aktualisiert. Der erneute Durchlauf hat wieder bis zu drei Versuche pro Host.
9. Doppelklick auf eine Zeile öffnet das Hostprotokoll; `HTML-Bericht erstellen` öffnet sofort einen eigenständigen, offline nutzbaren Bericht im Standardbrowser – ohne vorherigen Speicherdialog. Über „HTML speichern“ im Bericht oder Strg+S im Browser kann er dauerhaft gespeichert werden; PDF-Export per Drucken ist ebenfalls möglich. SHA256-Werte werden in GUI und Bericht ausgeblendet, die Integritätsprüfung bleibt aktiv.
10. `Upload` öffnet eine Dateiauswahl und überträgt die ausgewählten Dateien an **alle Hosts der Liste** in den eingestellten Zielpfad. Vorhandene Dateien werden nur bei abweichender SHA256-Prüfsumme ersetzt. Diese Aktion installiert nichts und funktioniert unabhängig von `Installationsdateien nicht übertragen`.
11. `Befehl senden` öffnet einen breiten, vergrößerbaren Dialog mit erklärten Aktionsbereichen. **nt.payment – Zahlungsdienst neu starten** und **nt.fiscal – Fiskaldienst neu starten** stoppen und starten die jeweiligen Dienste. **nt3 Server Suite – starten / neu starten** startet die `nt3_server_suite`-Dienste; laufende Dienste werden vorher gestoppt. Mehrere Dienstaktionen können gemeinsam ausgewählt werden. **POS neustart – KassaPreh neu starten** und **Terminal NEUSTART – Windows neu starten** sind als eigene Bereiche hervorgehoben. Jede dieser beiden Aktionen wird einzeln ausgeführt und kann weder mit der anderen noch mit Dienstaktionen kombiniert werden. Die aktuelle Auswahl steht oberhalb der Schaltflächen; ohne Auswahl ist das Ausführen deaktiviert.

Vor dem Upload wird der Zielordner auf jedem betroffenen Client geprüft. Bei nicht leeren Ordnern zeigt eine gemeinsame Abfrage Host, aufgelösten Zielpfad und Anzahl vorhandener Einträge. **Ja, bereinigen und hochladen** entfernt den gesamten Inhalt der bereinigbaren Uploadordner einschließlich versteckter Dateien und Unterordner; der Zielordner selbst bleibt bestehen. **Ohne Bereinigung hochladen** ist die Voreinstellung und behält vorhandene Inhalte bei. **Abbrechen** beendet den Vorgang vor der Bereinigung und Übertragung. Systemverzeichnisse sowie erkannte Payment-/Fiscal-/Server-Suite-Installationsordner und deren überlappende Pfade sind für die Bereinigung gesperrt und werden im Dialog markiert. Verknüpfungen/Junctions werden abgewiesen. Hat sich der Ordnerinhalt seit der Abfrage geändert, wird die Bereinigung für den Host abgebrochen. Die Prüfung gilt auch für Update-Uploads; bereits aktuelle Hosts und Updates mit deaktivierter ZIP-Übertragung bereinigen nichts. Prüfung und Bereinigung werden im Hostprotokoll und damit im HTML-Bericht dokumentiert.

**POS neustart** gilt wie der Terminalneustart für **alle eingetragenen Hosts**, unabhängig von Filter, Scan oder ZIP-Auswahl. Läuft `C:\Program Files (x86)\novatouch\bin\KassaPreh.exe` bereits, wird sie nach einer zusätzlichen Bestätigung gezielt per `taskkill /PID … /F` beendet und unter dem ursprünglichen Benutzer in derselben Windows-Sitzung gestartet. **Läuft sie nicht, wird die EXE aus diesem Standardverzeichnis unter dem eindeutig angemeldeten Benutzer gestartet.** Die Benutzersitzung wird dabei über die Windows-Sitzungsverwaltung ermittelt, unabhängig von Explorer oder einem laufenden Kassenprozess. Auch eine getrennte, weiterhin angemeldete Sitzung wird berücksichtigt. Bei mehreren angemeldeten Sitzungen erfolgt kein Start auf Verdacht. Die eingestellten WinRM-Anmeldedaten, HTTPS und `Update parallel` gelten auch hier. Windows und die Dienste werden nicht neu gestartet. Bei einem vorhandenen Prozess werden Benutzer und ursprüngliche Startparameter vor dem Beenden geprüft; ein erstmaliger Start erfolgt ohne zusätzliche Parameter. Arbeitsverzeichnis ist der `bin`-Ordner. Die temporäre Windows-Aufgabe wird vor dem Beenden vorbereitet und über ein [interaktives Benutzertoken in der konkreten Sitzung](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex) gestartet; ein Benutzerkennwort wird dafür nicht gespeichert.

Fehlt die Datei oder ist beim erstmaligen Start keine eindeutige angemeldete Benutzersitzung vorhanden, erscheint **Übersprungen** mit dem Grund. Ein vorhandener Kassenprozess ohne interaktive Benutzersitzung wird ebenfalls übersprungen. Bei mehreren passenden Prozessen oder nicht eindeutig lesbarem Benutzer/Pfad wird vor dem Beenden abgebrochen und ein Fehler angezeigt. Eine vor dem erstmaligen Start gewechselte Benutzeranmeldung wird erneut geprüft. Ein bereits durch einen Watchdog oder während der Vorbereitung gestarteter Prozess wird geprüft, ohne einen zweiten Start anzufordern. **POS OK** bestätigt den Prozess unter dem richtigen Benutzer in der vorgesehenen Sitzung und die Löschung der temporären Aufgabe; die Bedienbarkeit der Kasse wird dadurch nicht geprüft. Die Aufgabe wird auch nach Fehlern entfernt; schlägt die Bereinigung nach drei Versuchen fehl, wird der konkrete Aufgabenname als Fehler protokolliert. Die [Löschung der Aufgabe beendet die laufende Anwendung nicht](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/schtasks-delete). Ergebnisse und Details erscheinen auch im HTML-Bericht. `Abbrechen` stoppt weitere Hosts; aktive POS-Neustarts schließen die Startprüfung und Bereinigung ab.

Die Windows-Aktion gilt für **alle eingetragenen Hosts**, unabhängig von Tabellenfilter, vorherigem Scan oder ausgewählten ZIP-Dateien, und kann nicht mit Dienstaktionen kombiniert werden. Nach einer zusätzlichen Bestätigung wird pro Host über WinRM ein Windows-Neustart mit 30 Sekunden Vorlauf angefordert; die eingestellten Anmeldedaten, HTTPS und `Update parallel` gelten auch hier. Windows schließt dabei geöffnete Anwendungen; nicht gespeicherte Daten können verloren gehen. Die Umsetzung nutzt [`shutdown /r /t 30`](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/shutdown).

Nach bestätigtem Neustartauftrag steht beim Host **Windows-Neustart angefordert (30 Sekunden)** und im Ergebnis **Neustart angefordert**. Sobald die automatische Verbindungsprüfung nach dem Vorlauf eine erfolgreiche WinRM-Anmeldung meldet, lautet das Ergebnis **Wieder online**. Während des Neustarts bedeutet eine unterbrochene Verbindung keinen fehlgeschlagenen Auftrag. Die Anzeige bestätigt die Erreichbarkeit, ohne Windows-Startzeit oder Softwareversionen zu prüfen; vor weiteren Updates erneut `Prüfen + Versionen` ausführen. Ein Dienstbefehl endet mit **Dienste OK** oder **Fehler**. Jeder neue Befehl ersetzt das zuvor angezeigte Ergebnis; frühere Fehler bleiben im Protokoll. Befehle werden in der Zusammenfassung und im HTML-Bericht separat von Softwareupdates gezählt. `Abbrechen` stoppt die Vergabe weiterer Aufträge und wartet auf die Rückmeldungen bereits gestarteter Neustartanforderungen. Bereits an Windows übermittelte Neustartaufträge werden dadurch nicht zurückgenommen.

Test der Befehlsaktionen mit simulierten WinRM-, Dienst- und Shutdown-Aufrufen; dabei werden keine Rechner neu gestartet:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\RemoteCommand.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\PosRestart.Tests.ps1
dotnet run --project tests\CommandDialog.Tests\CommandDialog.Tests.csproj -c Release
```

Die Hostübersicht zeigt die **IST-Versionen** von Payment, Fiscal und Server Suite. Die für alle Hosts identischen Zielversionen stehen als **Payment Target**, **Fiscal Target** und **Server Suite Target** bei der ZIP-Auswahl und werden automatisch aus dem ZIP-Dateinamen gelesen.

Die Spalte **Verbindung** prüft ab dem Öffnen des Hauptfensters alle eingetragenen Hosts im Hintergrund ungefähr alle **10 Sekunden** mit einer WinRM-Anmeldung. Sie verwendet die eingestellten Anmeldedaten, die HTTPS-Auswahl und den HTTPS-Port. **Verbunden** erscheint grün, **Nicht erreichbar** rot und **Neustart angefordert** während des Vorlaufs braun. **Unbekannt** bedeutet, dass die Hintergrundprüfung selbst kein verlässliches Ergebnis liefern konnte. Der Tooltip zeigt die Uhrzeit der letzten Prüfung und Details; der Filter „Nicht erreichbar“ passt sich automatisch an. Statuswechsel werden im Hostprotokoll erfasst, gleichbleibende Ergebnisse erzeugen keine wiederholten Logzeilen. Der HTML-Bericht übernimmt den Zustand und die letzte Prüfzeit zum Exportzeitpunkt.

Während eines manuellen `Prüfen + Versionen` pausiert die Hintergrundprüfung, danach läuft sie wieder weiter. Beim Bearbeiten der Zugangsdaten und bei einem Benutzernamen ohne Passwort wird keine neue automatische Prüfung gestartet. Update-Ergebnisse und Versionsangaben werden durch die Hintergrundprüfung nicht geändert. Nur ein ausstehender Neustartauftrag wechselt bei bestätigter Erreichbarkeit nach dem Vorlauf auf **Wieder online**, einschließlich des Informationstexts. Die Prüfungen verwenden `Prüfen parallel` mit höchstens 40 gleichzeitigen Verbindungen und einem Zeitlimit pro Host. Eine noch laufende Runde wird nicht überlappt; bei vielen oder langsam antwortenden Hosts kann das Intervall deshalb länger ausfallen. Beim Schließen der Anwendung wird die Überwachung beendet.

Tests der Verbindungsüberwachung mit simulierten Antworten, ohne Verbindung zu Zielhosts:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\ConnectionWorker.Tests.ps1
dotnet run --project tests\ConnectionState.Tests\ConnectionState.Tests.csproj -c Release
dotnet run --project tests\HostState.Tests\HostState.Tests.csproj -c Release
```

Die Spaltenreihenfolge lautet: **Hostname | Verbindung | .NET 4.8 | Payment Ist | Fiscal Ist | Server Suite Ist | NovaTouch POS | Information | Versuch | Ergebnis**. Die schmale .NET-Spalte zeigt **✓** bei installiertem .NET Framework 4.8 oder neuer (auch 4.8.1), **✗** bei fehlendem/älterem Framework und **–** vor einer Prüfung bzw. bei nicht ermitteltem Status. Grundlage ist der Registry-Wert `Release >= 528040` unter `NET Framework Setup\NDP\v4\Full` ([Microsoft-Dokumentation](https://learn.microsoft.com/en-us/dotnet/framework/install/how-to-determine-which-versions-are-installed)). Die POS-Version stammt aus `DisplayVersion` der Programmeinträge mit Namen `NovaTouch POS` unter den 32- und 64-Bit-Uninstall-Schlüsseln in `HKLM`. Fehlt POS, steht dort **Nicht installiert**; das allein verursacht keinen Fehler und verhindert kein Update. Bei registrierter Installation ohne Versionsangabe steht **Version unbekannt**. Beim erneuten Prüfen werden alte Ergebnisse zurückgesetzt. Beide Angaben stehen auch im HTML-Bericht.

Die breite Spalte „Information“ zeigt den aktuellen Bearbeitungsschritt **zusätzlich** zu vorhandenen Fehler- und Informationstexten an. „Versuch“ und „Ergebnis“ sind kompakt; „Ergebnis“ steht ganz rechts. Im HTML-Bericht bleibt der aufklappbare Protokollbereich als zusätzliche Spalte **vor** „Versuch“ und „Ergebnis“ erhalten.

Die zusätzlichen Scan-Prüfungen lassen sich ohne Netzwerkzugriff mit simulierten Registry- und WinRM-Antworten testen:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\SoftwareInventory.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\ServiceInventory.Tests.ps1
```

## HTML-Bericht

Der HTML-Bericht wird beim Klick auf `HTML-Bericht erstellen` zunächst in einem temporären Vorschaupfad abgelegt und automatisch im Standardbrowser geöffnet (kein Internetzugang nötig). Der Button „HTML speichern“ im Bericht speichert die HTML-Datei auf Wunsch. Enthalten: Datum/Uhrzeit, Zielversionen, Gesamt-/Erfolg-/Fehlerzahlen, Hostliste inklusive Vorher-/Nachher-Versionen, Suche/Statusfilter sowie detaillierte Hostprotokolle. Die Server Suite steht direkt nach Fiscal; alle erfassten Suite-Instanzen werden mit Dienstnamen und Version aufgeführt. Die dynamischen Inhalte werden HTML-escaped. Ein Bericht erfasst den aktuellen Zustand der Anwendung zum Zeitpunkt des Exports.

## Ablauf je Host

1. WinRM-Sitzung aufbauen.
2. Aus der Registry des jeweiligen Dienstes `ImagePath` ermitteln; bei Payment/Fiscal EXE-Versionen auslesen, bei jeder Server-Suite-Instanz die zugehörige `version.txt`.
3. Nur bei abweichender Zielversion (oder bei aktiviertem Force Update): ZIP-Datei in den konfigurierten Zielpfad auf dem Client übertragen und per SHA256 prüfen. Bei aktivierter Option `Installationsdateien nicht übertragen` muss die gleichnamige, identische ZIP dort bereits vorhanden sein; sie wird nur geprüft und nicht kopiert.
4. ZIP in temporären Unterordner am Zielrechner entpacken. Für die Suite müssen die gefüllten Ordner `apps\nt3-server-suite` und `installer\python\portable_python` sowie `apps\nt3-server-suite\version.txt` mit passender Zielversion vorhanden sein. Die registrierte Dienst-EXE muss innerhalb von `portable_python` liegen. Eine bereits auf dem Client vorhandene Dienst-EXE muss nicht in der ZIP enthalten sein und bleibt beim Kopieren erhalten, wenn das Paket keine gleichnamige Datei mitliefert. Fehlt die Dienst-EXE auf dem Client und unter ihrem relativen Pfad in der ZIP, wird dies vor dem Dienststopp gemeldet. Die Python-EXE-Version wird nicht geprüft. Ungültige Pakete werden vor dem Dienststopp abgelehnt.
5. Alle betroffenen Dienste stoppen, bevor Dateien verändert werden. Payment/Fiscal werden wie bisher über ihre Installationsordner kopiert. Bei der Suite werden Dateien aus `apps\nt3-server-suite` in `<Installation>\apps\nt3-server-suite` und Dateien aus `installer\python\portable_python` in `<Installation>\portable_python` kopiert. **Client-Zielordner werden nicht gelöscht.** Gleichnamige Dateien werden überschrieben; fehlende Dateien, Unterordner und Zielordner werden angelegt. Zusätzliche lokale Dateien bleiben erhalten. Der Client-Ordner `portable_python` liegt auf derselben Ebene wie `apps`. Andere `apps`- und `installer`-Inhalte werden nicht übertragen. Gemeinsam genutzte Suite- und Python-Ordner werden jeweils einmal beschrieben; unterschiedliche Installationen erhalten ihre eigenen Kopien. Erst nach allen Kopiervorgängen werden alle betroffenen Dienste wieder gestartet (außer deaktivierte). Auch bei Fehlern wird der Wiederanlauf aller nicht deaktivierten Dienste versucht.
6. Bei Payment/Fiscal Produkt-/Dateiversion, bei jeder Suite-Instanz `version.txt` und den Zustand der gestarteten Dienste erneut kontrollieren.
7. Bei Fehlern maximal zwei weitere Male mit kurzer Wartezeit erneut versuchen. Bereits aktuelle Komponenten werden übersprungen; bei einem Suite-Update werden alle Suite-Instanzen gemeinsam gestoppt, ihre Dateien aktualisiert und die Instanzen verifiziert. Sind keine ausgewählten Dienste installiert, wird der Host ohne Fehler übersprungen.
8. Status und Fehlermeldung pro Host anzeigen und protokollieren. Während des Updates werden Vorbereitung, Dienststopp, Installation der Dateien, Dienststart und Verifikation getrennt live angezeigt. Beim Stoppen und Starten steht der jeweilige Dienstname dabei; beim Installieren die Komponente und ihr Zielordner. Diese Meldungen erscheinen auch im Hostprotokoll und damit im HTML-Bericht.

Die Informationszelle zeigt den Fortschritt des aktuellen Arbeitsschritts mit einem hellgrünen Balken hinter dunklem Text und einer Prozentangabe. Beim Upload läuft **jede Datei einzeln von 0 bis 100 %**; die Anzeige übernimmt die Zwischenstände der übertragenen Datenblöcke aus PowerShell. Dateiname und laufende Nummer bleiben sichtbar. Erst die erfolgreiche SHA256-Prüfung bestätigt 100 %; identische vorhandene Dateien werden weiterhin ohne erneuten Upload verifiziert. Auch beim Entpacken startet **jede ZIP einzeln bei 0 %**. Hier zählt die bereits geschriebene, entpackte Datenmenge der aktuellen ZIP; Zwischenstände erscheinen auch innerhalb eines großen ZIP-Eintrags. Die Meldung nennt ZIP-Name, laufende Nummer, Datenmenge und aktuellen Eintrag. Beim Installieren zählen die fertig kopierten Dateien über alle ausgewählten Zielordner; während einer einzelnen Datei bleibt der zuletzt bestätigte Stand sichtbar. Beim Update erreichen alle bestätigten Dienststopps zusammen 50 %, alle vorgesehenen Dienststarts zusammen 100 %. Ein einzelner Dienstneustart unter „Befehl senden“ zeigt 50 % nach dem Stopp und 100 % nach dem erfolgreichen Start. Ein neuer Schritt setzt die Anzeige passend zurück; Fehler erhalten keinen grünen Abschlussbalken.

Die Suite-Dateistruktur muss beispielsweise so aussehen:

```text
nt.server.suite-2026.4.9792.zip
├── apps
│   └── nt3-server-suite
│       ├── version.txt (z. B. 2026.4.9792)
│       └── <Dateien der Server Suite>
└── installer
    └── python
        └── portable_python
            └── <Dateien der Python-Laufzeit>
```

Für jeden Dienst mit dem Präfix `nt3_server_suite` wird der registrierte `ImagePath` aus der Windows-Dienstregistrierung gelesen. Aus der EXE unter `…\portable_python\…\dienst.exe` wird genau deren Installationsverzeichnis abgeleitet. Beispiel: `D:\InstanzB\portable_python\python.exe` ergibt `D:\InstanzB\apps\nt3-server-suite` und `D:\InstanzB\portable_python`. Dienstargumente und die Suche nach anderen Installationen beeinflussen diese Zuordnung nicht. Bei der Installation erzeugte Dienstdateien wie `nt3_server_suite_HKS.exe` dürfen in der ZIP fehlen: Sie bleiben ebenso wie andere zusätzliche Clientdateien erhalten, während gleichnamige ZIP-Dateien überschrieben und fehlende Dateien ergänzt werden. Abweichende Inventarpfade werden vor dem Kopieren abgewiesen. Jede Instanz wird mit Dienstnamen, EXE- und Suite-Pfad im Protokoll aufgeführt; nach dem Update werden Version und Status je Instanz protokolliert. Das Hostprotokoll steht auch im HTML-Bericht.

Eine fehlende `version.txt` ergibt **Version unbekannt**, sodass ein Update fehlende Dateien ergänzen kann. Unlesbare oder ungültige Versionsdateien und Dienst-EXE-Pfade ohne `portable_python` melden einen konkreten Lesefehler; es gibt keinen Rückgriff auf die Python-Version. Ebenso werden überlappende Installationsziele, Junctions und Verknüpfungen in beiden Zielpfaden abgelehnt. Der konfigurierte Client-Zielpfad ist nur die Ablage für ZIP und temporäres Entpacken; die registrierten Dienst-EXE-Pfade bestimmen die Installationsziele.

Test des vollständigen Update-Ablaufs mit lokalen Testordnern sowie simulierten Diensten und Remoting (keine Zielhosts werden angesprochen):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\ServerSuiteUpdate.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\UploadDirectory.Tests.ps1
```

## Grenzen und Sicherheitsaspekte

- **Kein automatisches Rollback**: Werden während des Kopierens Dateien verändert und der Vorgang scheitert, wiederholt der Updater den Versuch. Nach endgültigem Fehler muss der betroffene Host ggf. manuell repariert werden.
- ZIP-Inhalte werden über vorhandene Installationen kopiert. Auch bei der Server Suite bleiben die Zielordner `apps\nt3-server-suite` und `portable_python` sowie zusätzliche lokale Dateien erhalten. Nur im Paket vorhandene gleichnamige Dateien werden überschrieben; neue Dateien und Ordner werden angelegt. Bei einem reinen Payment-/Fiscal-Update bleibt `portable_python` unverändert.
- Dienstneustarts unterbrechen die laufenden Dienste. Vor Produktiveinsatz Wartungsfenster berücksichtigen.
- `Abbrechen` stoppt das Einplanen weiterer Hosts. Ein bereits gestarteter **Update-Schritt** darf zunächst Stop/Kopie/Start abschließen, damit Dienste nicht mitten im Update hängen bleiben.
- `Verbindung` testet die tatsächlich benötigte WinRM-Anmeldung (nicht nur ICMP-Ping); somit werden Hosts mit blockiertem ICMP nicht unnötig aussortiert.
- Das Kennwort wird nicht in Argumentlisten oder Logdateien gespeichert. Für die Übertragung vom GUI-Prozess an den lokalen Worker wird JSON über dessen **StandardInput** verwendet; dabei ist das Kennwort temporär als String im Speicher. Für besonders restriktive Sicherheitsumgebungen wären integrierte Windows-Anmeldung / Credential Manager vorzuziehen.
- Die Zahl gleichzeitig laufender PowerShell-Prozesse kann relevante CPU-/RAM-Last auf dem Steuer-PC und Netzwerk-Last erzeugen. Mit 10–20 Prüfungen und 3–8 Updates beginnen, erst nach Messung erhöhen.
- `RemoteWorker.ps1` und seine Funktionen aus `UpdateSteps.ps1` und `UploadDirectory.ps1` sind als Ressourcen in der EXE eingebettet und werden als gemeinsamer temporärer Worker auf dem Steuer-PC gestartet und beim Beenden gelöscht.
- Das hier ist ein **Quellcode-Prototyp**, kein bereits auf echten Systemen validiertes Produktionswerkzeug.

## Logdateien

`%LOCALAPPDATA%\NovaTouchUpdater\Logs\Update-YYYYMMDD-HHMMSS.log`

## Dateiaufbau

- `Program.cs` — WinForms-Einstieg
- `MainForm.cs` — GUI, Phasen- und Parallelitätssteuerung, Retry, Export
- `WorkerRunner.cs` — PowerShell-Prozessausführung / JSON-Protokoll
- `AppConfigStore.cs` — Config laden und atomar speichern, Passwortschutz über Windows DPAPI
- `RemoteWorker.ps1` — WinRM-Probe, Service-Inventar, ZIP-Verteilung, Update und Verifikation
- `UpdateSteps.ps1` — entpackte Pakete prüfen, Dienste gemeinsam stoppen, Installationsordner aktualisieren und Dienste wieder starten
- `UploadDirectory.ps1` — Uploadordner prüfen und nach Bestätigung ausschließlich ihren Inhalt bereinigen
- `UploadDirectoryDialog.cs` — Abfrage mit Hostnamen, Zielpfaden und Auswahl zwischen Bereinigen, Beibehalten und Abbrechen
- `NovaTouchUpdater.csproj` — .NET-8-WinForms-Projekt

## Alternative zu PowerShell-Remoting

Die **aktuelle Implementierung benötigt WinRM** auf den Zielhosts. Eine .NET-GUI ersetzt nicht automatisch das Remote-Ausführungsprotokoll. Falls WinRM unerwünscht ist, sind z. B. Windows-Dateifreigaben (SMB) + Remote-Service-Control/Tasks (RPC) oder ein selbst entwickelter Windows-Update-Agent mit HTTPS-Kommunikation möglich. SMB/RPC erfordert ebenfalls administrative Freigaben, Firewall- und Berechtigungskonfiguration. Für zahlreiche, auch sporadisch erreichbare Hosts ist ein lokaler Agent mit Status-Persistenz und HTTPS-Polling langfristig robuster; er muss jedoch zunächst auf allen Zielhosts ausgerollt werden. SSH ist ebenfalls möglich, erfordert aber einen aktivierten SSH-Server pro Zielhost und ist kein unmittelbarer Vorteil gegenüber WinRM im Windows-Umfeld.

## Spaltenbreiten selbst ändern

**Datei:** `MainForm.cs`, Methode **`ApplyColumnWeights()`** (unterhalb von `ConstructUi()`).
Hier stehen die zehn Werte von `FillWeight` (Summe = 100):

`hostname=11`, `connection=9`, `dotnet48=3`, `payment=6`, `fiscal=6`, `serversuite=8`, `pos=9`,
`information=35`, `attempt=5`, `result=8`.

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
### WinRM separat testen

Der Button **Test WinRM** prüft alle importierten Hosts mit der unter **Prüfen parallel** eingestellten Parallelität und den vorhandenen Anmeldedaten (leerer Benutzername: aktuelle Windows-Anmeldung). Geprüft wird ausschließlich das ausgewählte Protokoll: HTTP/5985 oder HTTPS auf dem eingestellten HTTPS-Port (Standard 5986, beispielsweise 443). Protokoll und tatsächlich verwendeter Port stehen im Diagnosefenster; verschiedene Hosts laufen parallel.

Das separate Ergebnisfenster zeigt TCP-Erreichbarkeit, `New-PSSession` und `Invoke-Command` jeweils einzeln. Eine ausgewählte Zeile zeigt vollständige Fehlerdetails. Ohne erfolgreiche Sitzung wird die Befehlsprüfung ausdrücklich als übersprungen ausgewiesen. Die TLS-Zertifikatsprüfung bleibt aktiv. Pro Protokoll gilt ein Gesamtzeitlimit von 60 Sekunden; der TCP-Test wartet maximal 5 Sekunden.

Der Remote-Testbefehl gibt lediglich einen konstanten Text zurück. Es werden keine Dateien, Dienste oder Einstellungen auf den Zielrechnern verändert. Sitzungen werden nach dem Test entfernt. **Abbrechen** beendet lokale Testprozesse und verhindert weitere Tests. Scan-, Update- und Wiederholungsstatus bleiben unverändert.

Lokaler Test der Diagnose-Fehlerpfade ohne Netzwerkzugriff:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\WinRmWorker.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\WinRmPort.Tests.ps1
```
