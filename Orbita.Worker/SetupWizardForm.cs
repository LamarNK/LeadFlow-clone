namespace Orbita.Worker;

internal sealed class SetupWizardForm : Form
{
    private readonly TextBox _apiKeyBox;
    private readonly Label _statusLabel;
    private readonly Button _saveButton;

    public WorkerCredentials? Result { get; private set; }

    public SetupWizardForm(WorkerCredentials? existing = null)
    {
        Text = "Настройка воркера Орбиты";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 220);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "OrbitaWorker.ico");
        if (File.Exists(iconPath))
        {
            Icon = new Icon(iconPath);
        }

        var title = new Label
        {
            Text = "Подключение к панели Орбита",
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 24, 40),
            AutoSize = true,
            Location = new Point(24, 20)
        };

        var hint = new Label
        {
            Text = "Вставьте API-ключ воркера из окна «Добавить воркер» в панели Орбита.",
            ForeColor = Color.FromArgb(102, 112, 133),
            AutoSize = false,
            Size = new Size(432, 36),
            Location = new Point(24, 52)
        };

        var apiKeyLabel = new Label
        {
            Text = "API-ключ воркера",
            AutoSize = true,
            Location = new Point(24, 98)
        };

        _apiKeyBox = new TextBox
        {
            Location = new Point(24, 118),
            Size = new Size(432, 23),
            UseSystemPasswordChar = true,
            Text = existing?.ApiKey ?? string.Empty
        };

        _statusLabel = new Label
        {
            ForeColor = Color.FromArgb(180, 35, 24),
            AutoSize = false,
            Size = new Size(432, 20),
            Location = new Point(24, 148),
            Text = string.Empty
        };

        var cancelButton = new Button
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Location = new Point(280, 172),
            Size = new Size(84, 32)
        };

        _saveButton = new Button
        {
            Text = "Сохранить",
            Location = new Point(372, 172),
            Size = new Size(84, 32)
        };
        _saveButton.Click += OnSaveClick;

        AcceptButton = _saveButton;
        CancelButton = cancelButton;

        Controls.AddRange([title, hint, apiKeyLabel, _apiKeyBox, _statusLabel, cancelButton, _saveButton]);
    }

    private async void OnSaveClick(object? sender, EventArgs e)
    {
        var apiKey = _apiKeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _statusLabel.Text = "Укажите API-ключ воркера.";
            return;
        }

        _saveButton.Enabled = false;
        _statusLabel.ForeColor = Color.FromArgb(102, 112, 133);
        _statusLabel.Text = "Проверка подключения…";

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(WorkerSetupConstants.ApiBaseUrl.TrimEnd('/') + "/") };
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/workers/config");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            var response = await http.SendAsync(request).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                _statusLabel.ForeColor = Color.FromArgb(180, 35, 24);
                _statusLabel.Text = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Неверный API-ключ или воркер отключён."
                    : $"Не удалось подключиться ({(int)response.StatusCode}).";
                return;
            }

            Result = WorkerConfigStore.CreateCredentials(apiKey);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.FromArgb(180, 35, 24);
            _statusLabel.Text = "Ошибка сети: " + ex.Message;
        }
        finally
        {
            _saveButton.Enabled = true;
        }
    }

    public static bool TryConfigure(WorkerConfigStore store, WorkerCredentials? existing = null)
    {
        using var form = new SetupWizardForm(existing);
        if (form.ShowDialog() != DialogResult.OK || form.Result is null)
        {
            return false;
        }

        store.Save(form.Result);
        return true;
    }
}