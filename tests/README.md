# Automatische Prüfungen

`MainForm.Tests --packages` prüft Paketauswahl und Entfernen über das rote × für alle sieben Produkte: nur das angeklickte Paket abwählen, unveränderte Quelldateien einschließlich gemeinsam genutzter ZIPs, Update-Aufträge ohne das entfernte Produkt, erneute Auswahl, Erhalt der Abwahl beim Einlesen des Ordners und explizite Wiederherstellung der Ordnerauswahl. Außerdem werden Sperren während laufender Vorgänge, die letzte Entfernung und das Zurücksetzen veralteter Prüfsummen beziehungsweise Datenbank-Upload-Ergebnisse geprüft. `--sections` prüft die Anordnung der Schaltflächen bei schmalen Fenstern und 100–200 % Skalierung sowie das Offenhalten des Bereichs beim Entfernen. Es werden keine Installer oder Wartungsaktionen auf echten Hosts ausgeführt.

`MainForm.Tests --administrator` prüft den optionalen Administratorstart: UAC-Aufruf mit aktuellem EXE-Pfad und Argumenten, Abbruch ohne Schließen, Sperre während aktiver Vorgänge und gegen Doppelklicks sowie Schließen nach erfolgreichem Neustart. UAC und Prozessstarts sind simuliert. Die Rechteerkennung liest echte Windows-Prozesstokens. Wenn das Testkonto ein UAC-Tokenpaar besitzt, wird zusätzlich geprüft: Mitglied der Administratorengruppe mit normalem Token → nicht erhöht, derselbe Benutzer mit erhöhtem Token → erhöht; eine Thread-Impersonation darf den Prozessstatus nicht ändern. Ohne solches Tokenpaar wird nur diese zusätzliche Prüfung mit einer ausdrücklichen Meldung übersprungen. Der Header wird mit und ohne Prozesserhöhung bei 100 % und 200 % Skalierung und verschiedenen Fensterbreiten geprüft. Vorschauen: `artifacts/test-results/administrator-header.png` und `feedback-header.png`.

`ServerSuiteSetup.Tests.ps1` und `ServerSuiteUpdate.Tests.ps1` prüfen zusätzliche Prozessbereinigung nach dem Dienststopp: alle Namen mit Präfix `nt3_server_suite`, beliebige Suffixe und Groß-/Kleinschreibung, unveränderte andere Prozesse, kein taskkill ohne Treffer, Abbruch vor Dateiänderungen bei Zugriffsfehlern oder weiterhin laufenden Prozessen, zwischenzeitlich verschwundene Prozesse und Dienstwiederherstellung bei Fehlern. Der gemeinsame Testersatz in `Shared/ServerSuiteProcesses.ps1` führt den echten Prozess-Hilfscode aus und ersetzt nur Prozessenumeration und das native taskkill-Programm durch Daten im Speicher. Die Übernahme des Hilfscodes in lokale und entfernte Setup-/ZIP-Aufrufe wird ebenfalls geprüft. Es werden keine realen Prozesse beendet.

`MainForm.Tests --staging` prüft zusätzlich den gespeicherten Remote-Schalter ohne dauerhafte Paketkopie, dessen Vorrang und das Überspringen der Zielordner-Prüfung. `ServerSuiteUpdate.Tests.ps1` installiert Payment, Fiscal und Server-Suite-ZIPs in isolierten Testverzeichnissen über den tatsächlichen Worker, mit temporären Paketen und simulierter Dienstverwaltung; geprüft werden unveränderter Zielordner, SHA256 und Aufräumen bei Erfolg und Fehler. `ServerSuiteSetup.Tests.ps1` prüft die temporäre Bereitstellung des Setups für alle Instanzen ohne echte Installer-Ausführung. `MainForm.Tests --sections` prüft den neuen Schalter bei unterschiedlichen Fensterbreiten und Skalierungen.

