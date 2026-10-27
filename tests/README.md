# Automatische Prüfungen

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

```powershell
dotnet run --project tests\dotnet\MainForm.Tests\MainForm.Tests.csproj -c Release
```

Ein PowerShell-Testprogramm ausführen, beispielsweise die Tests aller Server-Suite-Instanzen:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\ServerSuiteUpdate.Tests.ps1
```

Die weiteren Befehle stehen bei den jeweiligen Funktionen in der Haupt-README. Alle Testdateien, Protokolle und Vorschaubilder entstehen unter `artifacts/test-results`. Build-Ausgaben liegen unter `artifacts/bin` und `artifacts/obj`; die Quellcodeordner bleiben frei von diesen erzeugten Dateien.

`LdapSearch.Tests` umfasst auch den automatischen Wechsel zur lokalen Netzwerksuche, dieselben Namensmuster, IPv4-Bereiche und deren Überlappungen, Größenbegrenzung, Fortschritt, Abbruch, NetBIOS-Pakete sowie die Auswahlübernahme im Dialog. Domänenzugehörigkeit und Netzwerkantworten werden dabei simuliert; kein Test startet eine echte Netzwerksuche. `DirectorySearch.Tests.ps1` prüft zusätzlich, dass fehlende Domänen erkannt werden und Berechtigungsfehler weiterhin als LDAP-Fehler sichtbar bleiben.

`MainForm.Tests --updates` prüft mit simulierten Workern die getrennten Updateergebnisse für Payment, Fiscal und Server Suite: automatische und manuelle Wiederholung nur fehlgeschlagener Komponenten, Force-Modus, Uploadvorbereitung, Wiederaufnahme nach Abbruch oder Neustart sowie die gemeinsame Statusanzeige bei erfolgreichen, aktuellen und nicht installierten Komponenten. `ServerSuiteUpdate.Tests.ps1` prüft die vollständigen Worker-Schritte mit künstlichen ZIPs und simulierten Diensten, auch für Wiederholungen bei bereits erreichter Versionsnummer. Echte Hosts und Windows-Dienste werden dabei nicht verändert.

Die zusätzlichen Startoptionen der `AppRelease.Tests` prüfen einen veröffentlichten Test-Updater und dessen Neustart. Diese Integrationsprüfungen starten ausschließlich die separate Test-EXE unter `artifacts/test-results`; die Befehle sind in der Haupt-README beschrieben. Sie sind kein Update der eigentlichen Anwendung.

`MainForm.Tests --local` prüft den Paketkatalog mit echten, eigens erzeugten MSI-Datenbanken (nur lesend), ZIPs mit MSIs, externe CAB-Dateien, Versionsauswahl, Pfadprüfung sowie die drei Ausführungsmodi und direkte lokale Requests mit simulierten Workern. Die erzeugten MSI-Testdateien werden für den folgenden PowerShell-Test unter `artifacts/test-results` behalten:

```powershell
dotnet run --project tests\dotnet\MainForm.Tests\MainForm.Tests.csproj -c Release -- --local
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\MsiUpdates.Tests.ps1
```

`MsiUpdates.Tests.ps1` prüft fehlende, gleiche, neuere und ältere Produkte, MSI-Versionsvergleiche, lokale und Remote-Ausführung, MSI in ZIP, externe CABs, erneute Metadatenprüfung, Installerfehler, Neustartbedarf und temporäre Bereinigung. `msiexec` wird dabei niemals gestartet: Der Prozessaufruf ist simuliert. `ServerSuiteUpdate.Tests.ps1` prüft zusätzlich den vollständigen lokalen ZIP-Dienstablauf und bricht ab, falls dabei ein WinRM-Aufruf versucht wird. Echte Programme oder Dienste werden nicht verändert.
