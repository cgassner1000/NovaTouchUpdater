using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NovaTouchUpdater;
using NovaTouchUpdater.Feedback;

internal static partial class Program
{
    private static T Field<T>(FeedbackDialog form, string name) => (T)typeof(FeedbackDialog).GetField(name, Private)!.GetValue(form)!;
    private static void FeedbackTests()
    {
        var client = new FakeFeedbackClient();
        using var dialog = new FeedbackDialog(client);
        Assert(All(dialog).OfType<Button>().Count() == 2 && All(dialog).OfType<TextBox>().Count() == 3,
            "the report form has no credential setup button or token field");
        Assert(All(dialog).All(control => !control.Text.Contains(AppReleaseClient.Repository.Split('/')[0], StringComparison.OrdinalIgnoreCase)),
            "the technical repository owner is absent from the report form");
        var title = Field<TextBox>(dialog, "title");
        var description = Field<TextBox>(dialog, "description");
        var reporter = Field<TextBox>(dialog, "reporter");
        var send = Field<Button>(dialog, "send");
        Assert(reporter.Text.Length == 0 && !send.Enabled, "feedback starts anonymous with empty required fields and no automatic user details");
        title.Text = "Fehler beim Update"; description.Text = "Der Dienst bleibt nach dem Update gestoppt.";
        Assert(send.Enabled, "valid anonymous feedback can be sent without a login or a name");
        RenderFeedback(dialog);
        client.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task sending = dialog.SubmitAsync();
        Complete(dialog.SubmitAsync());
        Assert(!send.Enabled && client.Requests.Count == 1, "double-clicking submit while a report is pending sends only one request");
        client.Gate.SetResult(new(42));
        Complete(sending);
        Assert(client.Requests[0] is { Kind: "bug", Name: "" } && Field<Label>(dialog, "status").Text.Contains("#42") && !send.Enabled,
            "an anonymous issue is confirmed inside the app without opening GitHub");

        var retryClient = new FakeFeedbackClient { Fail = true };
        using var retry = new FeedbackDialog(retryClient);
        Field<TextBox>(retry, "title").Text = "Ein Vorschlag";
        Field<TextBox>(retry, "description").Text = "Bitte die Reihenfolge der Pakete ändern.";
        Field<TextBox>(retry, "reporter").Text = "Test Person";
        Field<RadioButton>(retry, "suggestion").Checked = true;
        Complete(retry.SubmitAsync());
        Assert(Field<TextBox>(retry, "description").Text.Length > 0 && Field<Button>(retry, "send").Enabled,
            "a delivery failure retains the report for correction or retry");
        retryClient.Fail = false;
        Complete(retry.SubmitAsync());
        Assert(retryClient.Requests.Count == 2 && retryClient.Requests[0].SubmissionId == retryClient.Requests[1].SubmissionId &&
            retryClient.Requests[1] is { Kind: "enhancement", Name: "Test Person" }, "an unchanged retry keeps its duplicate-protection identifier and voluntary name");
        using var unavailable = new FeedbackDialog(new FakeFeedbackClient { IsConfigured = false });
        Field<TextBox>(unavailable, "title").Text = "Ein Fehler";
        Field<TextBox>(unavailable, "description").Text = "Eine ausreichend lange Beschreibung.";
        Assert(!Field<Button>(unavailable, "send").Enabled && Field<Label>(unavailable, "status").Text.Contains("noch nicht eingerichtet"),
            "a build without a shared credential explains unavailability without asking users to configure access");
        FeedbackCredentialTests();
        Complete(FeedbackHttpTests());
        FeedbackHeaderTests();
    }