`MainForm.Tests --feedback` prüft das Meldeformular ohne Token-Eingabe oder Einrichtungs-Schaltfläche, anonymes Melden und freiwillige Namen, Kategorien, Eingabeprüfung, Fehler und Wiederholung, Doppelklickschutz, fehlende gemeinsame Zugangsdaten sowie die Platzierung rechts oben bei 100 % und 200 % Skalierung. Der echte HTTP-Client wird mit einem simulierten GitHub-Handler geprüft: direkter Versand an das feste Repository, nur Titel und Inhalt im JSON, Token ausschließlich im Authorization-Header, ungültige Tokens und Passwörter ohne Netzwerkzugriff, Anfragelimits, Wiederverwendung bestätigter Ergebnisse und Erkennung einer bereits erstellten Meldung nach verlorener Bestätigung. Der gemeinsame Token wird mit synthetischen Ressourcenströmen geprüft, einschließlich fehlender oder ungültiger Ressourcen und einmaligem Einlesen. Bestehende Benutzer-Zugangsdaten werden nicht gelesen oder verändert. Es werden keine realen GitHub-Issues erstellt. Vorschaubilder: `artifacts/test-results/feedback-form.png` und `feedback-header.png`.

`MainForm.Tests --updates` prüft auch Uploads trotz fehlgeschlagener lokaler und entfernter Dienste: Nach erfolgreichen Uploads laufen optionale Remote-POS-Neustarts weiter, während Dienstfehler sichtbar und unabhängig wiederholbar bleiben. Gleichzeitige Dienst- und Uploadfehler werden gemeinsam erhalten; ein später erfolgreicher Upload entfernt ausschließlich seinen eigenen Fehler.

`MainForm.Tests --updates` prüft zusätzlich die Abschlussphasen nach den Installationen: alle Uploads vor dem ersten automatischen POS-Neustart, keine lokalen POS-Neustarts, Upload an/aus, gespeicherte Schalter, kein Neustart bei deaktiviertem Upload, Fehler und Wiederholung nur ausstehender Abschlussaktionen. Verifizierte Dienstnamen müssen bei Instanzfehlern an Wiederholungen übergeben werden. `ServerSuiteUpdate.Tests.ps1` prüft dazu echte Worker-Abläufe mit simulierten Diensten: erfolgreiche Instanzen nach Versions-/Statusfehlern oder fehlender anderer Instanz behalten, nur fehlgeschlagene Instanzdateien erneut kopieren und Windows EventLog unverändert lassen. `MainForm.Tests --database` prüft das stille Auslassen von Hosts ohne IRIS-Instanz; Verbindungsfehler bleiben sichtbar. Alle Host-/Dienstaktionen sind simuliert.

`RemoteSoftwareDatabase.Tests.ps1` prüft die Remote-IRIS-Erkennung mit aktiven CPF-Testdateien, Versionsentscheidungen vor der Übertragung, begrenzte MSI-Streams zu mehreren parallelen Namespace-Transaktionen, Abbruch/Rollback, den echten lokalen Hilfsprozess sowie Kopier-, Streaming- und vorhandene-Datei-Modus mit simulierten Remote-Aufrufen. `RemoteDatabaseDispatch.Tests.ps1` prüft WinRM-Einstellungen, JSON-Inventar, unveränderte Streaming-Flags, Sitzungsbereinigung und die weiterhin gesperrte Remote-MSI-Installation. Beide Tests verwenden ausschließlich isolierte Dateien und einen Datenbankersatz ohne Netzwerkverbindungen oder Installer.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\RemoteSoftwareDatabase.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\RemoteDatabaseDispatch.Tests.ps1
```

`MainForm.Tests --database` umfasst lokale, entfernte und kombinierte Inventare, Hostzuordnung, WinRM-Zugangsdaten/HTTPS-Port, Hosts ohne IRIS und Verbindungsfehler. `--staging` prüft den Remote-Datenbank-Upload vor zulässigen Dienstinstallationen sowie reine Remote-MSI-Uploads. `--sections` prüft die ausgerichteten Produkt-/Versionsspalten bei 100–200 % Skalierung. `SoftwareDatabase.Tests.ps1` deckt fehlende, gleiche, ältere und neuere Datenbankversionen ab.

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

Server-Suite-Setup-Verfahren (zusätzlich Inno Setup 6 und lokales `nt3-server-suite`-Repository erforderlich):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\ServerSuiteSetup.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\ServerSuiteSilentSelection.Tests.ps1
dotnet run --project tests\dotnet\MainForm.Tests\MainForm.Tests.csproj -c Release -- --suite-setup
```

