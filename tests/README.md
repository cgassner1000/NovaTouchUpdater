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

Ein PowerShell-Testprogramm ausführen, beispielsweise die Tests aller Server-Suite-Instanzen:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\ServerSuiteUpdate.Tests.ps1
```

Die weiteren Befehle stehen bei den jeweiligen Funktionen in der Haupt-README. Alle Testdateien, Protokolle und Vorschaubilder entstehen unter `artifacts/test-results`. Build-Ausgaben liegen unter `artifacts/bin` und `artifacts/obj`; die Quellcodeordner bleiben frei von diesen erzeugten Dateien.

Die zusätzlichen Startoptionen der `AppRelease.Tests` prüfen einen veröffentlichten Test-Updater und dessen Neustart. Diese Integrationsprüfungen starten ausschließlich die separate Test-EXE unter `artifacts/test-results`; die Befehle sind in der Haupt-README beschrieben. Sie sind kein Update der eigentlichen Anwendung.
