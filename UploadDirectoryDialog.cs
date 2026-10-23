namespace NovaTouchUpdater;

internal sealed class UploadDirectoryDialog : Form
{
    public UploadDirectoryDialog(IReadOnlyList<(string Host, UploadDirectoryInfo Directory)> directories)
    {
        SuspendLayout();
        Text = "Zielverzeichnis nicht leer";
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.White;
        ClientSize = new Size(880, 490);
        MinimumSize = new Size(620, 380);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;

        var layout = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=3, Padding=new Padding(18) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var description = new Label
        {
            AutoSize=true, Dock=DockStyle.Fill, Margin=new Padding(0,0,0,12),
            Text="Die folgenden Uploadordner sind nicht leer. Sollen sie vor dem Upload bereinigt werden?\n\n" +
                "Bereinigen entfernt den gesamten vorhandenen Inhalt der bereinigbaren Ordner, einschließlich Unterordnern. " +
                "Die Zielordner selbst und geschützte Installationsverzeichnisse bleiben erhalten."
        };
        layout.Controls.Add(description,0,0);
        var table = new DataGridView
        {
            Name="directories", Dock=DockStyle.Fill, MinimumSize=new Size(0,130), ReadOnly=true,
            AllowUserToAddRows=false, AllowUserToDeleteRows=false, RowHeadersVisible=false,
            AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells, BackgroundColor=Color.White,
            DefaultCellStyle=new DataGridViewCellStyle { WrapMode=DataGridViewTriState.True, Padding=new Padding(2,4,2,4) }
        };
        table.Columns.Add("host","Host");
        table.Columns.Add("path","Zielverzeichnis");
        table.Columns.Add("entries","Einträge");
        table.Columns.Add("status","Bereinigung");
        table.Columns["host"].FillWeight=18;
        table.Columns["path"].FillWeight=35;
        table.Columns["entries"].FillWeight=9;
        table.Columns["status"].FillWeight=38;
        foreach (var item in directories)
        {
            int row=table.Rows.Add(item.Host,item.Directory.Path,item.Directory.EntryCount,
                item.Directory.CanClean ? "Wird bei Ja vollständig geleert" : "Geschützt – bleibt erhalten: " + item.Directory.ProtectionReason);
            table.Rows[row].Cells["path"].ToolTipText=item.Directory.Path;
            table.Rows[row].Cells["status"].ToolTipText=Convert.ToString(table.Rows[row].Cells["status"].Value);
        }
        layout.Controls.Add(table,0,1);
        Button Choice(string name, string text, DialogResult result) => new()
        {
            Name=name, Text=text, DialogResult=result, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink,
            Padding=new Padding(9,6,9,6), Margin=new Padding(5)
        };
        var clean=Choice("clean","Ja, bereinigen und hochladen",DialogResult.Yes);
        clean.Enabled=directories.Any(item => item.Directory.CanClean);
        var keep=Choice("keep","Ohne Bereinigung hochladen",DialogResult.No);
        var cancel=Choice("cancel","Abbrechen",DialogResult.Cancel);
        var buttons=new WrappingFlowLayoutPanel
        {
            Dock=DockStyle.Fill, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink,
            WrapContents=true, FlowDirection=FlowDirection.RightToLeft, Margin=new Padding(0,10,0,0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(keep);
        buttons.Controls.Add(clean);
        layout.Controls.Add(buttons,0,2);
        Controls.Add(WindowLayout.CreateScrollArea(layout,table));
        AcceptButton=keep;
        CancelButton=cancel;
        WindowLayout.CompleteInitialization(this);
    }
}