Der PowerShell-Test kompiliert den echten Inno-Code mit kleinen Testdateien und prüft die Produkt-/Protokollmetadaten. Die erzeugte EXE wird **niemals ausgeführt**. Alle Dienst-, WinRM- und Setup-Aufrufe sind simuliert. Abgedeckt sind mehrere Instanzen mit Leerzeichen im Pfad, Dienststopp vor allen Setup-Aufrufen, Dienststart nach dem letzten Setup, unverändertes EventLog, einzelne Setup-/Startfehler, deaktivierte Dienste, Exitcode 3010, gezielte Wiederholung, SHA256-Prüfung, lokale Direktquellen sowie Remote-Kopieren und Wiederverwendung. `-SuiteRepository` erlaubt einen anderen Quellpfad. Die C#-Prüfung nutzt das erzeugte Fixture für rekursive Erkennung, EXE-Vorrang, Ablehnung alter Setups und getrennte lokale/Remote-Setup-Requests. Vor einem produktiven Rollout bleibt ein Update auf einem Testclient mit dem tatsächlich freizugebenden Setup erforderlich.

Die Inno-Kompilierung prüft zusätzlich den Standardaufruf ohne `/DVersion` mit der tatsächlichen `version.txt`, drei- und vierteilige CI-Versionen sowie Datums- und Suffixversionen. Die textuelle PE-Produktversion muss in jedem Fall erhalten bleiben. Dadurch werden unzulässige numerische Versionsdirektiven bei normalen IDE-Builds erkannt.

`ServerSuiteSilentSelection.Tests.ps1` führt die tatsächlichen Pascal-Funktionen zur Instanzauswahl in einem separaten Inno-Testprogramm ohne Payload aus. Dieses beendet sich bereits in `InitializeSetup`, vor Installation, Wizard, Dienst- oder Registryzugriff. Die 21 Fälle prüfen `/SERVICENAME`, `/INSTANCE`, `/DIR`, widersprüchliche/mehrdeutige Angaben, Erhalt der bisherigen parameterfreien Auswahl sowie die erste stille Standardinstallation. Beide Tests erwarten ein eigenständiges Produktionsskript ohne zusätzliche ISS-Includes.

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

`MainForm.Tests --winrm` prüft die Reihenfolge der WinRM-Diagnose: lokale Vorbereitung mit allen geladenen Remote-Hosts vor den Tests der ausgewählten Hosts, unveränderte Protokoll-/Port-/Anmeldedaten, Fehleranzeige und Abbruch ohne anschließende Remote-Tests. `WinRmPreparation.Tests.ps1` simuliert lokale Dienste, WSMan und TrustedHosts vollständig. Geprüft werden Ergänzung und Erhalt bestehender Einträge, Duplikate, fehlender Dienst, Start gestoppter/deaktivierter Dienste, laufender Start/Stopp, Fortsetzen pausierter Dienste, Startfehler und Zeitlimit, lokale Diagnose ohne Remote-Hosts, defekter lokaler Endpunkt, fehlende Rechte, Richtlinien sowie ungültige Hostnamen. Die UI-Prüfung deckt außerdem frei bearbeitbare Einstellungen in allen drei Ausführungsmodi, die lokale Rückfallauswahl und die Sperre während laufender Vorgänge ab. Keine dieser Prüfungen ändert reale TrustedHosts oder Dienste.

`MainForm.Tests --sections` prüft die aufklappbaren Bereiche für Einstellungen, Remote-Hosts und einzelne Update-Dateien bei 100 % und 200 % Skalierung und verschiedenen Fensterbreiten. Geprüft werden die Erweiterung des ursprünglichen Blocks ohne rechteckigen Rahmen, Pfeile, Überlappung ohne Verschieben der anderen Bereiche, Eingabenerhalt, Bereichswechsel, Escape, Klick außerhalb und das Schließen des Fensters mit geöffnetem Block. Weitere Prüfungen decken die Liste aller sieben Pakete, den Zielpfad oberhalb des Speichern-Buttons und das Ein-/Ausblenden des Ports ohne Wertverlust ab. Alle Steuerelemente bleiben verborgen; Hosts und Worker sind simuliert. Vorschaubilder entstehen unter `artifacts/test-results/main-sections-open.png`, `settings-popup.png`, `main-packages.png` und `individual-files.png`.