    private static async Task FeedbackHttpTests()
    {
        using var handler = new FeedbackHttpHandler();
        using var http = new HttpClient(handler);
        var submission = new FeedbackSubmission(Guid.NewGuid(), "bug", "Ein Fehler", "Eine ausführliche Beschreibung des Fehlers.", "", "1.2.3");
        var credentials = new FakeFeedbackCredentials { Token = FeedbackTestToken };
        var client = new FeedbackClient(http, credentials);
        FeedbackReceipt receipt = await client.SendAsync(submission, CancellationToken.None);
        Assert(receipt.IssueNumber == 73 && handler.Body!.RootElement.EnumerateObject().Select(property => property.Name).Order()
            .SequenceEqual(new[] { "body", "title" }) && handler.LastBody!.Contains("Gemeldet von: Anonym") &&
            handler.LastBody.Contains("NovaTouch Updater: 1.2.3") && !handler.LastBody.Contains(credentials.Token),
            "direct GitHub feedback publishes only the user's content and app version, without its credential or local inventory");
        await client.SendAsync(submission, CancellationToken.None);
        Assert(handler.Posts == 1, "a confirmed submission reuses its receipt without posting a duplicate");
        await client.SendAsync(submission with { SubmissionId = Guid.NewGuid(), Kind = "enhancement", Name = "Test Person" }, CancellationToken.None);
        Assert(handler.LastBody!.Contains("Gemeldet von: Test Person") && handler.Body!.RootElement.GetProperty("title").GetString()!.StartsWith("[Verbesserungsvorschlag]"),
            "direct GitHub issues support improvements with a voluntary name, without an interactive login");
        handler.Status = HttpStatusCode.Unauthorized;
        bool unauthorized = false;
        try { await client.SendAsync(submission with { SubmissionId = Guid.NewGuid() }, CancellationToken.None); }
        catch (InvalidOperationException ex) { unauthorized = ex.Message.Contains("ungültig") && !ex.Message.Contains(credentials.Token); }
        Assert(unauthorized, "an expired GitHub token is reported without exposing the token or GitHub error payload");
        handler.Status = HttpStatusCode.TooManyRequests;
        bool limited = false;
        try { await client.SendAsync(submission with { SubmissionId = Guid.NewGuid() }, CancellationToken.None); } catch (InvalidOperationException ex) { limited = ex.Message.Contains("Zu viele"); }
        Assert(limited, "GitHub request limits are displayed as a readable feedback error");
        handler.Status = HttpStatusCode.Created;
        handler.LoseResponseOnce = true;
        var uncertain = submission with { SubmissionId = Guid.NewGuid() };
        try { await client.SendAsync(uncertain, CancellationToken.None); } catch (HttpRequestException) { }
        int postsBeforeRetry = handler.Posts;
        FeedbackReceipt found = await client.SendAsync(uncertain, CancellationToken.None);
        Assert(found.IssueNumber > 0 && handler.Reads == 1 && handler.Posts == postsBeforeRetry,
            "after a lost response a retry finds the submitted GitHub issue before creating another one");
        int previousCalls = handler.Posts;
        var missing = new FeedbackClient(http, new FakeFeedbackCredentials());
        bool refused = false;
        try { await missing.SendAsync(submission, CancellationToken.None); } catch (InvalidOperationException) { refused = true; }
        Assert(!missing.IsConfigured && refused && handler.Posts == previousCalls, "a missing credential prevents direct GitHub requests");
        credentials.Token = "synthetic\r\ninvalid-token";
        bool malformed = false;
        try { await client.SendAsync(submission with { SubmissionId = Guid.NewGuid() }, CancellationToken.None); }
        catch (InvalidOperationException ex) { malformed = ex.Message.Contains("ungültig") && !ex.Message.Contains(credentials.Token); }
        Assert(malformed && !client.IsConfigured && handler.Posts == previousCalls, "a malformed credential is rejected before creating an HTTP header and is never echoed");
        credentials.Token = "arbitrary-password";
        bool passwordRefused = false;
        try { await client.SendAsync(submission with { SubmissionId = Guid.NewGuid() }, CancellationToken.None); }
        catch (InvalidOperationException ex) { passwordRefused = !ex.Message.Contains(credentials.Token); }
        Assert(passwordRefused && !client.IsConfigured && handler.Posts == previousCalls,
            "a freely chosen password cannot configure GitHub feedback and is never transmitted");
    }

