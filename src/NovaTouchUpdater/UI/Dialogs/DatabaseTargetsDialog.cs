namespace NovaTouchUpdater;

internal sealed class DatabaseTargetsDialog : Form
{
    private sealed record NamespaceChoice(int ServerIndex, int InstanceIndex, string Namespace);
    private readonly ServicePackHostPlan[] plans;
    private readonly TreeView instances = new() { Name = "databaseTargets", Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false, MinimumSize = new Size(0, 180) };
    private readonly Label status = new() { Name = "selectedTargetCount", AutoSize = true, Dock = DockStyle.Fill };
    private readonly TextBox details = new() { Name = "targetDetails", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, MinimumSize = new Size(0, 100) };
    private readonly Button apply = new ModernButton { Name = "applySelectedNamespaces", AutoSize = true, Padding = new Padding(12, 7, 12, 7), DialogResult = DialogResult.OK, Role = ButtonRole.Primary };
    private bool changingChecks;

    internal static DatabaseTargetsDialog ForSoftwareCompilation(ServicePackHostPlan[] plans) => new(plans,
        "Softwarepakete kompilieren – Namespaces auswählen", "Kompilieren",
        "Namespaces für die Kompilierung auswählen. Ein Häkchen am Server oder an der Instanz wählt die zugehörigen Namespaces.\n" +
        "Quelle: installierte NovaTouch Control. Vorab: Aprima, Systemparameter, Novacom-System und BLS.\n" +
        "Danach: Daten → BOH → POS → POS-API → QRY. Auch aktuelle Klassenpakete werden erneut kompiliert.\n" +
        "Globale Stammdaten werden wie beim NTC versionsabhängig oder einmalig eingespielt. BLS wird nur eingespielt.\n" +
        "Namespaces mit Präfix % werden ausgeblendet. Fehler werden zum Abschluss aufgelistet.");

    internal static DatabaseTargetsDialog ForServicePacks(ServicePackHostPlan[] plans, int fileCount) => new(plans,
        "Servicepacks einspielen – Namespaces auswählen", "Einspielen",
        $"{fileCount} XML-Servicepack(s) in die ausgewählten Namespaces einspielen.\n" +
        "Ein Häkchen am Server oder an der Instanz wählt die zugehörigen Namespaces.\n" +
        "Klassen und Routinen werden mit fck kompiliert; Globals werden importiert.\n" +
        "Namespaces mit Präfix % werden ausgeblendet. Bei Import-/Compilerfehlern können Teilimporte vorhanden sein.");