`MainForm.Tests --local` prüft außerdem den Vorrang der Einzelauswahl je Produkt für ZIPs und MSIs: nur das gewählte Produkt ersetzen, alle anderen Ordnerpakete und frühere Einzelersetzungen behalten, die gesamte Auswahl beim erneuten Einlesen erhalten, vollständige Ordnerauswahl explizit wiederherstellen und die Kombination aus Ordner- und Einzelpaketen im Update-Request verwenden. Ohne geladenen Ordner wird nur das einzeln gewählte Produkt ausgewählt.

`MainForm.Tests --database` prüft die lokale IRIS-Inventarliste mit simulierten Registrierungseinträgen und CPF-Dateien in Testordnern: beide Registry-Ansichten, doppelte Instanzen, aktive CPF statt Standarddatei, alle Namespaces ohne Präfix `%`, Teilfehler, Abbruch und unveränderte Quelldateien. Das neue Fenster wird mit künstlichen Ergebnissen auf Gruppierung, Details, Aktualisieren, Fehleranzeige und Schließen während einer Abfrage geprüft. Die Sichtbarkeit des Buttons wird für lokale, entfernte, kombinierte und automatisch lokale Ziele geprüft. Es werden keine Datenbankverbindungen, Worker oder Dienste gestartet. Eine Vorschau entsteht unter `artifacts/test-results/database-check.png`.

Optional liest `--database-live` die tatsächlich lokal registrierten IRIS-Instanzen und ihre CPF-Dateien. Dafür muss mindestens eine lesbare IRIS-Instanz installiert sein. Auch diese Prüfung ändert keine Registrierung, Konfiguration oder Dienste und öffnet keine Datenbankverbindung; die Vorschau liegt unter `artifacts/test-results/database-check-local.png`.

```powershell
dotnet run --project tests\dotnet\MainForm.Tests\MainForm.Tests.csproj -c Release -- --database
dotnet run --project tests\dotnet\MainForm.Tests\MainForm.Tests.csproj -c Release -- --database-live
```

`MainForm.Tests --sections` simuliert außerdem die Deaktivierung des Hauptfensters und Klicks beziehungsweise Escape im Dateiauswahldialog. Der Bereich für einzelne Update-Dateien muss während des Dialogs sowie nach mehreren Paketauswahlen und Abbruch geöffnet bleiben. Nach einem Dialogfehler müssen Klick außerhalb und Überschrift weiterhin schließen. Es werden keine echten Dateiauswahldialoge geöffnet.

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

`MainForm.Tests --staging` prüft zusätzlich die unabhängigen Schalter für lokale Paketkopien und Remote-Uploads, Reihenfolge und Beschriftung, Config-Rundlauf mit aktivierten/deaktivierten Optionen, einen leeren lokalen Zielpfad sowie den Remote-Standardpfad. Direkte lokale Updates dürfen keinen `stage-packages`-Aufruf auslösen und müssen die ursprünglichen Paketpfade behalten. Lokale Updates ohne IRIS-Instanzen überspringen den Datenbank-Upload; eine separate lokale Verbindungsprobe bleibt entbehrlich. Die Hash-Vorbereitung wird mit gemeinsamen MSI-ZIPs und CAB-Dateien geprüft: jede benötigte Quelle einmal lesen, höchstens zwei parallele Lesevorgänge, vorbereitete Prüfsummen bei der Paketbereitstellung wiederverwenden, bei einem neuen Durchlauf neu berechnen, bei Wiederholungen nur ausstehende Komponenten berücksichtigen und nach Abbruch keine Teilresultate verwenden. `--sections` prüft die Lesbarkeit beider Schalter bei schmalen Fenstern und 100–200 % Skalierung. `ConfigStore.Tests` prüft beide gespeicherten Flags, alte Configs und explizit leere Zielpfade.

