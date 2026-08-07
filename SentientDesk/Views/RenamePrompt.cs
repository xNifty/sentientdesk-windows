using System.Drawing;
using System.Windows.Forms;
using DrawingFont = System.Drawing.Font;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace SentientDesk.Views;

internal static class RenamePrompt
{
    public static string? Ask(string title, string prompt, string initialValue)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new DrawingSize(360, 120),
            Font = new DrawingFont("Segoe UI", 9f)
        };

        var label = new Label
        {
            Text = prompt,
            AutoSize = false,
            Location = new DrawingPoint(12, 12),
            Size = new DrawingSize(336, 24)
        };

        var box = new TextBox
        {
            Text = initialValue,
            Location = new DrawingPoint(12, 40),
            Size = new DrawingSize(336, 23)
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new DrawingPoint(192, 78),
            Size = new DrawingSize(75, 28)
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new DrawingPoint(273, 78),
            Size = new DrawingSize(75, 28)
        };

        form.Controls.Add(label);
        form.Controls.Add(box);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        box.SelectAll();

        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
