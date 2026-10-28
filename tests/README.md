# Automatische Prüfungen

Die Paketbereitstellung lässt sich mit `MainForm.Tests --staging` und `tests/powershell/PackageStaging.Tests.ps1` getrennt prüfen. Die Oberfläche erhält simulierte Hosts; der Worker verwendet ausschließlich Testordner und simulierte Remote-Sitzungen. Geprüft werden die reine Versionsprüfung ohne Kopien, lokale Paketablage erst im bestätigten Update-Ablauf, gemeinsame ZIPs, externe CAB-Dateien, SHA256-Wiederverwendung, Pfadgrenzen und lokale Wiederholungen nur ausstehender Komponenten. Vollständige Remote-Paketablagen werden bereits vor einem Verbindungsaufbau abgewiesen. Der bestehende komponentenbezogene Remote-Updateablauf behält „Nicht übertragen“ und Force. Es werden keine Installer oder echten Dienstaktionen ausgeführt.

Dieser Ordner enthält Entwicklungsprüfungen. Sie erkennen unbeabsichtigte Änderungen beim Umbauen des Codes, etwa falsche Versionsvergleiche, verlorene Dateien beim Server-Suite-Update oder fehlerhafte Statusanzeigen. Die fertige `NovaTouchUpdater.exe` benötigt diesen Ordner nicht; die Tests werden nicht in die Anwendung eingebaut.

## Aufteilung

| Ordner | Inhalt |
| --- | --- |
| `dotnet/` | C#-Testprogramme für Hostzustände, Verbindungen, Konfigurationsspeicherung, LDAP-Abfragen, Selbstaktualisierung und Oberfläche |
| `powershell/` | Tests der Worker, ZIP-Verarbeitung, Dienstverwaltung, POS-Neustarts und Release-Versionierung |
| `Shared/` | Gemeinsame Pfade zum Quellcode und zu isolierten Testdateien |

Die Tests verwenden künstliche Hosts, Testdateien und simulierte Dienste bzw. HTTP-Antworten. Sie laden keine persönliche `config.json` und führen keine Wartungsaktionen auf echten Clients aus. Oberflächentests erzeugen verborgene Steuerelemente; der native Domänendialog wird nicht geöffnet. Konfigurationstests prüfen Windows DPAPI mit eigens erzeugten Testdaten unter der aktuellen Windows-Identität.

## Ausführen

Alle Befehle im Hauptordner des Repositorys ausführen. Voraussetzungen sind Windows und das .NET-SDK aus den Projektvoraussetzungen. Die PowerShell-Prüfungen laufen jeweils in einem eigenen Windows-PowerShell-Prozess, damit simulierte Funktionen nicht zwischen Tests bestehen bleiben.

Anwendung und alle C#-Testprogramme bauen:

```powershell
dotnet build NovaTouchUpdater.slnx -c Release
```

Ein C#-Testprogramm ausführen, beispielsweise die Status- und Berichtstests:

```powershell
dotnet run --project tests\dotnet\HostState.Tests\HostState.Tests.csproj -c Release
```

`MainForm.Tests` prüft die Hostauswahl, den Erhalt beim Filtern und Sortieren, die Sperre während eines Durchlaufs sowie die Spaltenbreiten bei 100–200 % Skalierung. Zusätzlich werden die Zelltypen beim Neuaufbau und bei kurzzeitig leeren Checkboxen geprüft; `--formatting` führt nur diese Formatierungsprüfung aus. `LdapSearch.Tests` prüft auch die Checkbox-Formatierung der Domänensuche. Die tatsächlichen Prüf- und Überwachungsabläufe erhalten dabei simulierte Worker; weder die persönliche Config noch reale Hosts werden verwendet.

`MainForm.Tests --hosts` prüft den lokalen Ersatz bei leerer Remote-Liste und beim Abwählen aller Remote-Hosts, die Anzeige **LOKAL**, den Bearbeitungsdialog mit leerer Eingabe, Speichern/Laden einer leeren Config sowie den Wechsel zurück zu Remote-Hosts. Lokale Prüfungen dürfen keine Remote-Anmeldedaten, WinRM-Überwachung oder Remote-Befehle verwenden. Die Auswahlprüfungen schließen Filter, Sortierung, Leertaste und das spätere Wiederauswählen der Remote-Hosts ein. `--startup` deckt zusätzlich die automatische lokale Prüfung einer leeren gespeicherten Liste ab; `HostState.Tests` prüft den Namen **LOKAL** auch im HTML-Bericht.