`MsiUpdates.Tests.ps1` und `ServerSuiteUpdate.Tests.ps1` führen die tatsächlichen Worker-Abläufe mit simulierten Diensten und Installern aus. Die Fälle prüfen einzelne lokale MSIs direkt am Quellort, externe CAB-Dateien, temporär entpackte MSI-/Dienst-ZIPs, leere Zielpfade und Remote-Transfers der drei Dienstpakete. Es darf keine zusätzliche Paketkopie oder Anlage des ungenutzten Zielordners geben; geänderte Quellprüfsummen müssen vor der Installation scheitern. Temporäre Entpackordner dieser Tests liegen ausschließlich innerhalb ihrer geprüften Testordner und werden bereinigt.

`MainForm.Tests --updates` prüft mit simulierten Workern die getrennten Updateergebnisse für Payment, Fiscal und Server Suite: automatische und manuelle Wiederholung nur fehlgeschlagener Komponenten, Force-Modus, Uploadvorbereitung, Wiederaufnahme nach Abbruch oder Neustart sowie die gemeinsame Statusanzeige bei erfolgreichen, aktuellen und nicht installierten Komponenten. `ServerSuiteUpdate.Tests.ps1` prüft die vollständigen Worker-Schritte mit künstlichen ZIPs und simulierten Diensten, auch für Wiederholungen bei bereits erreichter Versionsnummer. Echte Hosts und Windows-Dienste werden dabei nicht verändert.

Die zusätzlichen Startoptionen der `AppRelease.Tests` prüfen einen veröffentlichten Test-Updater und dessen Neustart. Diese Integrationsprüfungen starten ausschließlich die separate Test-EXE unter `artifacts/test-results`; die Befehle sind in der Haupt-README beschrieben. Sie sind kein Update der eigentlichen Anwendung.

`MainForm.Tests --local` prüft den Paketkatalog mit echten, eigens erzeugten MSI-Datenbanken (nur lesend), ZIPs mit MSIs, externe CAB-Dateien, rekursive Suche über mehrere Ordnerebenen, Versionsauswahl zwischen Haupt- und Unterordnern, Fehlerhinweise mit relativen Pfaden, Pfadprüfung sowie die drei Ausführungsmodi und direkte lokale Requests mit simulierten Workern. Die erzeugten MSI-Testdateien werden für den folgenden PowerShell-Test unter `artifacts/test-results` behalten:

```powershell
dotnet run --project tests\dotnet\MainForm.Tests\MainForm.Tests.csproj -c Release -- --local
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\MsiUpdates.Tests.ps1
```

`MsiUpdates.Tests.ps1` prüft fehlende, gleiche, neuere und ältere lokale Produkte, MSI-Versionsvergleiche, MSI in ZIP, externe CABs, erneute Metadatenprüfung, Installerfehler, Neustartbedarf und temporäre Bereinigung. Remote müssen alle vier MSI-Produkte schon vor WinRM-Verbindungen, Transfers oder Installationen abgewiesen werden, sowohl als einzelnes Paket als auch als Paketliste, bei Force, Wiederholung und Uploadvorprüfung. `MainForm.Tests --local` prüft dazu die Auswahl und Hostanzahl im kombinierten Modus, Remote-Requests ohne MSIs und das Entfernen alter Remote-MSI-Fehler aus der Wiederholung. `msiexec` wird niemals gestartet: Der Prozessaufruf ist simuliert.

`ServerSuiteUpdate.Tests.ps1` prüft zusätzlich den vollständigen lokalen ZIP-Dienstablauf und bricht ab, falls dabei ein WinRM-Aufruf versucht wird. Lokal und remote muss die Reihenfolge **Suite stoppen → alle ausstehenden Suite/Python-Installationen abschließen → Suite starten** gelten. Windows EventLog bleibt unverändert; ein Zugriff auf den Dienst lässt den Test scheitern. Geprüft werden auch anfangs gestopptes EventLog, dessen Stopp-/Startbeschränkungen und die Wiederherstellung der Suite nach Kopierfehlern. Installations- und Startfehler müssen gemeinsam sichtbar bleiben. Echte Programme oder Dienste werden nicht verändert.

