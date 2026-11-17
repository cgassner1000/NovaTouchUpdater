using NovaTouchUpdater;

internal static partial class Program
{
    private static void PackageLoadingTests()
    {
        string root = Path.Combine(TestWorkspace.Artifacts, "package-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousContext = SynchronizationContext.Current;
        using var context = new WindowsFormsSynchronizationContext();
        try
        {
            using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
            form.Size = new Size(1300, 800); form.Show(); Application.DoEvents();
            SynchronizationContext.SetSynchronizationContext(context);
            var spinner = Field<LoadingSpinner>(form, "packageLoadingSpinner");
            var status = Field<Label>(form, "packageStatus");
            Control content = form.Controls[0];
            Assert(!spinner.Active && !spinner.Visible, "no loading indicator is shown while idle");
            foreach (string mode in new[] { "Light", "Dark" })
            {
                UiTheme.SetMode(mode);
                status.Text = "Softwarepakete werden gelesen …";
                SetField(form, "loadingPackages", true); Call(form, "UpdateButtons"); Application.DoEvents();
                Assert(spinner.Visible && spinner.Width >= 112 && spinner.AccessibleRole == AccessibleRole.ProgressBar &&
                    spinner.Left + spinner.Width / 2 == form.ClientSize.Width / 2 && spinner.Top + spinner.Height / 2 == form.ClientSize.Height / 2 &&
                    form.Controls[0] == spinner && form.GetChildAtPoint(new Point(form.ClientSize.Width / 2, form.ClientSize.Height / 2)) == spinner &&
                    status.Text == "Softwarepakete werden gelesen …" && Field<Button>(form, "btnCancel").Enabled,
                    "a large centered spinner appears above the window while the existing hint and cancel action remain available; " + mode);
                using var first = new Bitmap(spinner.Width, spinner.Height);
                spinner.DrawToBitmap(first, spinner.ClientRectangle);
                Complete(Task.Delay(120));
                using var second = new Bitmap(spinner.Width, spinner.Height);
                spinner.DrawToBitmap(second, spinner.ClientRectangle);
                Assert(!first.GetPixel(spinner.Width / 2, 18).Equals(second.GetPixel(spinner.Width / 2, 18)) ||
                    Enumerable.Range(0, spinner.Width).Any(x => first.GetPixel(x, spinner.Height / 2) != second.GetPixel(x, spinner.Height / 2)),
                    "the loading ring advances between frames; " + mode);
                using var screenshot = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(screenshot, new Rectangle(Point.Empty, form.Size));
                // DrawToBitmap omits the sibling overlay above the docked content.
                // Native hit testing above verifies the actual front-to-back order.
                using (var graphics = Graphics.FromImage(screenshot))
                using (var visibleRegion = spinner.Region!.Clone())
                {
                    Point origin = form.PointToScreen(spinner.Location);
                    origin.Offset(-form.Left, -form.Top);
                    visibleRegion.Translate(origin.X, origin.Y);
                    graphics.SetClip(visibleRegion, System.Drawing.Drawing2D.CombineMode.Replace);
                    graphics.DrawImageUnscaled(second, origin);
                }
                screenshot.Save(Path.Combine(TestWorkspace.Artifacts, "package-loading-" + mode.ToLowerInvariant() + ".png"));
                form.ClientSize = new Size(1050, 650); Application.DoEvents();
                Assert(Math.Abs(spinner.Left + spinner.Width / 2 - form.ClientSize.Width / 2) <= 1 &&
                    Math.Abs(spinner.Top + spinner.Height / 2 - form.ClientSize.Height / 2) <= 1, "resizing keeps the loader centered");
                SetField(form, "loadingPackages", false); Call(form, "UpdateButtons");
                Assert(!spinner.Active && !spinner.Visible && form.Controls[0] == content, "finishing loading removes the overlay and restores the content order");
            }
            UiTheme.SetMode("System");
            Field<TextBox>(form, "softwarePath").Text = root;
            File.WriteAllText(Path.Combine(root, "nt.payment_2.0.0.zip"), "Synthetic package, never installed");
            SynchronizationContext.SetSynchronizationContext(context);
            Complete((Task)Call(form, "LoadSoftwarePackagesAsync")!);
            Assert(!spinner.Visible && !spinner.Active && status.Text.Contains("1 Komponenten gefunden"), "real folder loading keeps the existing result hint and hides the spinner");
            Field<TextBox>(form, "softwarePath").Text = Path.Combine(root, "missing");
            SynchronizationContext.SetSynchronizationContext(context);
            Complete((Task)Call(form, "LoadSoftwarePackagesAsync")!);
            Assert(!spinner.Visible && !spinner.Active && status.Text.StartsWith("Pakete konnten nicht gelesen werden"), "a discovery failure preserves its existing error hint and also hides the spinner");
        }
        finally
        {
            UiTheme.SetMode("System");
            SynchronizationContext.SetSynchronizationContext(previousContext);
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(TestWorkspace.Artifacts) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Loading fixture outside artifacts");
            Directory.Delete(root, recursive: true);
        }
    }
}
