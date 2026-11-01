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
            "a missing feedback endpoint is explained without pretending to submit a report");
        Complete(FeedbackHttpTests());
        FeedbackHeaderTests();
    }

    private static async Task FeedbackHttpTests()
    {
        using var handler = new FeedbackHttpHandler();
        using var http = new HttpClient(handler);
        var submission = new FeedbackSubmission(Guid.NewGuid(), "bug", "Ein Fehler", "Eine ausführliche Beschreibung des Fehlers.", "", "1.2.3");
        var client = new FeedbackClient(http, new Uri("https://feedback.example.invalid/api/feedback"));
        FeedbackReceipt receipt = await client.SendAsync(submission, CancellationToken.None);
        Assert(receipt.IssueNumber == 73 && handler.Body!.RootElement.EnumerateObject().Select(property => property.Name).Order()
            .SequenceEqual(new[] { "appVersion", "description", "kind", "name", "submissionId", "title" }),
            "the app sends only form fields and its version, without config, credentials, host inventory or logs");
        Assert(!new FeedbackClient(http, new Uri("http://feedback.example.invalid/api/feedback")).IsConfigured,
            "desktop feedback accepts only an HTTPS service endpoint");
        handler.Status = HttpStatusCode.TooManyRequests;
        bool limited = false;
        try { await client.SendAsync(submission, CancellationToken.None); } catch (InvalidOperationException ex) { limited = ex.Message.Contains("Zu viele"); }
        Assert(limited, "service request limits are displayed as a readable feedback error");
    }

    private static void RenderFeedback(Form form)
    {
        Control content = form.Controls[0];
        using var canvas = new Panel { Size = form.ClientSize, Font = form.Font, BackColor = form.BackColor };
        canvas.Controls.Add(content); canvas.CreateControl();
        foreach (Control control in All(canvas)) _ = control.Handle;
        for (int pass = 0; pass < 4; pass++) Layout(canvas);
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "feedback-form.png"));
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
        {
            using var notice = new LinkLabel { Text = "Neue Version verfügbar", Visible = true };
            using var feedback = new ModernButton { Text = "Fehler / Vorschlag melden", AutoSize = true, Padding = new Padding(12, 7, 12, 7) };
            using var header = new UpdaterHeader(notice, feedback);
            var fonts = All(header).ToDictionary(control => control, control => control.Font);
            header.Scale(new SizeF(scale, scale));
            foreach (var (control, font) in fonts) control.Font = new Font(font.FontFamily, font.SizeInPoints * scale, font.Style);
            using var canvas = new Panel { Size = new Size((int)(width * scale), (int)(250 * scale)) };
            canvas.Controls.Add(header); canvas.CreateControl();
            foreach (Control control in All(canvas)) _ = control.Handle;
            for (int pass = 0; pass < 5; pass++) Layout(canvas);
            Assert(feedback.Right <= header.ClientSize.Width && feedback.Left >= 0 && !feedback.Bounds.IntersectsWith(notice.Bounds),
                $"feedback is right-aligned without overlap with the release notice at width {width} / {scale:P0}");
            if (width == 1710 && scale == 1F)
            {
                using var bitmap = new Bitmap(width, header.Height);
                header.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "feedback-header.png"));
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
        internal HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert(request.Headers.Authorization is null, "the distributed app contains no GitHub authentication header");
            Body?.Dispose(); Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            return new(Status) { Content = JsonContent.Create(new FeedbackReceipt(73)) };
        }
        protected override void Dispose(bool disposing) { Body?.Dispose(); base.Dispose(disposing); }
    }
}
