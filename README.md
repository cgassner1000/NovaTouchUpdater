# NovaTouch Updater — WinForms / .NET 8

Die Tests prüfen unter anderem Versionsvergleiche, alle Server-Suite-Instanzen, den Erhalt bestehender Dateien, Fehlerfälle und die Darstellung bei verschiedenen Skalierungen. Sie gehören zur Entwicklung und werden nicht in die fertige Anwendung eingebaut. Die PowerShell-Worker unter `src/NovaTouchUpdater/Scripts` werden weiterhin mit denselben Ressourcennamen in die EXE eingebettet.

Eine vorhandene persönliche `config.json` im Hauptordner bleibt dort und wird für lokale Builds neben die EXE kopiert. Die Anwendung selbst lädt und speichert ihre Config weiterhin neben der gestarteten EXE. Die Beispielkonfiguration liegt unter `config/config.example.json`; erzeugte Dateien und persönliche Einstellungen sind in Git ignoriert.

## Voraussetzungen

- Visual Studio 2026 mit Workload **.NET-Desktopentwicklung** und .NET 8 SDK
- Adminrechte auf den Ziel-PCs, Windows PowerShell 5.1 auf dem Steuer-PC
- Für Remote-Updates: WinRM / PowerShell-Remoting ist auf den Ziel-PCs eingerichtet und erreichbar
- Die vorhandenen Windows-Dienste `nt.payment*`, `nt.fiscal*` und/oder `nt3_server_suite*` sind korrekt registriert
- Ausreichend Platz für ZIP-Übertragung und temporäres Entpacken auf den Ziel-PCs

Der GUI-Updater selbst nutzt **.NET 8** und startet Windows PowerShell 5.1 als Hintergrundprozess. Remote-Ziele werden über PowerShell-Remoting angesprochen. **Lokale Prüfungen und Updates greifen direkt auf den Rechner zu und benötigen kein WinRM.** Für lokale Änderungen die Anwendung als Administrator starten; reine Versionsprüfungen benötigen keine erhöhten Rechte. Die lokale Paketbereitstellung beim Start eines Updates benötigt Schreibzugriff auf den eingestellten Zielpfad. Prüfen + Versionen kopiert keine Dateien. Es wird kein zusätzlicher Agent installiert.

Mit **Config speichern** bei der Hostliste werden die aktuell geladenen Hosts, der Zielpfad für lokale und Remote-Update-Dateien, WinRM-Benutzer, Passwort, HTTPS-Option und HTTPS-Port sowie Ausführungsmodus und Softwareordner direkt als `config.json` neben der gestarteten EXE gespeichert. Auch durch den Tabellenfilter ausgeblendete Hosts gehören zur gespeicherten Liste; die Hostliste steht am Ende der Datei. Es gibt keine Dateiauswahl oder Rückfrage beim Überschreiben. Als Bestätigung erscheint nur **Config gespeichert**. Diese Datei wird beim nächsten Start automatisch geladen und ist in Git ignoriert.

Das Passwort wird beim Speichern mit **Windows DPAPI / LocalMachine** verschlüsselt und als `EncryptedPassword` abgelegt; `PasswordProtectionScope` kennzeichnet den Schutz. **Alle Windows-Benutzer mit Lesezugriff auf die Config können das Passwort auf demselben Rechner verwenden.** Die Config muss dafür in einem für diese Benutzer lesbaren Ordner liegen. Auf einem anderen Rechner muss das Passwort neu eingegeben und dort gespeichert werden. Das folgende Beispiel enthält noch kein Passwort: Passwort in der Anwendung eingeben und anschließend **Config speichern** verwenden.

```json
{
  "RemotePath": "C:\\install\\Novacom",
  "Username": "DOMAIN\\support",
  "PasswordProtectionScope": "LocalMachine",
  "EncryptedPassword": "",
  "UseSsl": false,
  "HttpsPort": 5986,
  "ExecutionMode": "Remote",
  "SoftwarePath": "",
  "Hosts": ["POS01", "POS02"]
}
```

Alte Configs mit dem Klartextfeld `Password` bleiben lesbar. Beim Speichern wird dieses Feld entfernt und durch das verschlüsselte Passwort ersetzt. Auch die bisherige benutzergebundene DPAPI-Config bleibt unter dem ursprünglichen Benutzer lesbar: **Einmal mit diesem Benutzer öffnen und erneut speichern**, damit sie anschließend von den anderen Benutzern desselben Rechners verwendet werden kann. Ist ein vorhandenes verschlüsseltes Passwort beschädigt oder nicht entschlüsselbar, werden die anderen Einstellungen trotzdem geladen und eine erneute Passworteingabe angefordert. Eine fehlgeschlagene Speicherung lässt eine vorhandene Datei unverändert. Fehlt `config.json`, startet die Anwendung mit leerer Hostliste und ohne vorausgefüllte Zugangsdaten.

Lokale Tests für Speicherung, DPAPI-Verschlüsselung, Migration und Schreibfehler (nur künstliche Zugangsdaten und Testdateien unter `artifacts/test-results`, keine vorhandene `config.json` wird verändert):

```powershell
dotnet run --project tests\dotnet\ConfigStore.Tests\ConfigStore.Tests.csproj -c Release
```

Windows 10 Enterprise LTSC 2019 (1809) und Windows Server 2019 unterstützen .NET 8. Der .NET-8-Support endet am 10. November 2026. Die Windows-Desktopanwendung kann self-contained veröffentlicht werden.

## Projekt öffnen

Visual Studio 2026: `NovaTouchUpdater.slnx` öffnen, NuGet/SDK laden, `F5` starten. Für Release:

```powershell
dotnet publish .\src\NovaTouchUpdater\NovaTouchUpdater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false -o ".\artifacts\publish\win-x64"
```

Ergebnis im Ordner `artifacts\publish\win-x64\`.

Build und lokale Tests sind unter Windows ausführbar. Die automatisierten Update-Tests verwenden Testordner und simulierte Dienste/WinRM-Aufrufe; sie ersetzen keinen Update-Test auf einem echten Testclient.

## Dateiaufbau

```text
NovaTouchUpdater/
├── NovaTouchUpdater.slnx       Visual-Studio-Projektmappe mit Anwendung und Tests
├── Build-win64.cmd            Eigenständige Windows-EXE erstellen
├── version.txt                Zentrale Versionseinstellung mit Anleitung
├── Directory.Build.props      Gemeinsame Build- und Ausgabeordner
├── config/                    Beispielkonfiguration ohne Zugangsdaten
├── src/NovaTouchUpdater/      Anwendung und Projektdatei
│   ├── Program.cs             Programmeinstieg
│   ├── UI/                    Hauptfenster, Dialoge, Steuerelemente, Layout und Design
│   ├── Configuration/         Config laden/speichern und Passwortverschlüsselung
│   ├── Models/                Hostzustand, Verbindungszustand und Paketnamen
│   ├── DirectoryServices/     Domänen-, LDAP- und lokale Netzwerksuche
│   ├── Remoting/              PowerShell-Prozessausführung und JSON-Protokoll
│   ├── Updates/               Paketkatalog, MSI-Metadaten und Selbstaktualisierung
│   ├── Reporting/             HTML-Bericht
│   ├── Common/                Gemeinsame Textaufbereitung
│   ├── Scripts/               Eingebettete PowerShell-Worker und Updateabläufe
│   └── Assets/                Programmsymbol und Bildressourcen
├── tests/                     Automatische Entwicklungsprüfungen; siehe tests/README.md
│   ├── dotnet/                C#-Tests für Zustände, Konfiguration und Oberfläche
│   ├── powershell/            Tests der Worker mit simulierten Diensten und Hosts
│   └── Shared/                Gemeinsame Testpfade
└── artifacts/                 Erzeugte Dateien; nicht in Git
    ├── bin/                   Kompilierte Anwendungen und Testprogramme
    ├── obj/                   Build-Zwischendateien
    ├── publish/win-x64/       Fertige NovaTouchUpdater.exe
    ├── test-results/          Testdateien, Protokolle und Vorschaubilder
    └── legacy/                Frühere Build-Ausgaben und Vorschaudateien
