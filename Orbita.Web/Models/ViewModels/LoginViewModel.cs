using System.ComponentModel.DataAnnotations;

namespace Orbita.Web.Models.ViewModels;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "Введите email")]
    [EmailAddress(ErrorMessage = "Некорректный email")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Введите пароль")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }

    public string? ErrorMessage { get; set; }

    public bool DesignPreviewEnabled { get; set; }
}