    private DatabaseTargetsDialog(ServicePackHostPlan[] plans, string title, string actionText, string instructions)
    {
        this.plans = plans;
        SuspendLayout();
        Text = title;
        apply.Text = actionText;
        Font = UiTheme.BodyFont(); ClientSize = new Size(900, 680); MinimumSize = new Size(620, 480);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = MinimizeBox = false;
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 10),
            Text = instructions }, 0, 0);
        layout.Controls.Add(status, 0, 1); layout.Controls.Add(instances, 0, 2); layout.Controls.Add(details, 0, 3);
        var cancel = new ModernButton { Text = "Abbrechen", AutoSize = true, Padding = new Padding(12, 7, 12, 7), DialogResult = DialogResult.Cancel };
        var buttons = new WrappingFlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(apply); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 4);
        Controls.Add(WindowLayout.CreateScrollArea(layout, instances, details));
        CancelButton = cancel; AcceptButton = apply;
        for (int serverIndex = 0; serverIndex < plans.Length; serverIndex++)
        {
            ServicePackHostPlan plan = plans[serverIndex];
            string server = plan.Host.IsLocal ? Environment.MachineName + " (lokal)" : plan.Host.Host;
            var root = new TreeNode(server) { Tag = "Server: " + server, Checked = true };
            for (int instanceIndex = 0; instanceIndex < plan.Targets.Length; instanceIndex++)
            {
                IrisUploadTarget target = plan.Targets[instanceIndex];
                var node = new TreeNode($"{target.Instance} · Port {target.Port}") { Checked = true,
                    Tag = $"Server: {server}\r\nInstanz: {target.Instance}\r\nSuperServer-Port: {target.Port}\r\nInstallation: {target.Directory}" };
                foreach (string name in target.Namespaces.Where(IrisDatabaseInventory.IsApplicationNamespace).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                    node.Nodes.Add(new TreeNode(name) { Name = name, Checked = true, Tag = new NamespaceChoice(serverIndex, instanceIndex, name) });
                if (node.Nodes.Count > 0) root.Nodes.Add(node);
            }
            root.Checked = root.Nodes.Count > 0;
            if (root.Nodes.Count == 0) { root.ForeColor = UiTheme.Muted; root.Text += " · keine berücksichtigten Namespaces"; }
            instances.Nodes.Add(root); root.Expand();
            foreach (TreeNode node in root.Nodes) node.Expand();
        }
        instances.BeforeCheck += (_, e) => { if (e.Node is { Nodes.Count: 0, Tag: not NamespaceChoice }) e.Cancel = true; };
        instances.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Node.Tag is NamespaceChoice &&
                (instances.HitTest(e.Location).Location & TreeViewHitTestLocations.Label) != 0)
                e.Node.Checked = !e.Node.Checked;
        };
        instances.AfterCheck += (_, e) =>
        {
            if (changingChecks || e.Node is null) return;
            changingChecks = true;
            try
            {
                foreach (TreeNode child in Descendants(e.Node)) child.Checked = e.Node.Checked;
                for (TreeNode? parent = e.Node.Parent; parent is not null; parent = parent.Parent)
                    parent.Checked = parent.Nodes.Cast<TreeNode>().Any(node => node.Checked);
            }
            finally { changingChecks = false; }
            UpdateSelection();
        };
        instances.AfterSelect += (_, _) =>
        {
            if (instances.SelectedNode?.Tag is NamespaceChoice choice)
                details.Text = instances.SelectedNode.Parent!.Tag + "\r\nNamespace: " + choice.Namespace;
            else details.Text = Convert.ToString(instances.SelectedNode?.Tag);
        };
        UpdateSelection();
        if (instances.Nodes.Count > 0) instances.SelectedNode = instances.Nodes[0];
        WindowLayout.CompleteInitialization(this);
    }

    private static IEnumerable<TreeNode> Descendants(TreeNode root)
    {
        foreach (TreeNode child in root.Nodes)
        {
            yield return child;
            foreach (TreeNode descendant in Descendants(child)) yield return descendant;
        }
    }

    private void UpdateSelection()
    {
        TreeNode[] leaves = instances.Nodes.Cast<TreeNode>().SelectMany(Descendants).Where(node => node.Tag is NamespaceChoice).ToArray();
        int selected = leaves.Count(node => node.Checked);
        status.Text = $"{selected} von {leaves.Length} Namespace(s) ausgewählt.";
        apply.Enabled = selected > 0;
    }

    internal ServicePackHostPlan[] GetSelectedPlans()
    {
        var chosen = instances.Nodes.Cast<TreeNode>().SelectMany(Descendants).Where(node => node.Checked && node.Tag is NamespaceChoice)
            .Select(node => (NamespaceChoice)node.Tag!).ToHashSet();
        var result = new List<ServicePackHostPlan>();
        for (int serverIndex = 0; serverIndex < plans.Length; serverIndex++)
        {
            var targets = new List<IrisUploadTarget>();
            for (int instanceIndex = 0; instanceIndex < plans[serverIndex].Targets.Length; instanceIndex++)
            {
                IrisUploadTarget target = plans[serverIndex].Targets[instanceIndex];
                string[] names = target.Namespaces.Where(name => chosen.Contains(new NamespaceChoice(serverIndex, instanceIndex, name))).ToArray();
                if (names.Length > 0) targets.Add(new IrisUploadTarget { Instance = target.Instance, Directory = target.Directory, Port = target.Port,
                    Namespaces = names, User = target.User, Password = target.Password, Ssl = target.Ssl });
            }
            if (targets.Count > 0) result.Add(new(plans[serverIndex].Host, targets.ToArray()));
        }
        return result.ToArray();
    }
}