```

## Bedienung

### Lokal, Lokal & Remote und Remote

Unter **Verbindung & Ausführung → Ausführung** stehen genau drei Möglichkeiten zur Auswahl:

| Auswahl | Ziele | Verbindung |
| --- | --- | --- |
| Lokal | Automatisch angelegter Eintrag **LOCALHOST** | Direkter Zugriff ohne WinRM und ohne Remote-Zugangsdaten |
| Lokal & Remote | **LOCALHOST** und die ausgewählten **Remote-Hosts** | Lokal direkt; entfernte Rechner über WinRM |
| Remote | Nur die ausgewählten **Remote-Hosts** | WinRM mit den eingestellten Anmeldedaten, HTTPS und Port |

Vorhandene Configs ohne diese Einstellung starten weiterhin mit **Remote**. Der Modus und der Softwareordner werden mit **Config speichern** gesichert; JSON-Werte für `ExecutionMode` sind `Local`, `LocalAndRemote` und `Remote`. `LOCALHOST` wird automatisch verwaltet und nicht als Remote-Hostname gespeichert. Die Checkboxen in der Übersicht gelten auch für den lokalen Eintrag. **Test WinRM**, **Upload** und **Befehl senden** betreffen nur ausgewählte Remote-Hosts; die lokalen Update-Schritte verwalten ihre Dienste selbst.

Links neben **Remote-Hosts** steht die Box **Installations-Pakete**. Dort einen Softwareordner wählen oder einen Pfad eingeben und **Pakete einlesen** drücken (alternativ Enter). Der Ordner einschließlich Unterordnern wird nach den drei bekannten Dienst-ZIPs und MSI-Paketen durchsucht. Die Auswahl in **Update-Dateien** wird anhand der höchsten gefundenen Version je Komponente gefüllt. Nicht lesbare Pakete erscheinen im Protokoll. Ein gespeicherter Softwareordner wird beim Start erneut eingelesen. Die dort gefundenen Pakete stehen für lokale und Remote-Updates gleichermaßen zur Verfügung; weiterhin kann jedes Paket einzeln gewählt werden.

| Paket | Erkennung |
| --- | --- |
| Payment | `nt.payment_VERSION*.zip` |
| Fiscal | `nt.fiscal_VERSION*.zip` |
| Server Suite | `nt.server.suite-VERSION.zip` |
| POS | MSI-Produktname **NovaTouch POS** |
| NTC | MSI-Produktname **NovaTouch Control** |
| NTV | MSI-Produktname **NovaTouch Voucher** |
| OMAN | MSI-Produktname **NovaTouch Orderman SOL** |

MSIs dürfen einzeln oder in ZIP-Dateien vorliegen. Produkt und Version werden aus der MSI-Datenbank gelesen, unabhängig vom Dateinamen. ZIPs werden samt enthaltenen Begleitdateien bereitgestellt; bei einzelnen MSIs werden externe CAB-Dateien aus der MSI-Medientabelle neben der MSI gesucht und mitübertragen. Eine einzelne gewählte ZIP oder MSI reicht für einen Update-Durchlauf.

**Nur bereits installierte Komponenten werden aktualisiert.** Nicht installierte Dienste oder MSI-Produkte werden ohne Fehler übersprungen. Programme werden unter den 32- und 64-Bit-Uninstall-Einträgen in `HKLM` gesucht; benutzerspezifische Installationen sind nicht Teil dieser Erkennung. Eine gleiche oder neuere installierte MSI-Version wird immer übersprungen, auch bei Force oder einer Wiederholung. Für MSI-Vergleiche zählen gemäß [Windows Installer](https://learn.microsoft.com/windows/win32/msi/productversion) die ersten drei Versionsbestandteile; ein vierter Bestandteil wird ignoriert. Eine unbekannte installierte Version führt zu einem Fehler für die betroffene Komponente.

MSI-Updates laufen [still mit unterdrücktem automatischen Neustart](https://learn.microsoft.com/en-us/windows/desktop/msi/standard-installer-command-line-options). Gleichbleibende ProductCodes erhalten die Eigenschaften `REINSTALL=ALL REINSTALLMODE=vomus`. Statisch benannte Dienste aus der MSI-Tabelle `ServiceControl` werden gestoppt und anschließend gestartet; formatierte Dienstnamen behandelt der Installer. Deaktivierte Dienste bleiben deaktiviert. Nach der Installation wird die registrierte Version erneut geprüft. Einen erforderlichen Windows-Neustart zeigt der Updater in **Information** und im HTML-Bericht an; er startet Windows nicht selbst neu. MSI-Protokolle bleiben im Arbeitsordner erhalten, der temporäre Entpackordner wird bereinigt. Lokal und remote gilt der eingestellte **Zielpfad für Update-Dateien** (Standard `C:\install\Novacom`), jeweils auf dem betreffenden Rechner. **Prüfen + Versionen** liest nur Verbindung und installierte Versionen; dabei werden keine Update-Dateien kopiert. Erst nach Bestätigung von **Updates starten** werden auf **LOCALHOST** alle gewählten ZIPs, MSIs und externen CAB-Dateien im Zielpfad bereitgestellt, auch für bereits aktuelle oder nicht installierte Programme. Fehlende Zielordner werden dann angelegt. Gleiche Dateien werden per SHA256 erkannt und wiederverwendet; fehlende oder abweichende Dateien werden kopiert. Installiert bzw. aktualisiert werden weiterhin nur bereits installierte Komponenten. **Remote-Hosts erhalten keine vollständige Paketablage**: Ihr bisheriger Updateablauf kopiert nur die Pakete, die für installierte Komponenten tatsächlich benötigt werden. **Installationsdatei nicht übertragen**, **Force Up-Downgrade** und die Wiederholung nur fehlgeschlagener Komponenten behalten ihr bisheriges Verhalten. Lokale Wiederholungen stellen ebenfalls nur noch ausstehende Komponenten bereit. Der Quellordner unter **Installations-Pakete** bleibt davon unabhängig. **Config speichern** sichert beide Pfade: `SoftwarePath` für den Quellordner und `RemotePath` für die lokale/Remote-Paketablage. Bestehende Configs bleiben kompatibel; lokale Updates benötigen kein WinRM.

Die Hosttabelle enthält nach **NovaTouch POS** die neue Spalte **NovaTouch NTC**. Alle vier erkannten MSI-Programme erscheinen zusätzlich im Tooltip der NTC-Zelle und im HTML-Bericht. Der Bericht enthält alle Hosts des gewählten Ausführungsmodus, auch abgewählte Hosts, sowie die geladenen Zielversionen. Bei einem Fehler werden ausschließlich fehlgeschlagene Komponenten wiederholt; bereits bestätigte ZIP- oder MSI-Updates bleiben abgeschlossen.

### Oberfläche und bestehende Remote-Funktionen

Die Windows-Oberfläche verwendet ein einheitliches helles Design mit dezentem Hintergrund, eigenen Boxen für **Installations-Pakete**, **Remote-Hosts** und **Update-Dateien**, einem Bereich **Verbindung & Ausführung**, blauen Hauptaktionen und farbigen Statusfeldern in der Hostübersicht. Das gelieferte Icon wird in der EXE, den Fenstern und im Kopfbereich verwendet. HTTPS-Checkbox und Port sind gemeinsam mittig ausgerichtet; **Force Up-Downgrade** steht unter **Installationsdatei nicht übertragen**. Die Hosttabelle zeigt dezente senkrechte Trennlinien in Blaugrau zwischen den Überschriften, dezente graue Zeilentrenner und umbrochene Zelltexte. Alle Spalten bis **Status** passen sich der verfügbaren Breite ohne horizontales Scrollen an; die grünen Fortschrittsanzeigen pro Host bleiben erhalten. Auch die Dialoge für Befehle, Domänensuche, Upload und WinRM verwenden diesen Stil. Bei schmalen Fenstern werden die Einstellungsbereiche untereinander angeordnet; größere Windows-Skalierungen bleiben scrollbar. Die bestehenden Update- und Befehlsabläufe bleiben erhalten.

Rechts neben dem Untertitel erscheint **Neue Version verfügbar**, wenn ein neueres stabiles [GitHub-Release](https://github.com/cgassner1000/NovaTouchUpdater/releases/latest) mit passender EXE und SHA256-Prüfsumme vorliegt. Der Hinweis steht ganz rechts; bei wenig Platz erhält er eine eigene rechtsbündige Zeile. Die Prüfung läuft beim Start und anschließend alle 30 Minuten im Hintergrund. Ohne Internet oder bei einer fehlgeschlagenen GitHub-Abfrage erscheint kein störender Startdialog.

Ein Klick fragt mit **Ja/Nein**, ob die neue Updater-Version verwendet werden soll. Nach **Ja** wird die eigenständige EXE mit Fortschrittsanzeige heruntergeladen und anhand von Größe und SHA256 geprüft. **Abbrechen** beendet die Vorbereitung, ohne die bisherige EXE zu verändern. Erst nach einem erfolgreichen Download und einem bereiten lokalen Updatehelfer schließt sich das Fenster. Der Helfer wartet auf das Programmende und öffnet die neue Version im selben Ordner immer als **NovaTouchUpdater.exe**, ohne Versionsnummer im Dateinamen. Eine zuvor gestartete EXE mit Versionsnummer wird entfernt. Die alte EXE und eine gegebenenfalls bereits vorhandene `NovaTouchUpdater.exe` werden bis zum erfolgreichen Startaufruf gesichert; schlägt der Austausch oder Startaufruf fehl, werden sie wiederhergestellt. `config.json` und andere Dateien im Programmordner bleiben unverändert. Nicht gespeicherte Einstellungen gehen beim Neustart verloren, worauf die Bestätigung hinweist.

Das automatische Aktualisieren setzt die **eigenständige Release-EXE** und Schreibzugriff auf ihren Ordner voraus. Ein Visual-Studio-/DLL-Build wird nicht durch eine einzelne EXE ersetzt. Während eines Remote-Durchlaufs ist der Hinweis deaktiviert; während der Vorbereitung des Updater-Neustarts können keine neuen Remote-Aktionen gestartet werden. Fehler nach dem Schließen werden vom Helfer mit Meldung und Protokollpfad angezeigt.

Beim Wechsel von einer älteren Updater-Version kann deren eingebetteter Updatehelfer die neue EXE noch unter dem bisherigen Versionsnamen starten. Deshalb korrigiert die neue eigenständige EXE ihren Dateinamen zusätzlich beim Start: Sie bereitet eine identische Kopie vor, beendet sich und öffnet sich im selben Ordner als `NovaTouchUpdater.exe`, bevor das Hauptfenster geöffnet oder eine Hostverbindung aufgebaut wird. Das funktioniert auch beim direkten Start einer versionierten Release-EXE. Bereits korrekt benannte EXEs benötigen keinen weiteren Neustart. Bei einer Wiederherstellung nach einem Fehler wird die Umbenennung einmalig übersprungen, damit keine Neustartschleife entsteht.

Lokale Tests verwenden simulierte HTTP-Antworten, Testdateien und simulierte Programmstarts; sie aktualisieren weder den laufenden Updater noch Remote-Clients:

```powershell
dotnet run --project tests\dotnet\AppRelease.Tests\AppRelease.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\SelfUpdate.Tests.ps1
```

Der vollständige Umbenennungstest startet ausschließlich eine separat veröffentlichte **Test-EXE** in neuen Testordnern unter `artifacts/test-results`. Er prüft den tatsächlichen Neustart von einem Versionsnamen auf `NovaTouchUpdater.exe`, eine bereits vorhandene Zieldatei, unveränderte Config-Dateien und das Ausbleiben wiederholter Neustarts. Die Anwendung selbst und Remote-Clients werden dabei nicht gestartet:

```powershell
dotnet publish tests\dotnet\AppRelease.Tests\AppRelease.Tests.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=false -p:OutputType=WinExe -o "artifacts\test-results\app rename test"
dotnet run --project tests\dotnet\AppRelease.Tests\AppRelease.Tests.csproj -c Release -- --rename-integration "artifacts\test-results\app rename test\AppRelease.Tests.exe"
```

Die Oberfläche berücksichtigt die Windows-Anzeigeskalierung pro Monitor (PerMonitorV2). Buttonleisten und Optionen brechen bei wenig Platz um; Zeilenhöhen richten sich nach dem Inhalt. Bei großen Schriften oder schmalen Fenstern stehen Dateiauswahl und Einstellungen untereinander. Reicht die Bildschirmfläche nicht aus, lassen sich die Inhalte im Haupt- und WinRM-Fenster scrollen.

Zur Prüfung der Darstellung die Anwendung bei 100 %, 125 %, 150 % und 200 % Windows-Skalierung öffnen, das Fenster verkleinern und wieder vergrößern sowie bei mehreren Monitoren zwischen den Bildschirmen verschieben.

**Host hinzufügen → Durchsuchen** öffnet den Dialog **Computer suchen – Domäne oder lokales Netzwerk**. Unter **Suchen in** stehen drei Bereiche zur Auswahl:

- **Automatisch (Domäne, sonst Netzwerk):** verwendet die Domäne des Rechners. Ohne Domänenmitgliedschaft oder wenn bei der automatischen Domänenermittlung keine Domäne bzw. kein Domaincontroller erreichbar ist, wird das lokale IPv4-Netz durchsucht. Ausdrücklich eingetragene Domänen/Suchbasen und LDAP-Berechtigungsfehler bleiben Domänenabfragen; eine leere Trefferliste löst keinen Netzwerkwechsel aus.
- **Domäne (LDAP):** verwendet ausschließlich die Domänensuche.
- **Lokales Netzwerk:** durchsucht die aktiven IPv4-Netze oder den eingetragenen **IP-Bereich**, unabhängig von einer Domäne.

Für die Filterung gibt es zwei Modi:

- **Namensmuster:** ein frei wählbares Muster, beispielsweise `PC*-RST-POS-*`, `SERVER-*` oder `*-KASSE-*`. `*` steht für beliebig viele Zeichen; ein leeres Feld oder `*` sucht alle gefundenen Computer/Geräte. Es ist kein Namensschema fest vorgegeben. In der Domäne wird das Muster direkt als LDAP-Filter abgefragt; im Netzwerk wird dasselbe Muster ohne Beachtung der Groß-/Kleinschreibung auf die ermittelten Computernamen angewendet.
- **Erweiterte Suche (LDAP):** ein eigener LDAP-Filter, beispielsweise `(|(name=PC*-RST-POS-*)(name=SERVER-*))` oder `(&(name=SERVER-*)(operatingSystem=*Windows*))`. Zusätzlich wird immer `objectCategory=computer` eingeschränkt. AND/OR/NOT und LDAP-Matching-Regeln bleiben nutzbar; Sonderzeichen und Platzhalter folgen der [LDAP-Filtersyntax](https://learn.microsoft.com/en-us/windows/win32/adsi/search-filter-syntax).

**Domäne / Domaincontroller** und **Suchbasis (DN)** sind für die Domänensuche optional. Ohne Angaben wird die aktuelle Domäne durchsucht; mit einer Suchbasis wie `OU=Kassen,DC=firma,DC=local` lässt sich die Abfrage auf einen Teilbaum begrenzen. Die Domänensuche verwendet die **aktuelle Windows-Anmeldung** und benötigt Zugriff auf die betreffende Domäne; die separat eingetragenen WinRM-Zugangsdaten werden dafür nicht verwendet. **Erweiterte Suche (LDAP)** benötigt eine Domäne und wird nicht als Netzwerksuche interpretiert. Erst **Suchen** startet eine Abfrage. Laufende Suchen können abgebrochen werden; beim Schließen werden sie beendet. Es werden keine Computer oder Dienste verändert.

Die lokale Suche prüft die IPv4-Adressen der aktiven Netzwerkadapter mit begrenzter Parallelität und kurzen Zeitlimits. Computernamen werden über DNS und eine lesende [NetBIOS-Namensabfrage (NBSTAT)](https://www.rfc-editor.org/rfc/rfc1002.html#section-4.2.17) ermittelt; Erreichbarkeit wird über Ping oder eine NetBIOS-Antwort festgestellt. Firewalls, ausgeschaltete Geräte oder fehlende Namensantworten können Treffer verhindern. Ein erreichbares Gerät ohne Namen erscheint bei leerem Muster oder `*` als IP-Adresse; bei einem konkreten Namensmuster kann es nicht zugeordnet werden. Die IP-Adresse steht in einer eigenen Spalte; Betriebssystem und AD-Beschreibung werden im Netzwerk nicht ermittelt. Unter **IP-Bereich** kann beispielsweise `192.168.1.0/24` eingetragen werden, mehrere Bereiche werden mit Komma getrennt. Ohne Angabe gelten die aktiven IPv4-Netze. Überlappende Bereiche werden nur einmal geprüft. Maximal 4096 Adressen sind pro Suchlauf möglich; größere Bereiche müssen ausdrücklich eingegrenzt werden und werden nicht still abgeschnitten. Die Statuszeile zeigt geprüfte Adressen und Treffer.

Die Trefferliste zeigt Computername, IP-Adresse, Betriebssystem und Beschreibung. Treffer erscheinen bereits während der Suche; Domänen-IP-Adressen werden parallel ermittelt und in derselben Zeile ergänzt. Mehrere bekannte IP-Adressen werden gemeinsam angezeigt. Ohne ermittelbare Adresse steht dort ein Strich; der Computer kann trotzdem übernommen werden. Nach Abschluss der Suche gewünschte Computer durch Klick auf die jeweilige Zeile oder das Kontrollkästchen in **Auswahl** an- und abwählen; mit der Leertaste wird der aktuelle Computer ebenfalls umgeschaltet. Mehrere Computer lassen sich unabhängig anhaken, alternativ **Alle auswählen** verwenden. Anschließend **Auswahl übernehmen** klicken. Verfügbare DNS-Computernamen werden vollständig übernommen, andernfalls der Computername. Doppelte Einträge werden beim Bestätigen der Hostliste mit **OK** entfernt. Geänderte Suchangaben erfordern eine neue Suche, bevor alte Treffer übernommen werden können. Manuelle Eingabe und TXT-Import bleiben möglich.

Die LDAP-Suche verwendet Windows PowerShell 5.1 und die vorhandene .NET-Verzeichnisanbindung; das ActiveDirectory-PowerShell-Modul wird nicht benötigt. Die Verzeichnisverbindung übernimmt die aktuelle Windows-Anmeldung, ohne leere Zugangsdaten zu übergeben. Fehler nennen den betroffenen Suchschritt und LDAP-Pfad sowie gegebenenfalls den ursprünglichen LDAP-Fehlercode; Fehler beim Aufräumen verdecken diese Ursache nicht. Die Ergebnisse werden seitenweise abgefragt, damit die übliche AD-Begrenzung auf 1000 Einträge nicht unbemerkt greift. Maximal 10.000 eindeutige Computer werden angezeigt; weitere Treffer werden ausdrücklich gemeldet und können durch ein engeres Muster oder eine Suchbasis eingegrenzt werden. Client, Server und Gesamtprozess haben Zeitlimits.

Lokale Tests prüfen Filterbildung, frei wählbare Muster, eigene LDAP-Ausdrücke, Auswahl, Abbrechen, Darstellung und die echte JSON-Verbindung zum eingebetteten Suchprozess mit einer vor jeder Verzeichnisverbindung abgewiesenen Testeingabe. Zusätzlich werden automatische Bereichswahl, Netzwerk-Adressbereiche, NetBIOS-Antworten, Fortschritt, Überlappungen und der Abbruch mit simulierten Netzwerkteilnehmern geprüft. Die PowerShell-Tests prüfen echte Verzeichnisobjekte ohne Netzwerkverbindung auf unveränderte Windows-Anmeldung und korrektes Aufräumen; Suchergebnisse und LDAP-/Aufräumfehler sowie fehlende Domänen werden simuliert. Die Tests fragen keine echte Domäne ab, durchsuchen kein echtes Netzwerk und verändern keine vorhandene Config:

```powershell
dotnet run --project tests\dotnet\LdapSearch.Tests\LdapSearch.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\DirectorySearch.Tests.ps1
```

Neu geladene oder hinzugefügte Hosts sind in der ersten Tabellenspalte standardmäßig angehakt. Die Kontrollkästchen erlauben einen oder mehrere Zielrechner; **Alle auswählen** und **Auswahl aufheben** gelten für die gesamte geladene Liste. Die Anzeige nennt die ausgewählte und die gesamte Hostanzahl. Der Statusfilter ändert nur die Anzeige: Die Auswahl ausgeblendeter Hosts bleibt erhalten. **Prüfen + Versionen**, **Updates starten**, **Fehler erneut versuchen**, **Upload**, **Test WinRM** und alle Aktionen unter **Befehl senden** verwenden ausschließlich die ausgewählten Hosts. Ohne Auswahl sind diese Aktionen deaktiviert. Während eines Durchlaufs ist die Auswahl gesperrt. Beim erneuten Programmstart sind wieder alle geladenen Hosts ausgewählt; Die Config enthält weiterhin alle Remote-Hosts; der HTML-Bericht umfasst alle Hosts des gewählten Ausführungsmodus.

Das Hauptfenster startet mit 1710 Pixeln Breite. Die Spaltenüberschriften stehen beim normalen Start einzeilig; bei schmalen Fenstern dürfen sie umbrechen, während **Status** ohne horizontales Scrollen sichtbar bleibt.
1. Hostnamen-TXT laden: ein Hostname/IP pro Zeile; Leerzeilen und Zeilen mit `#`/`;` werden ignoriert, Duplikate entfernt.
2. `nt.payment_VERSION*.zip`, `nt.fiscal_VERSION*.zip` und/oder `nt.server.suite-VERSION.zip` wählen (die Auswahlfelder stehen direkt untereinander). Beispiele: `nt.payment_2026.2.9546.5587.zip`, `nt.server.suite-2026.4.9792.zip`. Die Server-Suite-Auswahl steht unter Fiscal; ihre ZIP muss die gefüllten Ordner `apps\nt3-server-suite` und `installer\python\portable_python` enthalten.
   **Eine einzige ZIP genügt:** Nur Payment, nur Fiscal oder nur Server Suite können unabhängig aktualisiert werden. Dienste und Installationsdateien der nicht ausgewählten Komponenten bleiben auch bei Force unverändert. Bei einer ausgewählten Server-Suite-ZIP werden alle zugehörigen Instanzen des Hosts berücksichtigt.
