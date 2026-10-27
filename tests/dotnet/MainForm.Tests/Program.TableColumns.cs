using NovaTouchUpdater;

internal static partial class Program
{
    private static void ColumnSizingTests()
    {
        using var form = new MainForm(preview: true, new FakeRunner(), new FakeRunner());
        HostState[] hosts = Populate(form);
        DataGridViewRow firstRow = hosts[0].Row!;
        var table = Field<DataGridView>(form, "table");
        Control parent = table.Parent!;
        using var canvas = new Panel { Size = new Size(1670, 600), Font = form.Font };
        var errors = new List<string>();
        table.DataError += (_, e) => { errors.Add(e.Exception?.Message ?? "DataError"); e.ThrowException = false; };
        canvas.Controls.Add(table);
        canvas.CreateControl();
        _ = table.Handle;
        try
        {
            void Settle()
            {
                canvas.PerformLayout();
                Application.DoEvents();
                Application.DoEvents();
            }
            Dictionary<string, int> Widths() => table.Columns.Cast<DataGridViewColumn>().ToDictionary(c => c.Name, c => c.Width);
            bool StatusVisible()
            {
                Rectangle status = table.GetCellDisplayRectangle(table.Columns["result"].Index, 0, false);
                int right = table.ClientSize.Width - table.Controls.OfType<VScrollBar>().Where(s => s.Visible).Sum(s => s.Width);
                return status.Width > 0 && Math.Abs(status.Right - right) <= 1;
            }

            Settle();
            Call(form, "ApplyColumnWidths");
            Settle();
            var baseline = Widths();
            Assert(table.Columns.Cast<DataGridViewColumn>().Where(c => c.Name != "include")
                .All(c => c.Width >= c.GetPreferredWidth(DataGridViewAutoSizeColumnMode.ColumnHeader, true)),
                "every heading fits with space around it at the normal table width");

            firstRow.Cells["hostname"].Value = "PC-A-VERKAUF-12345";
            Settle();
            var longerHost = Widths();
            Assert(longerHost["hostname"] > baseline["hostname"] &&
                longerHost.Where(c => c.Key is not ("hostname" or "information")).All(c => c.Value == baseline[c.Key]) &&
                longerHost["information"] == baseline["information"] - (longerHost["hostname"] - baseline["hostname"]),
                "changing a hostname automatically grows only its column and gives Information the remaining width");
            int hostText = TextRenderer.MeasureText("PC-A-VERKAUF-12345", table.Rows[0].Cells["hostname"].InheritedStyle.Font).Width;
            Assert(longerHost["hostname"] > hostText && longerHost["hostname"] - hostText < 30,
                "a content-sized hostname column leaves a small gap instead of a percentage of the window");

            firstRow.Cells["payment"].Value = "2026.123.10234.12345";
            Settle();
            Assert(table.Columns["payment"].Width > baseline["payment"], "a longer version automatically expands its software column");
            var withVersion = Widths();
            firstRow.Cells["information"].Value = new string('W', 1000);
            firstRow.Cells["information"].Tag = 75;
            Settle();
            Assert(Widths().All(c => c.Value == withVersion[c.Key]), "long Information text and copy progress do not influence column widths");
            firstRow.Cells["information"].Value = "Datei wird kopiert (75 %)";

            canvas.Width += 240;
            Settle();
            Assert(table.Columns["information"].Width == withVersion["information"] + 240 &&
                Widths().Where(c => c.Key != "information").All(c => c.Value == withVersion[c.Key]) && StatusVisible(),
                "making the window wider gives all extra space to Information and keeps Status at the right edge");
            canvas.Width -= 240;
            Call(form, "PaintRow", hosts[0]);
            Settle();
            Assert(Widths().All(c => c.Value == baseline[c.Key]), "shorter replacement values shrink columns back to their header or content width");

            for (int i = 0; i < 50; i++) table.Rows.Add(true, "PC-" + i, "Online", "✓", "1.2.3", "", "", "", "", "", "Bereit");
            int last = table.Rows.Add(true, "PC-VERKAUF-GANZ-UNTEN-12345", "Online", "✓", "1.2.3", "", "", "", "", "", "Bereit");
            Settle();
            Assert(!table.Rows[last].Displayed && table.Columns["hostname"].Width > baseline["hostname"] && StatusVisible(),
                "values in rows outside the visible area also determine width, even with a vertical scrollbar");
            while (table.Rows.Count > hosts.Length) table.Rows.RemoveAt(table.Rows.Count - 1);
            Settle();
            Assert(Widths().All(c => c.Value == baseline[c.Key]), "removing the longest row recalculates widths and returns freed space to Information");

            firstRow.Cells["hostname"].Value = "PC-" + new string('W', 150);
            firstRow.Cells["result"].Value = "Ein ungewöhnlich langer Status für einen einzelnen Rechner";
            firstRow.Cells["serversuite"].Value = "2026.4.9792 / Instanz KASSE\n2026.4.10234 / Instanz BACKOFFICE";
            firstRow.Cells["information"].Value = "Installationspaket wird kopiert (75 %)";
            firstRow.Cells["information"].Tag = 75;
            Settle();
            Assert(StatusVisible() && table.HorizontalScrollingOffset == 0 &&
                table.Columns.Cast<DataGridViewColumn>().Where(c => c.Name != "include")
                    .All(c => c.Width >= c.GetPreferredWidth(DataGridViewAutoSizeColumnMode.ColumnHeader, true)),
                "exceptionally long content shares the available space while headings and Status remain visible");
            Assert(errors.Count == 0, "dynamic sizing raises no DataGridView formatting errors: " + string.Join(", ", errors));

            firstRow.Cells["hostname"].Value = "PC01-RST-POS-01";
            firstRow.Cells["result"].Value = "Aktualisierung läuft";
            Settle();
            using var bitmap = new Bitmap(canvas.Width, canvas.Height);
            canvas.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(TestWorkspace.Artifacts, "main-content-columns.png"));
        }
        finally
        {
            canvas.Controls.Remove(table);
            parent.Controls.Add(table);
        }
    }
}