`MainForm.Tests --columns` prüft die automatische Anpassung an längere und wieder kürzere Hostnamen und Versionen, Zeilen außerhalb des sichtbaren Ausschnitts sowie das Entfernen langer Werte. Auch geprüft: Nur „Information“ bekommt zusätzlichen Platz beim Vergrößern des Fensters; ihr Text und Fortschritt beeinflussen die anderen Spalten nicht. Bei überlangen Zellinhalten bleiben die Überschriften und die rechte Statusspalte sichtbar. `--layout` prüft zusätzlich das gesamte Fenster bei 100–200 % Skalierung und schmaler Fensterbreite.

`MainForm.Tests --parallel` prüft den Prüfablauf mit gezielt angehaltenen simulierten Verbindungen und Versionsabfragen. Ein bereits verbundener Host muss Versionen lesen und fertig werden können, während eine andere Verbindung noch wartet. Geprüft werden außerdem die gemeinsame Parallelitätsgrenze für beide Schritte, sofortige Ergebnisse, unabhängige Verbindungs- und Lesefehler, einmaliger Fortschritt je Host, die Hostauswahl sowie Abbruch ohne Start weiterer Schritte oder Hosts. Die Hintergrundüberwachung wird nach dem Durchlauf wieder freigegeben. Es werden keine echten Verbindungen aufgebaut oder Pakete übertragen.

`MainForm.Tests --startup` prüft die speicherbare Startprüfung in allen drei Ausführungsmodi: Config-Rundlauf durch das Formular, Ein-/Ausschalten, ausgewählte Hosts, freigegebene Update-Schaltfläche und manuelle Prüfung bei ausgeschalteter Automatik. Ohne geladene Config oder ohne Ziele starten keine Versionsabfragen. Alle Worker sind simuliert.

`MainForm.Tests --toggles` prüft das Neuzeichnen der Kippschalter bei 100–200 % Skalierung mit aktivierten Windows-Designs. Ein mit fremdem Text vorbelegter Zeichenpuffer muss vollständig ersetzt werden; auch Textwechsel, Schalterzustände, mehrzeilige Beschriftungen, deaktivierte Schalter und geänderte Hintergrundfarben dürfen keine alten Pixel hinterlassen. Vorschaubilder liegen unter `artifacts/test-results/toggle-*.png`.

`MainForm.Tests --winrm` prüft die Reihenfolge der WinRM-Diagnose: lokale Vorbereitung mit allen geladenen Remote-Hosts vor den Tests der ausgewählten Hosts, unveränderte Protokoll-/Port-/Anmeldedaten, Fehleranzeige und Abbruch ohne anschließende Remote-Tests. `WinRmPreparation.Tests.ps1` simuliert lokale Dienste, WSMan und TrustedHosts vollständig. Geprüft werden Ergänzung und Erhalt bestehender Einträge, Duplikate, fehlender oder gestoppter Dienst, defekter lokaler Endpunkt, fehlende Rechte, Richtlinien sowie ungültige Hostnamen. Keine dieser Prüfungen ändert reale TrustedHosts oder Dienste.

`MainForm.Tests --sections` prüft die aufklappbaren Bereiche für Einstellungen, Remote-Hosts und einzelne Update-Dateien bei 100 % und 200 % Skalierung und verschiedenen Fensterbreiten. Geprüft werden die Erweiterung des ursprünglichen Blocks ohne rechteckigen Rahmen, Pfeile, Überlappung ohne Verschieben der anderen Bereiche, Eingabenerhalt, Bereichswechsel, Escape, Klick außerhalb und das Schließen des Fensters mit geöffnetem Block. Weitere Prüfungen decken die Liste aller sieben Pakete, den Zielpfad oberhalb des Speichern-Buttons und das Ein-/Ausblenden des Ports ohne Wertverlust ab. Alle Steuerelemente bleiben verborgen; Hosts und Worker sind simuliert. Vorschaubilder entstehen unter `artifacts/test-results/main-sections-open.png`, `settings-popup.png`, `main-packages.png` und `individual-files.png`.

`MainForm.Tests --local` prüft außerdem den Vorrang der Einzelauswahl für ZIPs und MSIs: automatische Pakete abwählen, weitere Einzelkomponenten ergänzen, manuelle Auswahl beim erneuten Einlesen erhalten, vollständige Ordnerauswahl explizit wiederherstellen und ausschließlich ausgewählte Pakete im Update-Request verwenden.

```powershell
dotnet run --project tests\dotnet\MainForm.Tests\MainForm.Tests.csproj -c Release
```