3. Den **Zielpfad für Update-Dateien** bei Bedarf anpassen (Standard: `C:\install\Novacom`). Dort liegen die Update-ZIPs und temporären Update-Dateien. Wenn andere Windows-Anmeldedaten benötigt werden, Benutzername und Passwort eingeben. Sonst wird das aktuelle Windows-Konto genutzt. Option `HTTPS` aktiviert WinRM/TLS. Im Feld **HTTPS-Port** kann der Port zwischen 1 und 65535 eingestellt werden, beispielsweise **443** statt des Standards **5986**. Die Einstellung gilt für Prüfen, Upload, Updates, Befehle, Test WinRM und Verbindungsüberwachung. Ohne HTTPS wird weiterhin **HTTP/5985** verwendet. Optional setzt `"HttpsPort": 443` zusammen mit `"UseSsl": true` in `config.json` diese Werte beim Start; ohne Portangabe bleibt 5986 vorbelegt. Die Einstellung wählt den vorhandenen WinRM-Endpunkt aus und ändert keine Listener oder Firewallregeln auf dem Client.
4. `Installationsdateien nicht übertragen` überspringt den ZIP-Transfer. Die lokal ausgewählten ZIPs werden weiterhin für Zielversion und SHA256 verwendet. Die passenden ZIP-Dateien müssen im eingestellten Zielpfad auf jedem Client unter demselben Dateinamen liegen; der Updater prüft sie vor dem Entpacken auf identische SHA256-Prüfsumme. Installierte Dienstversionen werden weiterhin mit den Zielversionen aus den lokalen ZIP-Dateinamen verglichen.
5. `1. Prüfen + Versionen`: ausgewählte Hosts zuerst prüfen (LOCALHOST direkt, Remote-Hosts per WinRM) (Standard: bis zu 40 parallel), dann .NET Framework 4.8, die installierte NovaTouch-POS-Version sowie vorhandene Dienste lesen. Payment/Fiscal verwenden ProductVersion/FileVersion der Dienst-EXE. Die Server-Suite-Version stammt je Instanz ausschließlich aus `apps\nt3-server-suite\version.txt`; die Version der Python-EXE wird dafür nicht verwendet. Diese Quelle gilt auch für Tabelle, HTML-Bericht, Versionsvergleich und Verifikation nach einem Update.
   Fehlende Payment-, Fiscal- oder Server-Suite-Dienste sind beim Prüfen kein Fehler, auch wenn alle fehlen. In den jeweiligen IST-Spalten und im HTML-Bericht steht **Nicht installiert**; .NET und POS werden trotzdem geprüft. Jeder Dienst mit dem Präfix `nt3_server_suite` wird einzeln erfasst, einschließlich des Namens ohne weiteren Suffix. Unterschiedliche Server-Suite-Versionen bleiben sichtbar; der Tooltip nennt alle Instanzen mit Dienstnamen und Pfaden. Bei registrierter Suite-Instanz ohne `version.txt` steht **Version unbekannt**, damit ein Update fehlende Dateien und Zielordner ergänzen kann. Verbindungsfehler und andere Lesefehler bleiben Fehler. Nach einem fehlgeschlagenen Scan steht bei den Dienstversionen **–** statt einer vermeintlich fehlenden Installation.
   Der RemoteWorker schließt jede PowerShell-Remotesitzung am Ende der Aktion automatisch (auch bei Fehlern); ein zusätzlicher Schließen-Button ist daher nicht erforderlich.
