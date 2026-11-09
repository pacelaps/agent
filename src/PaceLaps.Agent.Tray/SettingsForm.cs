using System.Globalization;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.Tray;

/// <summary>
/// Edits config.json, the same file the console reads. The fields are checked by the file's own parser; a file with
/// errors is shown with them and can be fixed here. Changes apply at the next Activate.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly ConfigStore _config;
    private readonly IAutoStart _autoStart;
    private readonly TextBox _server = new() { Width = 340 };
    private readonly TextBox _league = new() { Width = 120 };
    private readonly CheckBox _macros = new() { Text = "Use chat macros", AutoSize = true };
    private readonly TextBox[] _slots = Enumerable.Range(1, SettingsModel.Slots).Select(_ => new TextBox { Width = 300 }).ToArray();
    private readonly CheckBox _failsafe = new() { Text = "Run the local failsafe when the server is gone", AutoSize = true };
    private readonly NumericUpDown _delay = new()
    {
        Minimum = LocalFailsafeSettings.MinDelaySeconds, Maximum = LocalFailsafeSettings.MaxDelaySeconds, Width = 70,
    };
    private readonly TextBox _appIni = new() { Width = 340 };
    private readonly CheckBox _withoutFocus = new() { Text = "Type without bringing iRacing to the front", AutoSize = true };
    private readonly CheckBox _startWithWindows = new() { Text = "Start with Windows (sleeping)", AutoSize = true };
    private readonly CheckBox _checkForUpdates = new() { Text = "Look for a new version once a day", AutoSize = true };
    private readonly Label _errors = new() { AutoSize = true, ForeColor = Color.DarkRed, MaximumSize = new Size(560, 0) };

    private readonly Uri? _fixedServer;

    /// <param name="fixedServer">A release build's server: shown, not editable.</param>
    public SettingsForm(ConfigStore config, IAutoStart autoStart, bool agentAwake, Uri? fixedServer = null)
    {
        _fixedServer = fixedServer;
        _config = config;
        _autoStart = autoStart;
        Text = $"{OperatorWords.ProductName}: settings";
        Icon = TrayIcons.Program;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var (current, errors) = config.TryLoad();
        Fill(SettingsModel.From(current, autoStart.Enabled));
        _errors.Text = errors.Count > 0 ? $"{config.Path} has errors (showing defaults):\n" + string.Join("\n", errors) : "";

        var browse = new Button { Text = "&Browse...", AutoSize = true };
        browse.Click += (_, _) => BrowseAppIni();
        var save = new Button { Text = "&Save", AutoSize = true };
        save.Click += (_, _) => Save();
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = save;
        CancelButton = cancel;

        var slots = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Height = 220, AutoScroll = true, Dock = DockStyle.Fill };
        for (var i = 0; i < _slots.Length; i++)
        {
            slots.Controls.Add(new Label { Text = $"Slot {i + 1}", AutoSize = true, Anchor = AnchorStyles.Left });
            slots.Controls.Add(_slots[i]);
        }

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        Row(grid, "Server", _server);
        Row(grid, "League id", Inline(_league, Hint("only for hosted sessions, which have no league id")));
        Row(grid, "Macros", _macros);
        Row(grid, "", slots);
        Row(grid, "Local failsafe", _failsafe);
        Row(grid, "", Inline(new Label { Text = "after", AutoSize = true, Anchor = AnchorStyles.Left }, _delay, Hint("seconds without the server")));
        Row(grid, "app.ini", Inline(_appIni, browse));
        Row(grid, "", Hint("empty: the default location in Documents\\iRacing"));
        Row(grid, "Typed commands", _withoutFocus);
        Row(grid, "", Hint("off: iRacing comes to the front for every command"));
        Row(grid, "Windows", _startWithWindows);
        Row(grid, "Updates", _checkForUpdates);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, save]);
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(grid);
        if (agentAwake)
        {
            layout.Controls.Add(Hint("Changes apply at the next Activate."));
        }

        layout.Controls.Add(_errors);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    private void Fill(SettingsModel model)
    {
        _server.Text = _fixedServer?.ToString() ?? model.Server;
        _server.ReadOnly = _fixedServer is not null;
        _league.Text = model.League;
        _macros.Checked = model.MacrosEnabled;
        for (var i = 0; i < _slots.Length; i++)
        {
            _slots[i].Text = model.MacroSlots[i];
        }

        _failsafe.Checked = model.LocalFailsafeEnabled;
        _delay.Value = decimal.TryParse(model.LocalFailsafeDelay, NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            ? Math.Clamp(d, _delay.Minimum, _delay.Maximum) : LocalFailsafeSettings.DefaultDelaySeconds;
        _appIni.Text = model.AppIni;
        _withoutFocus.Checked = model.TypedWithoutFocus;
        _startWithWindows.Checked = model.StartWithWindows;
        _checkForUpdates.Checked = model.CheckForUpdates;
    }

    private SettingsModel Read()
    {
        var model = new SettingsModel
        {
            Server = _fixedServer?.ToString() ?? _server.Text,
            League = _league.Text,
            MacrosEnabled = _macros.Checked,
            LocalFailsafeEnabled = _failsafe.Checked,
            LocalFailsafeDelay = ((int)_delay.Value).ToString(CultureInfo.InvariantCulture),
            AppIni = _appIni.Text,
            TypedWithoutFocus = _withoutFocus.Checked,
            StartWithWindows = _startWithWindows.Checked,
            CheckForUpdates = _checkForUpdates.Checked,
        };
        for (var i = 0; i < _slots.Length; i++)
        {
            model.MacroSlots[i] = _slots[i].Text;
        }

        return model;
    }

    private void Save()
    {
        var model = Read();
        var (config, errors) = model.ToConfig();
        if (config is null)
        {
            _errors.Text = "Not saved:\n" + string.Join("\n", errors);
            return;
        }

        try
        {
            _config.Save(config);
            if (_autoStart.Enabled != model.StartWithWindows)
            {
                _autoStart.Set(model.StartWithWindows);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _errors.Text = $"Not saved: {e.Message}";
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void BrowseAppIni()
    {
        using var dialog = new OpenFileDialog { Filter = "app.ini|app.ini|INI files (*.ini)|*.ini", Title = "iRacing's app.ini", CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _appIni.Text = dialog.FileName;
        }
    }

    private static void Row(TableLayoutPanel grid, string label, Control control)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 6, 12, 3) });
        grid.Controls.Add(control);
    }

    private static FlowLayoutPanel Inline(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left };
}
