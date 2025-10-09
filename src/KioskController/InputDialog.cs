using System.Drawing;
using System.Windows.Forms;

namespace KioskController;

/// <summary>
/// Simple input dialog for getting text input from user
/// </summary>
public class InputDialog : Form
{
    private TextBox _textBox;
    private Button _okButton;
    private Button _cancelButton;

    public string InputValue => _textBox.Text;

    public InputDialog(string prompt, string defaultValue = "")
    {
        this.Text = "Input";
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.Size = new Size(400, 150);

        var label = new Label
        {
            Text = prompt,
            Location = new Point(10, 10),
            Size = new Size(370, 20)
        };

        _textBox = new TextBox
        {
            Text = defaultValue,
            Location = new Point(10, 35),
            Size = new Size(370, 23)
        };

        _okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(210, 70),
            Size = new Size(80, 30)
        };

        _cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(300, 70),
            Size = new Size(80, 30)
        };

        this.Controls.Add(label);
        this.Controls.Add(_textBox);
        this.Controls.Add(_okButton);
        this.Controls.Add(_cancelButton);

        this.AcceptButton = _okButton;
        this.CancelButton = _cancelButton;
    }
}