6. Optional `Force Update` aktivieren: Normalmodus überspringt Komponenten mit IST-Version = Zielversion; Force installiert die **ausgewählten** Komponenten trotzdem erneut. Nicht ausgewählte Komponenten bleiben unangetastet. Sobald eine Server-Suite-Instanz abweicht, werden alle Server-Suite-Instanzen des Hosts gemeinsam aktualisiert. Eine dreiteilige Suite-Zielversion wie `2026.4.9792` akzeptiert auch einen passenden vierteiligen Eintrag in `version.txt` wie `2026.4.9792.0`. Die Zielversion wird auch bei Force nach Installation verifiziert.
7. `2. Updates starten`: Updates auf den erfolgreichen Scan-Hosts (Standard: 8 gleichzeitig) mit **maximal drei Versuchsrunden pro Host**. Payment, Fiscal und Server Suite werden einzeln installiert und verifiziert. Scheitert eine Komponente, werden die anderen Komponenten trotzdem bearbeitet; in den weiteren Runden werden ausschließlich die noch nicht erfolgreich abgeschlossenen Komponenten wiederholt. Das gilt auch bei **Force Update**. Die beiden Optionen `Prüfen parallel` und `Update parallel` begrenzen unabhängig voneinander die Zahl gleichzeitig bearbeiteter Rechner (Vorgaben: 40 bzw. 8). Der Bestätigungsdialog nennt die geplanten Komponenten und deren Hostanzahl.
8. `Fehler erneut versuchen` berücksichtigt ausgewählte Hosts mit Prüf-/Updatefehlern und prüft bei Bedarf erst ihre Erreichbarkeit. Bei teilweise erfolgreichen Updates werden **nur die fehlgeschlagenen Komponenten** erneut übertragen, entpackt, installiert und verifiziert. Beispiel: Payment und Server Suite erfolgreich, Fiscal fehlgeschlagen → nur Fiscal wiederholen. Auch die Uploadvorbereitung und lokale ZIP-Prüfsummenberechnung berücksichtigen nur die noch benötigten Komponenten. Bereits verifizierte Ergebnisse bleiben für die geladenen Hosts in der laufenden Anwendung erhalten, auch nach einer erneuten Prüfung oder einem Dienst-/Terminalneustart. Ein fehlgeschlagener Befehl allein löst keinen Updateversuch aus. Der erneute Durchlauf hat wieder bis zu drei Versuchsrunden pro Host. Ein ausdrücklich neu gestarteter Durchlauf über `Updates starten` verwendet dagegen die aktuell ausgewählten ZIPs als neuen Auftrag.
9. Doppelklick auf eine Zeile öffnet das Hostprotokoll; `HTML-Bericht erstellen` öffnet sofort einen eigenständigen, offline nutzbaren Bericht im Standardbrowser – ohne vorherigen Speicherdialog. Über „HTML speichern“ im Bericht oder Strg+S im Browser kann er dauerhaft gespeichert werden; PDF-Export per Drucken ist ebenfalls möglich. SHA256-Werte werden in GUI und Bericht ausgeblendet, die Integritätsprüfung bleibt aktiv.
10. `Upload` öffnet eine Dateiauswahl und überträgt die ausgewählten Dateien an **die ausgewählten Hosts** in den eingestellten Zielpfad. Vorhandene Dateien werden nur bei abweichender SHA256-Prüfsumme ersetzt. Diese Aktion installiert nichts und funktioniert unabhängig von `Installationsdateien nicht übertragen`.
11. `Befehl senden` öffnet einen breiten, vergrößerbaren Dialog mit erklärten Aktionsbereichen. **nt.payment – Zahlungsdienst neu starten** und **nt.fiscal – Fiskaldienst neu starten** stoppen und starten die jeweiligen Dienste. **nt3 Server Suite – starten / neu starten** startet die `nt3_server_suite`-Dienste; laufende Dienste werden vorher gestoppt. Mehrere Dienstaktionen können gemeinsam ausgewählt werden. **POS neustart – KassaPreh neu starten** und **Terminal NEUSTART – Windows neu starten** sind als eigene Bereiche hervorgehoben. Jede dieser beiden Aktionen wird einzeln ausgeführt und kann weder mit der anderen noch mit Dienstaktionen kombiniert werden. Die aktuelle Auswahl steht oberhalb der Schaltflächen; ohne Auswahl ist das Ausführen deaktiviert.

