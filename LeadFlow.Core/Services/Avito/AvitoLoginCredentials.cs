namespace LeadFlow.Core.Services.Avito;

/// <summary>Учётные данные Avito для автоматического входа (из панели Орбиты).</summary>
public sealed record AvitoLoginCredentials(string Login, string Password)
{
    public static AvitoLoginCredentials? TryCreate(string? login, string? password)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        return new AvitoLoginCredentials(login.Trim(), password);
    }

    public bool IsUsable => !string.IsNullOrWhiteSpace(Login) && !string.IsNullOrEmpty(Password);
}
