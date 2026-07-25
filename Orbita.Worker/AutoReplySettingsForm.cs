using LeadFlow.Core.Models;

namespace Orbita.Worker;

internal sealed class AutoReplySettingsForm : Form
{
    private readonly CheckBox _enabledCheckBox;
    private readonly TextBox _messageBox;
    private readonly Label _statusLabel;

    private AutoReplySettingsForm(AvitoMessengerAutoReplySettings settings)
    {
        Text = "Автоответы в чатах";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 305);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        var title = new Label
        {
            Text = "Автоответы Avito в чатах",
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 24, 40),
            AutoSize = true,
            Location = new Point(24, 20)
        };
        var hint = new Label
        {
            Text = "Ответ отправляется, если кандидат написал в чат, а менеджер ещё не ответил.",
            ForeColor = Color.FromArgb(102, 112, 133),
            AutoSize = false,
            Size = new Size(472, 34),
            Location = new Point(24, 51)
        };
        _enabledCheckBox = new CheckBox
        {
            Text = "Включить автоответы",
            AutoSize = true,
            Location = new Point(24, 94),
            Checked = settings.Enabled
        };
        var messageLabel = new Label
        {
            Text = "Текст ответа",
            AutoSize = true,
            Location = new Point(24, 124)
        };
        _messageBox = new TextBox
        {
            Location = new Point(24, 145),
            Size = new Size(472, 88),
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
            MaxLength = 2000,
            Text = settings.Message
        };
        _statusLabel = new Label
        {
            ForeColor = Color.FromArgb(180, 35, 24),
            AutoSize = false,
            Size = new Size(472, 20),
            Location = new Point(24, 240)
        };
        var cancelButton = new Button
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Location = new Point(320, 267),
            Size = new Size(84, 32)
        };
        var saveButton = new Button
        {
            Text = "Сохранить",
            Location = new Point(412, 267),
            Size = new Size(84, 32)
        };
        saveButton.Click += OnSaveClick;

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.AddRange([title, hint, _enabledCheckBox, messageLabel, _messageBox, _statusLabel, cancelButton, saveButton]);
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        if (_enabledCheckBox.Checked && string.IsNullOrWhiteSpace(_messageBox.Text))
        {
            _statusLabel.Text = "Укажите текст автоответа или выключите автоответы.";
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    public static bool TryConfigure(
        WorkerAppSettingsStore store,
        AppSettings appSettings)
    {
        var settings = appSettings.Avito.MessengerAutoReply;
        using var form = new AutoReplySettingsForm(settings);
        if (form.ShowDialog() != DialogResult.OK)
        {
            return false;
        }

        settings.Enabled = form._enabledCheckBox.Checked;
        settings.Message = form._messageBox.Text.Trim();
        store.Save(appSettings);
        return true;
    }
}