Vor dem Upload wird der Zielordner auf jedem betroffenen Client geprüft. Bei nicht leeren Ordnern zeigt eine gemeinsame Abfrage Host, aufgelösten Zielpfad und Anzahl vorhandener Einträge. **Ja, bereinigen und hochladen** entfernt den gesamten Inhalt der bereinigbaren Uploadordner einschließlich versteckter Dateien und Unterordner; der Zielordner selbst bleibt bestehen. **Ohne Bereinigung hochladen** ist die Voreinstellung und behält vorhandene Inhalte bei. **Abbrechen** beendet den Vorgang vor der Bereinigung und Übertragung. Systemverzeichnisse sowie erkannte Payment-/Fiscal-/Server-Suite-Installationsordner und deren überlappende Pfade sind für die Bereinigung gesperrt und werden im Dialog markiert. Verknüpfungen/Junctions werden abgewiesen. Hat sich der Ordnerinhalt seit der Abfrage geändert, wird die Bereinigung für den Host abgebrochen. Die Prüfung gilt auch für Update-Uploads; bereits aktuelle Hosts und Updates mit deaktivierter ZIP-Übertragung bereinigen nichts. Prüfung und Bereinigung werden im Hostprotokoll und damit im HTML-Bericht dokumentiert.

**POS neustart** gilt wie der Terminalneustart für **die ausgewählten Hosts**, unabhängig von vorherigem Scan oder ZIP-Auswahl. Läuft `C:\Program Files (x86)\novatouch\bin\KassaPreh.exe` bereits, wird sie nach einer zusätzlichen Bestätigung gezielt per `taskkill /PID … /F` beendet und unter dem ursprünglichen Benutzer in derselben Windows-Sitzung gestartet. **Läuft sie nicht, wird die EXE aus diesem Standardverzeichnis unter dem eindeutig angemeldeten Benutzer gestartet.** Die Benutzersitzung wird dabei über die Windows-Sitzungsverwaltung ermittelt, unabhängig von Explorer oder einem laufenden Kassenprozess. Auch eine getrennte, weiterhin angemeldete Sitzung wird berücksichtigt. Bei mehreren angemeldeten Sitzungen erfolgt kein Start auf Verdacht. Die eingestellten WinRM-Anmeldedaten, HTTPS und `Update parallel` gelten auch hier. Windows und die Dienste werden nicht neu gestartet. Bei einem vorhandenen Prozess werden Benutzer und ursprüngliche Startparameter vor dem Beenden geprüft; ein erstmaliger Start erfolgt ohne zusätzliche Parameter. Arbeitsverzeichnis ist der `bin`-Ordner. Die temporäre Windows-Aufgabe wird vor dem Beenden vorbereitet und über ein [interaktives Benutzertoken in der konkreten Sitzung](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex) gestartet; ein Benutzerkennwort wird dafür nicht gespeichert.

