using NovaTouchUpdater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            foreach (float scale in new[] { 1F, 1.5F, 2F }) Render(scale, args.FirstOrDefault());
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Render(float scale, string? outputDirectory)
    {
        // Invisible fixture only. No main application, timers or workers run.
        using var font = new Font("Segoe UI", 9F * scale);
        using var grid = new DataGridView
        {
            Font=font, AllowUserToAddRows=false, RowHeadersVisible=false,
            ReadOnly=true, SelectionMode=DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.None,
            BackgroundColor=Color.White, ScrollBars=ScrollBars.None,
            DefaultCellStyle=new DataGridViewCellStyle { Font=font, Padding=new Padding(0, 4, 0, 4) }
        };
        grid.Columns.Add("host", "Hostname");
        grid.Columns[0].Width=(int)(110 * scale);
        grid.Columns.Add(new DataGridViewColumn(new InformationProgressCell())
            { Name="information", HeaderText="Information", Width=(int)(670 * scale) });
        grid.ColumnHeadersHeight=(int)(30 * scale);
        int rowHeight=(int)(38 * scale);
        string[] texts = [
            "0 % · Update installieren – Server Suite: 0/120 Dateien kopiert",
            "25 % · Update installieren – Server Suite: 30/120 Dateien kopiert",
            "50 % · Dienste stoppen – Dienst 'nt3_server_suite_HKS' ist gestoppt",
            "75 % · Update installieren – Server Suite / Python: 90/120 Dateien kopiert",
            "100 % · Dienste starten – Dienst 'nt3_server_suite_HKS' läuft",
            "Update fehlgeschlagen – Zugriff verweigert"
        ];
        int?[] percentages = [0, 25, 50, 75, 100, null];
        for (int i=0; i<texts.Length; i++)
        {
            int row=grid.Rows.Add($"KASSE-{i+1:00}", texts[i]);
            grid.Rows[row].Height=rowHeight;
            grid.Rows[row].Cells[1].Tag=percentages[i];
        }
        grid.Rows[5].DefaultCellStyle.BackColor=Color.FromArgb(255, 229, 230);
        grid.Size=new Size(grid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible)+3,
            grid.ColumnHeadersHeight + rowHeight * texts.Length + 3);
        _=grid.Handle;
        grid.ClearSelection();
        grid.Rows[2].Selected=true;
        using var bitmap = new Bitmap(grid.Width, grid.Height);
        grid.DrawToBitmap(bitmap, new Rectangle(Point.Empty, grid.Size));
        for (int row=0; row<percentages.Length; row++)
        {
            Rectangle bounds=grid.GetCellDisplayRectangle(1, row, false);
            int y=bounds.Bottom-5;
            Assert(y >= 0 && y < bitmap.Height, $"Row {row} lies outside the render surface: {bounds}; bitmap={bitmap.Size}");
            bool selected=row==2;
            Color track=selected ? Color.FromArgb(227,243,233) : Color.FromArgb(242,250,244);
            Color fill=selected ? Color.FromArgb(139,207,157) : Color.FromArgb(163,218,176);
            if (percentages[row] is int percent)
            {
                for (int portion=5; portion<100; portion+=10)
                {
                    if (portion==percent) continue; // Border rounding at the exact fill edge.
                    Color expected=portion<percent ? fill : track;
                    Color actual=bitmap.GetPixel(bounds.Left + bounds.Width * portion/100, y);
                    Assert(actual.ToArgb()==expected.ToArgb(), $"{scale:P0}, {percent}%: incorrect bar width or selection color at {portion}% ({actual})");
                }
                int darkPixels=0;
                for (int py=bounds.Top+4; py<bounds.Bottom-4; py++)
                    for (int px=bounds.Left+5; px<bounds.Left+bounds.Width/2; px++)
                    {
                        Color pixel=bitmap.GetPixel(px,py);
                        if (pixel.R<90 && pixel.G<110 && pixel.B<90) darkPixels++;
                    }
                Assert(darkPixels>50, $"{scale:P0}, {percent}%: readable dark status text must remain above the bar");
            }
            else Assert(bitmap.GetPixel(bounds.Left+10,y).ToArgb()==Color.FromArgb(255,229,230).ToArgb(),
                "An error cell without progress must retain its ordinary error background");
        }
        if (outputDirectory is not null)
        {
            string target=Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(target);
            bitmap.Save(Path.Combine(target, $"information-progress-{scale*100:0}.png"));
        }
        Console.WriteLine($"PASS progress widths, dark text, selected row and error background at {scale:P0}");
    }
}