Ein PowerShell-Testprogramm ausführen, beispielsweise die Tests aller Server-Suite-Instanzen:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\ServerSuiteUpdate.Tests.ps1
```

Die weiteren Befehle stehen bei den jeweiligen Funktionen in der Haupt-README. Alle Testdateien, Protokolle und Vorschaubilder entstehen unter `artifacts/test-results`. Build-Ausgaben liegen unter `artifacts/bin` und `artifacts/obj`; die Quellcodeordner bleiben frei von diesen erzeugten Dateien.

`LdapSearch.Tests` umfasst auch den automatischen Wechsel zur lokalen Netzwerksuche, dieselben Namensmuster, IPv4-Bereiche und deren Überlappungen, Größenbegrenzung, Fortschritt, Abbruch, NetBIOS-Pakete sowie die Auswahlübernahme im Dialog. Domänenzugehörigkeit und Netzwerkantworten werden dabei simuliert; kein Test startet eine echte Netzwerksuche. `DirectorySearch.Tests.ps1` prüft zusätzlich, dass fehlende Domänen erkannt werden und Berechtigungsfehler weiterhin als LDAP-Fehler sichtbar bleiben.

Die Suchtests prüfen außerdem einzelne JSON-Treffermeldungen vor dem Abschluss, sofortige Tabellenzeilen, nachgetragene IP-Adressen ohne Duplikate, weiterhin sichtbare Rechner bei fehlender DNS-Auflösung sowie das Verwerfen verspäteter Meldungen nach Abschluss oder Abbruch. Die Netzwerkprobe und die DNS-Auflösung verwenden künstliche Antworten. Der PowerShell-Test prüft die Treffercallbacks während der LDAP-Ergebnisverarbeitung einschließlich Begrenzung und Aufräumen.

`MainForm.Tests --local` prüft auch, dass lokale ZIP- und MSI-Requests denselben einstellbaren Zielpfad verwenden wie Remote-Requests. Quellordner, Zielpfad und Modus werden über dieselben Methoden wie beim Config-Speichern und Programmstart in eine künstliche Config geschrieben und wieder in ein Vorschaufenster geladen. Vorhandene persönliche Einstellungen werden dabei nicht gelesen oder geändert; die Test-Config enthält kein Passwort.

`MainForm.Tests --staging` prüft zusätzlich die unabhängigen Schalter für lokale Paketkopien und Remote-Uploads, Reihenfolge und Beschriftung, Config-Rundlauf mit aktivierten/deaktivierten Optionen, einen leeren lokalen Zielpfad sowie den Remote-Standardpfad. Direkte lokale Updates dürfen keinen `stage-packages`-Aufruf auslösen und müssen die ursprünglichen Paketpfade behalten. Lokale Updates dürfen keinen Datenbank-Upload und keine separate Verbindungsprobe anfordern. Die Hash-Vorbereitung wird mit gemeinsamen MSI-ZIPs und CAB-Dateien geprüft: jede benötigte Quelle einmal lesen, höchstens zwei parallele Lesevorgänge, vorbereitete Prüfsummen bei der Paketbereitstellung wiederverwenden, bei einem neuen Durchlauf neu berechnen, bei Wiederholungen nur ausstehende Komponenten berücksichtigen und nach Abbruch keine Teilresultate verwenden. `--sections` prüft die Lesbarkeit beider Schalter bei schmalen Fenstern und 100–200 % Skalierung. `ConfigStore.Tests` prüft beide gespeicherten Flags, alte Configs und explizit leere Zielpfade.

`MsiUpdates.Tests.ps1` und `ServerSuiteUpdate.Tests.ps1` führen die tatsächlichen Worker-Abläufe mit simulierten Diensten und Installern aus. Die neuen Fälle prüfen einzelne MSIs direkt am Quellort, externe CAB-Dateien, temporär entpackte MSI-/Dienst-ZIPs, leere Zielpfade und unveränderte Remote-Transfers. Es darf keine zusätzliche Paketkopie oder Anlage des ungenutzten Zielordners geben; geänderte Quellprüfsummen müssen vor der Installation scheitern. Temporäre Entpackordner dieser Tests liegen ausschließlich innerhalb ihrer geprüften Testordner und werden bereinigt.

`MainForm.Tests --updates` prüft mit simulierten Workern die getrennten Updateergebnisse für Payment, Fiscal und Server Suite: automatische und manuelle Wiederholung nur fehlgeschlagener Komponenten, Force-Modus, Uploadvorbereitung, Wiederaufnahme nach Abbruch oder Neustart sowie die gemeinsame Statusanzeige bei erfolgreichen, aktuellen und nicht installierten Komponenten. `ServerSuiteUpdate.Tests.ps1` prüft die vollständigen Worker-Schritte mit künstlichen ZIPs und simulierten Diensten, auch für Wiederholungen bei bereits erreichter Versionsnummer. Echte Hosts und Windows-Dienste werden dabei nicht verändert.

Die zusätzlichen Startoptionen der `AppRelease.Tests` prüfen einen veröffentlichten Test-Updater und dessen Neustart. Diese Integrationsprüfungen starten ausschließlich die separate Test-EXE unter `artifacts/test-results`; die Befehle sind in der Haupt-README beschrieben. Sie sind kein Update der eigentlichen Anwendung.

`MainForm.Tests --local` prüft den Paketkatalog mit echten, eigens erzeugten MSI-Datenbanken (nur lesend), ZIPs mit MSIs, externe CAB-Dateien, Versionsauswahl, Pfadprüfung sowie die drei Ausführungsmodi und direkte lokale Requests mit simulierten Workern. Die erzeugten MSI-Testdateien werden für den folgenden PowerShell-Test unter `artifacts/test-results` behalten:

```powershell
dotnet run --project tests\dotnet\MainForm.Tests\MainForm.Tests.csproj -c Release -- --local
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\MsiUpdates.Tests.ps1
```

`MsiUpdates.Tests.ps1` prüft fehlende, gleiche, neuere und ältere Produkte, MSI-Versionsvergleiche, lokale und Remote-Ausführung, MSI in ZIP, externe CABs, erneute Metadatenprüfung, Installerfehler, Neustartbedarf und temporäre Bereinigung. `msiexec` wird dabei niemals gestartet: Der Prozessaufruf ist simuliert. `ServerSuiteUpdate.Tests.ps1` prüft zusätzlich den vollständigen lokalen ZIP-Dienstablauf und bricht ab, falls dabei ein WinRM-Aufruf versucht wird. Echte Programme oder Dienste werden nicht verändert.

Der automatische `zzzSoftware`-Upload ist deaktiviert; die Datenbankroutine wird nicht mehr in die EXE eingebettet. `SoftwareDatabase.Tests.ps1` prüft die erhaltene Entwicklungsreferenz isoliert mit MSI-Testdateien und einem InterSystems-Provider als reiner Simulation im Speicher. Geprüft werden Verbindungen aus den Update-Dienstpfaden, XML/INI mit Duplikaten, SQLDSN, verschlüsselte Zugangsdaten, NTC ohne POS, POS ohne NTC, alle Anwendungs-Namespaces, Binärdaten aus MSI und ZIP, Metadaten, Quellenänderungen, Transaktionen mit Rollback, Fehler ohne Zugangsdaten im Protokoll und temporäre Bereinigung. Zusätzlich muss der tatsächliche Worker die alte Upload-Aktion lokal und remote ablehnen, bevor Dienste oder Datenbankziele abgefragt werden. Es werden keine Datenbankverbindungen geöffnet. `MainForm.Tests --staging` prüft lokale Updates ohne Datenbankvoraussetzung und die Wiederholung nach Kopierfehlern.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\SoftwareDatabase.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\SoftwareDatabaseDiscovery.Tests.ps1
```

`SoftwareDatabaseDiscovery.Tests.ps1` verwendet simulierte Update-Dienste und Konfigurationen in Testordnern. Geprüft werden NTC-XML, POS-INI ohne Einstellungen-Ordner, Unicode, verschlüsselte Zugangsdaten, Server/Port/Namespace/SSL, doppelte Verbindungen, gestoppte Dienste, Dienstnamen und Anzeigenamen, EXE-Pfade mit Leerzeichen/Parametern/Umgebungsvariablen sowie fehlende und ungültige Konfigurationen. Registrierungszugriffe auf Programme sowie Dienststarts und Prozessstarts führen im Test zum Fehler. Die tatsächlichen Windows-Dienste werden nicht abgefragt oder verändert.

`PowerShellValidation.Tests.ps1` prüft die Build-Prüfung mit gültigen Skripten, Syntaxfehlern und Git-Konfliktmarkern. Auch Konfliktmarker in syntaktisch gültigen Textblöcken müssen den Build verhindern. Die Testskripte werden ausschließlich geparst; ihre Inhalte werden nicht ausgeführt.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\PowerShellValidation.Tests.ps1
```