Fehlt die Datei oder ist beim erstmaligen Start keine eindeutige angemeldete Benutzersitzung vorhanden, erscheint **Übersprungen** mit dem Grund. Ein vorhandener Kassenprozess ohne interaktive Benutzersitzung wird ebenfalls übersprungen. Bei mehreren passenden Prozessen oder nicht eindeutig lesbarem Benutzer/Pfad wird vor dem Beenden abgebrochen und ein Fehler angezeigt. Eine vor dem erstmaligen Start gewechselte Benutzeranmeldung wird erneut geprüft. Ein bereits durch einen Watchdog oder während der Vorbereitung gestarteter Prozess wird geprüft, ohne einen zweiten Start anzufordern. **POS OK** bestätigt den Prozess unter dem richtigen Benutzer in der vorgesehenen Sitzung und die Löschung der temporären Aufgabe; die Bedienbarkeit der Kasse wird dadurch nicht geprüft. Die Aufgabe wird auch nach Fehlern entfernt; schlägt die Bereinigung nach drei Versuchen fehl, wird der konkrete Aufgabenname als Fehler protokolliert. Die [Löschung der Aufgabe beendet die laufende Anwendung nicht](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/schtasks-delete). Ergebnisse und Details erscheinen auch im HTML-Bericht. `Abbrechen` stoppt weitere Hosts; aktive POS-Neustarts schließen die Startprüfung und Bereinigung ab.

Die Windows-Aktion gilt für **die ausgewählten Hosts**, unabhängig von vorherigem Scan oder ausgewählten ZIP-Dateien, und kann nicht mit Dienstaktionen kombiniert werden. Nach einer zusätzlichen Bestätigung wird pro Host über WinRM ein Windows-Neustart mit 30 Sekunden Vorlauf angefordert; die eingestellten Anmeldedaten, HTTPS und `Update parallel` gelten auch hier. Windows schließt dabei geöffnete Anwendungen; nicht gespeicherte Daten können verloren gehen. Die Umsetzung nutzt [`shutdown /r /t 30`](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/shutdown).

Nach bestätigtem Neustartauftrag steht beim Host **Windows-Neustart angefordert (30 Sekunden)** und im Ergebnis **Neustart angefordert**. Sobald die automatische Verbindungsprüfung nach dem Vorlauf eine erfolgreiche WinRM-Anmeldung meldet, lautet das Ergebnis **Wieder online**. Während des Neustarts bedeutet eine unterbrochene Verbindung keinen fehlgeschlagenen Auftrag. Die Anzeige bestätigt die Erreichbarkeit, ohne Windows-Startzeit oder Softwareversionen zu prüfen; vor weiteren Updates erneut `Prüfen + Versionen` ausführen. Ein Dienstbefehl endet mit **Dienste OK** oder **Fehler**. Jeder neue Befehl ersetzt das zuvor angezeigte Ergebnis; frühere Fehler bleiben im Protokoll. Befehle werden in der Zusammenfassung und im HTML-Bericht separat von Softwareupdates gezählt. `Abbrechen` stoppt die Vergabe weiterer Aufträge und wartet auf die Rückmeldungen bereits gestarteter Neustartanforderungen. Bereits an Windows übermittelte Neustartaufträge werden dadurch nicht zurückgenommen.