Die Python-Bereinigung wird lokal und remote mit alten Dateien und Unterordnern, versteckten und schreibgeschützten Inhalten sowie mehreren Dienst-EXEs geprüft. `nt3_server_suite*.exe` müssen unabhängig von Suffix, Groß-/Kleinschreibung und Unterordnern bytegleich erhalten bleiben, auch bei einer gleichnamigen Datei im Paket. Fehlende geschützte EXEs dürfen aus dem Paket ergänzt werden. Eine registrierte EXE außerhalb des Musters ohne Paket-Ersatz muss vor Dienststopp und Bereinigung scheitern. Gemeinsame Runtime-Ordner dürfen nur einmal bereinigt werden; Löschfehler müssen die Suite-Dienste wiederherzustellen versuchen und EventLog unverändert lassen. Paketvalidierung, Pfadgrenzen und Verknüpfungsprüfungen müssen vor der Bereinigung greifen. Alle Löschungen bleiben auf die geprüften Testordner begrenzt.

`SoftwareDatabase.Tests.ps1` prüft den lokalen `zzzSoftware`-Upload mit echten MSI-Testdateien und einem InterSystems-Provider als Simulation im Speicher. Mehrere CPF-Instanzports, alle ausgewählten Produkte, USER, Ausschluss aller Namespaces mit Präfix %, fehlende Klassen, Binärdaten aus MSI und ZIP, MSI-Streamprojektion, Metadaten, Quellenänderungen, Transaktionen mit Rollback, Fehler ohne Zugangsdaten im Protokoll und temporäre Bereinigung werden geprüft. Die Simulation lehnt dynamische Klassen-/Property-/Methodenaufrufe ab und verlangt die Bindings-Assembly für Binärstreams. Der echte Runspace-Pool muss parallele Namespace-Transaktionen bis zum eingestellten Limit ausführen, mit je einer eigenen Verbindung auf demselben Thread. Fehlerstatus beim Leeren/Speichern des Streams und Speichern des Objekts werden inklusive Ursache gemeldet; Rollbacks betreffen nur den jeweiligen Namespace. Andere erreichbare Namespaces müssen trotz einzelner Verbindungs- oder Uploadfehler fertig werden. Byte-Fortschritte müssen Namespace und MSI enthalten. Der tatsächliche Worker muss lokale Uploads vor jeder Dienst-/WinRM-Aktion ausführen; die gesonderten Remote-Tests prüfen die Ausführung auf dem Zielrechner. Die alte XML/INI-Erkennung bleibt als isolierte Entwicklungsreferenz getestet und bestimmt keine Uploadziele der Anwendung. Es werden keine echten Datenbankverbindungen geöffnet. `MainForm.Tests --staging` prüft zusätzlich die unveränderte Übernahme von Update parallel auch oberhalb von vier Namespaces, kompakte Fehlercodes je Namespace bei vollständigem Hostprotokoll sowie eine frische Fortschrittsanzeige beim Fortsetzen. `--updates` und `HostState.Tests` prüfen das Zurücksetzen alter Anzeigefehler und die abschließende Wiederherstellung noch offener Komponentenfehler. `--database` prüft Portauswertung, automatische feste Anmeldung ohne angezeigte Zugangsdaten, den in Einstellungen verschobenen TLS-Schalter und dessen Config-Rundlauf unabhängig von WinRM-HTTPS und den rechts angeordneten BETA-Button.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\SoftwareDatabase.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\SoftwareDatabaseProvider.Tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\SoftwareDatabaseDiscovery.Tests.ps1
dotnet run --project tests\dotnet\MainForm.Tests\MainForm.Tests.csproj -c Release -- --iris-provider
```

`MainForm.Tests --iris-provider` prüft die tatsächlich eingebetteten NuGet-DLLs und Lizenzhinweise, einmalige Bereitstellung, Laden der passenden Framework-Assemblies in Windows PowerShell, die benötigten Objekt-/Stream-APIs sowie Bereinigung ohne Löschen benachbarter Dateien. Der echte Worker erhält den bereitgestellten Pfad und wird mit leeren Datenbankzielen vor Datenbankzugriffen gestoppt. Auch Remote-Uploads erhalten die eingebetteten Provider; leere Paketanforderungen müssen vor jeder Verbindung scheitern. `SoftwareDatabase.Tests.ps1` prüft zusätzlich, dass lokale Uploads ausschließlich den übergebenen mitgelieferten IRIS-Provider verwenden und bei fehlender Bereitstellung abbrechen. Es werden keine Datenbankverbindungen geöffnet.

`SoftwareDatabaseProvider.Tests.ps1` lädt eigens erzeugte minimale Assemblies mit der tatsächlichen CLR von Windows PowerShell. Die erhaltene Loader-Entwicklungsreferenz wird auf Framework-Verzeichnisse, Versionsreihenfolge, Bindings, Fallback und genaue Fehlermeldungen mit Pfad, Exception-Typ und HRESULT geprüft. Der produktive Upload erhält ausschließlich den aus der EXE bereitgestellten NuGet-Providerpfad. Es werden keine installierten Anwendungs-DLLs verwendet oder Datenbankverbindungen geöffnet. Bereits geladene Test-DLLs können bis zum Prozessende gesperrt bleiben und liegen ausschließlich unter `artifacts/test-results`.

`SoftwareDatabaseDiscovery.Tests.ps1` verwendet simulierte Update-Dienste und Konfigurationen in Testordnern. Geprüft werden NTC-XML, POS-INI ohne Einstellungen-Ordner, Unicode, verschlüsselte Zugangsdaten, Server/Port/Namespace/SSL, doppelte Verbindungen, gestoppte Dienste, Dienstnamen und Anzeigenamen, EXE-Pfade mit Leerzeichen/Parametern/Umgebungsvariablen sowie fehlende und ungültige Konfigurationen. Registrierungszugriffe auf Programme sowie Dienststarts und Prozessstarts führen im Test zum Fehler. Die tatsächlichen Windows-Dienste werden nicht abgefragt oder verändert.

`PowerShellValidation.Tests.ps1` prüft die Build-Prüfung mit gültigen Skripten, Syntaxfehlern und Git-Konfliktmarkern. Auch Konfliktmarker in syntaktisch gültigen Textblöcken müssen den Build verhindern. Die Testskripte werden ausschließlich geparst; ihre Inhalte werden nicht ausgeführt.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\powershell\PowerShellValidation.Tests.ps1
```

