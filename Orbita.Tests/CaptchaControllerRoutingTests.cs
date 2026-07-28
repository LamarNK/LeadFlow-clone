using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Controllers;

namespace Orbita.Tests;

public sealed class CaptchaControllerRoutingTests
{
    [Fact]
    public void Cancel_uses_the_url_called_by_the_captcha_modal()
    {
        var action = typeof(CaptchaController).GetMethod(nameof(CaptchaController.Cancel));
        var route = action?.GetCustomAttribute<HttpPostAttribute>();

        Assert.NotNull(route);
        Assert.Equal("/Captcha/Cancel/{id:guid}", route.Template);
    }
}