Test der Befehlsaktionen mit simulierten WinRM-, Dienst- und Shutdown-Aufrufen; dabei werden keine Rechner neu gestartet:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\RemoteCommand.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\PosRestart.Tests.ps1
dotnet run --project tests\dotnet\CommandDialog.Tests\CommandDialog.Tests.csproj -c Release
```

Die Hostübersicht zeigt die **installierten Versionen** von Payment, Fiscal und Server Suite. Die für alle Hosts identischen Zielversionen stehen als **Payment Target**, **Fiscal Target** und **Server Suite Target** bei der ZIP-Auswahl und werden automatisch aus dem ZIP-Dateinamen gelesen.

Die Spalte **Verbindung** prüft ab dem Öffnen des Hauptfensters die ausgewählten Remote-Hosts im Hintergrund ungefähr alle **10 Sekunden** mit einer WinRM-Anmeldung. Sie verwendet die eingestellten Anmeldedaten, die HTTPS-Auswahl und den HTTPS-Port. **Verbunden** erscheint grün, **Nicht erreichbar** rot und **Neustart angefordert** während des Vorlaufs braun. **Unbekannt** bedeutet, dass die Hintergrundprüfung selbst kein verlässliches Ergebnis liefern konnte. Der Tooltip zeigt die Uhrzeit der letzten Prüfung und Details; der Filter „Nicht erreichbar“ passt sich automatisch an. Statuswechsel werden im Hostprotokoll erfasst, gleichbleibende Ergebnisse erzeugen keine wiederholten Logzeilen. Der HTML-Bericht übernimmt den Zustand und die letzte Prüfzeit zum Exportzeitpunkt.

Während eines manuellen `Prüfen + Versionen` pausiert die Hintergrundprüfung, danach läuft sie wieder weiter. Beim Bearbeiten der Zugangsdaten und bei einem Benutzernamen ohne Passwort wird keine neue automatische Prüfung gestartet. Update-Ergebnisse und Versionsangaben werden durch die Hintergrundprüfung nicht geändert. Nur ein ausstehender Neustartauftrag wechselt bei bestätigter Erreichbarkeit nach dem Vorlauf auf **Wieder online**, einschließlich des Informationstexts. Die Prüfungen verwenden `Prüfen parallel` mit höchstens 40 gleichzeitigen Verbindungen und einem Zeitlimit pro Host. Eine noch laufende Runde wird nicht überlappt; bei vielen oder langsam antwortenden Hosts kann das Intervall deshalb länger ausfallen. Beim Schließen der Anwendung wird die Überwachung beendet.

Tests der Verbindungsüberwachung mit simulierten Antworten, ohne Verbindung zu Zielhosts:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\ConnectionWorker.Tests.ps1
dotnet run --project tests\dotnet\ConnectionState.Tests\ConnectionState.Tests.csproj -c Release
dotnet run --project tests\dotnet\HostState.Tests\HostState.Tests.csproj -c Release
```