    private static void FeedbackCredentialTests()
    {
        var missing = new EmbeddedFeedbackCredentials(() => null);
        Assert(missing.Read() is null, "a build without an embedded shared credential stays unconfigured");
        int reads = 0;
        var embedded = new EmbeddedFeedbackCredentials(() =>
        {
            reads++;
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes("\r\n" + FeedbackTestToken + "\r\n"));
        });
        Assert(embedded.Read() == FeedbackTestToken && embedded.Read() == FeedbackTestToken && reads == 1,
            "the shared credential is read once from the build resource without per-user configuration");
        var invalid = new EmbeddedFeedbackCredentials(() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("arbitrary-password")));
        Assert(invalid.Read() is null, "an arbitrary password in a build resource is rejected without being displayed");
    }

    private static void RenderFeedback(Form form, string fileName = "feedback-form.png")
    {
        Control content = form.Controls[0];
        using var canvas = new Panel { Size = form.ClientSize, Font = form.Font, BackColor = form.BackColor };
        canvas.Controls.Add(content); canvas.CreateControl();
        foreach (Control control in All(canvas)) _ = control.Handle;
        for (int pass = 0; pass < 4; pass++) Layout(canvas);
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(TestWorkspace.Artifacts, fileName));
        canvas.Size = new Size(560, 460);
        for (int pass = 0; pass < 4; pass++) Layout(canvas);
        Assert(All(canvas).OfType<TextBox>().All(input => input.Width > 0) && !((Panel)content).HorizontalScroll.Visible,
            "feedback remains usable in a narrow window without horizontal scrolling");
        canvas.Controls.Remove(content); form.Controls.Add(content);
    }

    private static void FeedbackHeaderTests()
    {
        foreach (float scale in new[] { 1F, 2F })
        foreach (int width in new[] { 600, 1000, 1710 })
        foreach (bool elevated in new[] { false, true })
        {
            using var notice = new LinkLabel { Text = "Neue Version verfügbar", Visible = true };
            using var feedback = new AppButton { Text = "Report issue", AutoSize = true, Padding = new Padding(12, 7, 12, 7) };
            using var administrator = new AdministratorButton { Visible = !elevated };
            using var header = new UpdaterHeader(notice, feedback, administrator);
            var fonts = All(header).ToDictionary(control => control, control => control.Font);
            header.Scale(new SizeF(scale, scale));
            foreach (var (control, font) in fonts) control.Font = new Font(font.FontFamily, font.SizeInPoints * scale, font.Style);
            using var canvas = new Panel { Size = new Size((int)(width * scale), (int)(300 * scale)) };
            canvas.Controls.Add(header); canvas.CreateControl();
            foreach (Control control in All(canvas)) _ = control.Handle;
            for (int pass = 0; pass < 5; pass++) Layout(canvas);
            Rectangle feedbackBounds = header.RectangleToClient(feedback.RectangleToScreen(feedback.ClientRectangle));
            Assert(feedbackBounds.Right <= header.ClientSize.Width && feedbackBounds.Left >= 0 && !feedbackBounds.IntersectsWith(notice.Bounds),
                $"feedback is right-aligned without overlap with the release notice at width {width} / {scale:P0}");
            Assert(administrator.Visible == !elevated, "the administrator action is hidden only in an elevated header");
            if (!elevated)
            {
                Rectangle administratorBounds = header.RectangleToClient(administrator.RectangleToScreen(administrator.ClientRectangle));
                Assert(administratorBounds.Left >= 0 && administratorBounds.Right <= feedbackBounds.Left &&
                    !administratorBounds.IntersectsWith(notice.Bounds) &&
                    header.Controls.OfType<Label>().Where(label => label is not LinkLabel)
                        .All(label => !administratorBounds.IntersectsWith(label.Bounds) && !feedbackBounds.IntersectsWith(label.Bounds)),
                    $"the shield action fits left of Report issue without overlapping title, subtitle or release notice at width {width} / {scale:P0}");
            }
            if (width == 1710 && scale == 1F)
            {
                using var bitmap = new Bitmap(width, header.Height);
                header.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(TestWorkspace.Artifacts, elevated ? "feedback-header.png" : "administrator-header.png"));
            }
        }
    }

    private sealed class FakeFeedbackClient : IFeedbackClient
    {
        public bool IsConfigured { get; init; } = true;
        internal bool Fail;
        internal TaskCompletionSource<FeedbackReceipt>? Gate;
        internal List<FeedbackSubmission> Requests { get; } = [];
        public Task<FeedbackReceipt> SendAsync(FeedbackSubmission submission, CancellationToken ct)
        {
            Requests.Add(submission);
            if (Fail) throw new InvalidOperationException("Simulierter Sendefehler");
            return Gate?.Task ?? Task.FromResult(new FeedbackReceipt(43));
        }
    }

    private sealed class FeedbackHttpHandler : HttpMessageHandler
    {
        internal JsonDocument? Body;
        internal string? LastBody;
        internal int Posts, Reads;
        internal bool LoseResponseOnce;
        internal HttpStatusCode Status = HttpStatusCode.Created;
        private readonly List<(int Number, string Body)> issues = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert(request.RequestUri?.Scheme == "https" && request.RequestUri.Host == "api.github.com" &&
                request.RequestUri.AbsolutePath == FeedbackClient.IssuesUrl.AbsolutePath && request.Headers.Authorization?.Parameter == FeedbackTestToken,
                "the updater sends directly to the fixed GitHub repository using only the configured test credential");
            if (request.Method == HttpMethod.Get)
            {
                Reads++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(issues.Select(issue => new { number = issue.Number, body = issue.Body })) };
            }
            Posts++;
            Body?.Dispose(); Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            LastBody = Body.RootElement.GetProperty("body").GetString()!;
            if (Status != HttpStatusCode.Created) return new(Status) { Content = JsonContent.Create(new { message = FeedbackTestToken + " must remain private" }) };
            int number = 73 + issues.Count;
            issues.Add((number, LastBody));
            if (LoseResponseOnce) { LoseResponseOnce = false; throw new HttpRequestException("Simulated lost response after GitHub accepted the issue"); }
            return new(Status) { Content = JsonContent.Create(new { number }) };
        }
        protected override void Dispose(bool disposing) { Body?.Dispose(); base.Dispose(disposing); }
    }

    private static readonly string FeedbackTestToken = "github_pat_" + new string('x', 82);

    private sealed class FakeFeedbackCredentials : IFeedbackCredentials
    {
        internal string? Token;
        public string? Read() => Token;
    }
}
