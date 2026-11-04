using PaceLaps.Agent.Core.SelfTest;

namespace PaceLaps.Agent.Tray;

/// <summary>
/// The self-test's questions to the operator (the !help reply, the key-binding rule) in a dialog on top of every
/// window, on the UI thread whatever thread asks.
/// </summary>
internal sealed class DialogPrompt(SynchronizationContext ui) : IOperatorPrompt
{
    public Task<bool> ConfirmAsync(string question, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            using var form = new ConfirmForm(question);
            using var registration = ct.Register(() => form.BeginInvoke(form.Close));
            answer.TrySetResult(form.ShowDialog() == DialogResult.Yes);
        }, null);
        return answer.Task;
    }

    private sealed class ConfirmForm : Form
    {
        public ConfirmForm(string question)
        {
            Text = "PaceLaps Race Control: self-test";
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            var yes = new Button { Text = "&Yes", DialogResult = DialogResult.Yes, AutoSize = true };
            var no = new Button { Text = "&No", DialogResult = DialogResult.No, AutoSize = true };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            buttons.Controls.AddRange([no, yes]);
            var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            layout.Controls.Add(new Label { Text = question, AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 12) });
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            AcceptButton = yes;
            CancelButton = no;
        }
    }
}