Die Spaltenreihenfolge lautet: **Checkbox | Hostname | Verbindung | .NET 4.8 | Payment | Fiscal | Server Suite | NovaTouch POS | NovaTouch NTC | Information | Status**. Die schmale .NET-Spalte zeigt **✓** bei installiertem .NET Framework 4.8 oder neuer (auch 4.8.1), **✗** bei fehlendem/älterem Framework und **–** vor einer Prüfung bzw. bei nicht ermitteltem Status. Grundlage ist der Registry-Wert `Release >= 528040` unter `NET Framework Setup\NDP\v4\Full` ([Microsoft-Dokumentation](https://learn.microsoft.com/en-us/dotnet/framework/install/how-to-determine-which-versions-are-installed)). Die POS-Version stammt aus `DisplayVersion` der Programmeinträge mit Namen `NovaTouch POS` unter den 32- und 64-Bit-Uninstall-Schlüsseln in `HKLM`. Fehlt POS, steht dort **Nicht installiert**; das allein verursacht keinen Fehler und verhindert kein Update. Bei registrierter Installation ohne Versionsangabe steht **Version unbekannt**. Beim erneuten Prüfen werden alte Ergebnisse zurückgesetzt. Beide Angaben stehen auch im HTML-Bericht.

Die breite Spalte „Information“ zeigt den aktuellen Bearbeitungsschritt **zusätzlich** zu vorhandenen Fehler- und Informationstexten an. Im ersten Versuch bleibt der Informationstext unverändert. Ab dem zweiten Versuch steht davor **Versuch 2/3:** bzw. **Versuch 3/3:**. Eine eigene Spalte „Versuch“ entfällt. „Status“ steht ganz rechts; im HTML-Bericht steht der aufklappbare Protokollbereich davor. Auch dort erscheinen Wiederholungen im Informationstext.

Die zusätzlichen Scan-Prüfungen lassen sich ohne Netzwerkzugriff mit simulierten Registry- und WinRM-Antworten testen:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\SoftwareInventory.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\ServiceInventory.Tests.ps1
```

## HTML-Bericht

Der HTML-Bericht wird beim Klick auf `HTML-Bericht erstellen` zunächst in einem temporären Vorschaupfad abgelegt und automatisch im Standardbrowser geöffnet (kein Internetzugang nötig). Der Button „HTML speichern“ im Bericht speichert die HTML-Datei auf Wunsch. Enthalten: Datum/Uhrzeit, Zielversionen, Gesamt-/Erfolg-/Fehlerzahlen, Hostliste inklusive Vorher-/Nachher-Versionen, Suche/Statusfilter sowie detaillierte Hostprotokolle. Die Server Suite steht direkt nach Fiscal; alle erfassten Suite-Instanzen werden mit Dienstnamen und Version aufgeführt. Die dynamischen Inhalte werden HTML-escaped. Ein Bericht erfasst den aktuellen Zustand der Anwendung zum Zeitpunkt des Exports.

## Ablauf je Host

1. WinRM-Sitzung aufbauen.
2. Aus der Registry des jeweiligen Dienstes `ImagePath` ermitteln; bei Payment/Fiscal EXE-Versionen auslesen, bei jeder Server-Suite-Instanz die zugehörige `version.txt`.
3. Nur bei abweichender Zielversion (oder bei aktiviertem Force Update): ZIP-Datei in den konfigurierten Zielpfad auf dem Client übertragen und per SHA256 prüfen. Bei aktivierter Option `Installationsdateien nicht übertragen` muss die gleichnamige, identische ZIP dort bereits vorhanden sein; sie wird nur geprüft und nicht kopiert.
4. ZIP in temporären Unterordner am Zielrechner entpacken. Für die Suite müssen die gefüllten Ordner `apps\nt3-server-suite` und `installer\python\portable_python` sowie `apps\nt3-server-suite\version.txt` mit passender Zielversion vorhanden sein. Die registrierte Dienst-EXE muss innerhalb von `portable_python` liegen. Eine bereits auf dem Client vorhandene Dienst-EXE muss nicht in der ZIP enthalten sein und bleibt beim Kopieren erhalten, wenn das Paket keine gleichnamige Datei mitliefert. Fehlt die Dienst-EXE auf dem Client und unter ihrem relativen Pfad in der ZIP, wird dies vor dem Dienststopp gemeldet. Die Python-EXE-Version wird nicht geprüft. Ungültige Pakete werden vor dem Dienststopp abgelehnt.
5. Pro Komponente alle betroffenen Dienste stoppen, bevor deren Dateien verändert werden. Payment/Fiscal werden wie bisher über ihre Installationsordner kopiert. Bei der Suite werden Dateien aus `apps\nt3-server-suite` in `<Installation>\apps\nt3-server-suite` und Dateien aus `installer\python\portable_python` in `<Installation>\portable_python` kopiert. **Client-Zielordner werden nicht gelöscht.** Gleichnamige Dateien werden überschrieben; fehlende Dateien, Unterordner und Zielordner werden angelegt. Zusätzliche lokale Dateien bleiben erhalten. Der Client-Ordner `portable_python` liegt auf derselben Ebene wie `apps`. Andere `apps`- und `installer`-Inhalte werden nicht übertragen. Gemeinsam genutzte Suite- und Python-Ordner werden jeweils einmal beschrieben; unterschiedliche Installationen erhalten ihre eigenen Kopien. Erst nach allen Kopiervorgängen der jeweiligen Komponente werden deren betroffene Dienste wieder gestartet (außer deaktivierte). Auch bei Fehlern wird der Wiederanlauf aller nicht deaktivierten Dienste dieser Komponente versucht.
6. Bei Payment/Fiscal Produkt-/Dateiversion, bei jeder Suite-Instanz `version.txt` und den Zustand der gestarteten Dienste erneut kontrollieren.
7. Bei Fehlern maximal zwei weitere Runden mit kurzer Wartezeit ausführen, jeweils nur für die noch offenen Komponenten. Bereits verifizierte Komponenten werden auch bei Force nicht erneut installiert oder angehalten. Die fehlgeschlagene Komponente wird vollständig wiederholt, selbst wenn eine teilweise kopierte EXE oder `version.txt` bereits die Zielversion meldet. Bei einem Suite-Update werden weiterhin alle Suite-Instanzen gemeinsam gestoppt, ihre Dateien aktualisiert und die Instanzen verifiziert. Sind keine ausgewählten Dienste installiert, wird der Host ohne Fehler übersprungen.
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
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\ServerSuiteUpdate.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\UploadDirectory.Tests.ps1
```

## Grenzen und Sicherheitsaspekte

- **Kein automatisches Rollback**: Werden während des Kopierens Dateien verändert und der Vorgang scheitert, wiederholt der Updater den Versuch. Nach endgültigem Fehler muss der betroffene Host ggf. manuell repariert werden.
- ZIP-Inhalte werden über vorhandene Installationen kopiert. Auch bei der Server Suite bleiben die Zielordner `apps\nt3-server-suite` und `portable_python` sowie zusätzliche lokale Dateien erhalten. Nur im Paket vorhandene gleichnamige Dateien werden überschrieben; neue Dateien und Ordner werden angelegt. Bei einem reinen Payment-/Fiscal-Update bleibt `portable_python` unverändert.
- Dienstneustarts unterbrechen die laufenden Dienste. Vor Produktiveinsatz Wartungsfenster berücksichtigen.
- `Abbrechen` stoppt das Einplanen weiterer Hosts. Ein bereits gestarteter **Update-Schritt** darf zunächst Stop/Kopie/Start abschließen, damit Dienste nicht mitten im Update hängen bleiben.
- `Verbindung` testet die tatsächlich benötigte WinRM-Anmeldung (nicht nur ICMP-Ping); somit werden Hosts mit blockiertem ICMP nicht unnötig aussortiert.
- Das Kennwort wird nicht in Argumentlisten oder Logdateien gespeichert. Für die Übertragung vom GUI-Prozess an den lokalen Worker wird JSON über dessen **StandardInput** verwendet; dabei ist das Kennwort temporär als String im Speicher. Für besonders restriktive Sicherheitsumgebungen wären integrierte Windows-Anmeldung / Credential Manager vorzuziehen.
- Die Zahl gleichzeitig laufender PowerShell-Prozesse kann relevante CPU-/RAM-Last auf dem Steuer-PC und Netzwerk-Last erzeugen. Mit 10–20 Prüfungen und 3–8 Updates beginnen, erst nach Messung erhöhen.
- `RemoteWorker.ps1` und seine Funktionen aus `UpdateSteps.ps1`, `MsiUpdates.ps1` und `UploadDirectory.ps1` sind als Ressourcen in der EXE eingebettet und werden als gemeinsamer temporärer Worker auf dem Steuer-PC gestartet und beim Beenden gelöscht.
- Das hier ist ein **Quellcode-Prototyp**, kein bereits auf echten Systemen validiertes Produktionswerkzeug.

## Logdateien

`%LOCALAPPDATA%\NovaTouchUpdater\Logs\Update-YYYYMMDD-HHMMSS.log`

## Alternative zu PowerShell-Remoting

**Lokal** benötigt kein WinRM. Für entfernte Zielhosts wird weiterhin WinRM verwendet. Andere Remote-Transporte sind nicht implementiert.

## Spaltenbreiten selbst ändern

**Datei:** `src/NovaTouchUpdater/UI/MainForm.cs`, Methode **`ApplyColumnWeights()`**.

Die erste Spalte hat keine Überschrift und bleibt fest auf Checkbox-Breite (26 Pixel bei 100 %, passend zur DPI skaliert). Die Basisbreiten unter `preferredMinimums` und die gemessenen, einzeiligen Überschriften bestimmen den gemeinsamen Mindestbedarf der übrigen Spalten. Der restliche Platz wird nach den `FillWeight`-Werten verteilt: `hostname=11`, `connection=9`, `dotnet48=5`, `payment=6`, `fiscal=6`, `serversuite=9`, `pos=10`, `ntc=10`, `information=32`, `result=8`. Diese Werte verteilen den zusätzlich verfügbaren Platz, sie sind keine festen Prozentanteile der gesamten Tabelle. **Status** reicht bis zum rechten Tabellenrand; Platz für die senkrechte Scrollleiste wird nur bei sichtbarer Scrollleiste abgezogen.

Reicht die Fensterbreite nicht aus, wird das gemeinsame Breitenbudget verkleinert und der Text umgebrochen. Die Spaltenbreiten werden direkt berechnet, damit die rechte Spalte **Status** auch nach Größenänderungen im sichtbaren Bereich bleibt. Das HTML-Layout wird getrennt in `src/NovaTouchUpdater/Reporting/HtmlReport.cs` über CSS gesteuert.
### Windows-Release-Build

Die Basisversion wird ausschließlich in **`version.txt`** im Projektordner geändert. Die Datei enthält eine Anleitung; die mit `#` beginnenden Beschreibungszeilen werden beim Einlesen ignoriert. Nur die Zahl in der letzten Zeile ändern: beispielsweise `1.1` für die Reihe `1.1.x` oder `1.2` für `1.2.x`. Lokale Builds erhalten damit die Version `1.1.0`. Bei GitHub-Releases wird die fortlaufende Workflow-Laufnummer angehängt, beispielsweise `1.1.123`. Die Laufnummer beginnt beim Wechsel der Basisversion nicht erneut bei null. EXE-Dateiname und Release-Tag werden aus der eingebetteten EXE-Version übernommen; weitere Dateien müssen für einen Versionswechsel nicht geändert werden.

`Build-win64.cmd` im Projektordner ausführen (Visual Studio / .NET-8-SDK notwendig).
Damit wird eine eigenständige `win-x64`-EXE veröffentlicht.

Lokaler Test der Versionsübernahme und Release-Benennung (isolierte Testdateien unter `artifacts/test-results`, keine Veröffentlichung):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\Version.Tests.ps1
```

### Single EXE Build

Im Projektordner 
dotnet publish .\src\NovaTouchUpdater\NovaTouchUpdater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o ".\artifacts\publish\win-x64"
### WinRM separat testen

Der Button **Test WinRM** prüft alle importierten Hosts mit der unter **Prüfen parallel** eingestellten Parallelität und den vorhandenen Anmeldedaten (leerer Benutzername: aktuelle Windows-Anmeldung). Geprüft wird ausschließlich das ausgewählte Protokoll: HTTP/5985 oder HTTPS auf dem eingestellten HTTPS-Port (Standard 5986, beispielsweise 443). Protokoll und tatsächlich verwendeter Port stehen im Diagnosefenster; verschiedene Hosts laufen parallel.

Das separate Ergebnisfenster zeigt TCP-Erreichbarkeit, `New-PSSession` und `Invoke-Command` jeweils einzeln. Eine ausgewählte Zeile zeigt vollständige Fehlerdetails. Ohne erfolgreiche Sitzung wird die Befehlsprüfung ausdrücklich als übersprungen ausgewiesen. Die TLS-Zertifikatsprüfung bleibt aktiv. Pro Protokoll gilt ein Gesamtzeitlimit von 60 Sekunden; der TCP-Test wartet maximal 5 Sekunden.

Der Remote-Testbefehl gibt lediglich einen konstanten Text zurück. Es werden keine Dateien, Dienste oder Einstellungen auf den Zielrechnern verändert. Sitzungen werden nach dem Test entfernt. **Abbrechen** beendet lokale Testprozesse und verhindert weitere Tests. Scan-, Update- und Wiederholungsstatus bleiben unverändert.

Lokaler Test der Diagnose-Fehlerpfade ohne Netzwerkzugriff:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\WinRmWorker.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\WinRmPort.Tests.ps1
```
