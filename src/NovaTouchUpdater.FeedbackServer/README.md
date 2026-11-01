# Feedback-Webdienst

Dieser separate .NET-8-Webdienst empfängt Meldungen aus dem Updater und erstellt Issues in `cgassner1000/NovaTouchUpdater`. Meldende benötigen kein GitHub-Konto. Der freiwillige Name steht im Beschreibungstext; ohne Namen steht dort „Anonym“. GitHub zeigt als technischen Issue-Autor das Konto des serverseitigen Tokens.

## Bereitstellen

1. `dotnet publish src/NovaTouchUpdater.FeedbackServer/NovaTouchUpdater.FeedbackServer.csproj -c Release -p:UseAppHost=false -o artifacts/publish/feedback-server`
2. Den veröffentlichten Ordner auf einem .NET-8-Webserver bereitstellen, beispielsweise IIS mit dem ASP.NET Core Hosting Bundle oder einem .NET-fähigen Cloud-Dienst. Eine öffentliche HTTPS-Adresse ist erforderlich. Der Endpoint ist `/api/feedback`.
3. Ausschließlich auf dem Server die Konfiguration `Feedback:GitHubToken` setzen, als Umgebungsvariable `Feedback__GitHubToken` oder über die Geheimnisverwaltung des Hosters. Ein Fine-grained Token des technischen GitHub-Kontos benötigt für dieses Repository **Issues: Read and write**. Token weder in die EXE noch in `config.json`, Repository oder Client-Buildvariablen aufnehmen. Ablaufdatum und Erneuerung am Server verwalten.
4. Die vollständige öffentliche HTTPS-Adresse des Endpoints als GitHub-Repositoryvariable `FEEDBACK_ENDPOINT` setzen. Der Release-Workflow bettet nur diese öffentliche Adresse in die EXE ein. Für lokale Builds kann dieselbe Umgebungsvariable vor `Build-win64.cmd` gesetzt werden. Danach den Updater neu bauen.

Ohne eingebettete Dienstadresse lässt sich das Formular öffnen; der Updater zeigt, dass das Senden noch nicht eingerichtet ist. `GET /health` prüft die Erreichbarkeit des Dienstes; es erstellt kein Issue. Es wurde kein Dienst automatisch bereitgestellt und kein GitHub-Zugang angelegt.

## Verhalten

- Eingaben: Art (`bug`/`enhancement`), Titel, Beschreibung, Name (freiwillig), Updater-Version und zufällige Meldungsnummer. Keine automatisch angehängten Hostlisten, Benutzernamen, Logs, Konfigurationen oder Passwörter.
- Die Meldung wird im öffentlichen Repository veröffentlicht. Server-/Proxy-Zugriffslogs können IP-Adressen enthalten; die Anwendung überträgt diese nicht an GitHub. Namen sind freiwillige, ungeprüfte Angaben.
- Höchstens fünf Anfragen je IP in zehn Minuten, insgesamt höchstens 60. `Feedback__PerIpLimit` kann den IP-Grenzwert zwischen 1 und 20 einstellen. Bei einem Reverse-Proxy die Weitergabe der tatsächlichen Client-IP passend zur Hostingumgebung konfigurieren; nicht beliebige Forwarded-Header aus dem Internet vertrauen.
- 64 KiB maximale Requestgröße. Weitere Grenzen: Titel 160, Beschreibung 12.000, Name 80 Zeichen. GitHub-Zugangsdaten bleiben im Serverprozess; Fehlerantworten enthalten keine GitHub-Antwortinhalte oder Zugangsdaten.
- Wiederholungen derselben Meldung werden im laufenden Serverprozess 15 Minuten lang zusammengeführt. Der Cache ist nicht dauerhaft und wird bei Neustart gelöscht. Es gibt keine automatische Wiederholung eines GitHub-POSTs. Bei unklarer Bestätigung weist der Updater darauf hin, dass das Issue möglicherweise bereits erstellt wurde.
- GitHub-API-Referenz: [Issue erstellen](https://docs.github.com/en/rest/issues/issues#create-an-issue), [Zugangsdaten sicher verwahren](https://docs.github.com/en/rest/authentication/keeping-your-api-credentials-secure).

## Prüfen

`dotnet run --project tests/dotnet/FeedbackServer.Tests/FeedbackServer.Tests.csproj -c Release`

Die Tests starten den Dienst ausschließlich auf Loopback und ersetzen GitHub durch einen simulierten HTTP-Handler. Sie veröffentlichen keine realen Issues. Der Updater-Dialog und sein HTTP-Client werden separat mit `MainForm.Tests --feedback` geprüft.