`Version.Tests.ps1` prüft die Basisversion aus `version.txt`, lokale `.0`-Builds, die per Versionsreihe vorbereitete `UPDATER_BUILD_NUMBER`, Unabhängigkeit von `GITHUB_RUN_NUMBER`, Pflichtwerte im GitHub-Workflow sowie übereinstimmende EXE-Versionen, Dateinamen und Tags. `ReleasePolicy.Tests.ps1` prüft Neustart bei `.1` für neue Reihen, numerische Fortsetzung, Archivierung des letzten Releases jeder alten Reihe, drei Releases der aktuellen Reihe, Schutz manueller Releases/Drafts/Prereleases, vollständige paginierte Antworten und Abbruch bei API-/Bereinigungsfehlern. Die echten Release-Skripte laufen dabei mit einer simulierten GitHub-CLI; es werden keine Releases oder Tags auf GitHub verändert.

MainForm.Tests --updates deckt die POS-Freigabe über tatsächlich gespeicherte POS-MSI-Arten ab: ältere Clientversion erforderlich, kein Neustart ohne POS-Auswahl oder nach reinem NTC-Upload, gleiche/neue/unklare Clientversionen, keine IRIS-Instanz, unveränderte Datenbankversionen, fehlgeschlagene Uploads, lokaler Server mit Remote-POS und reine POS-Wiederholung mit erhaltenem Commit-Nachweis aus demselben Durchlauf. SoftwareDatabase.Tests.ps1 und RemoteSoftwareDatabase.Tests.ps1 prüfen Produkttypen erst nach Commit und fehlenden Nachweis nach Rollback. --targeting prüft alle sieben Produktfilter, Mehrfachauswahl, Ausschluss unnötiger WinRM-Probes/Scans und Uploadvorprüfungen, die IRIS-Server-Ausnahme, den frühen Administratorhinweis und die neuen Einstellungsbeschriftungen.
