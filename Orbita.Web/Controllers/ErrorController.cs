using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Controllers;

[AllowAnonymous]
[Route("error")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ErrorController : Controller
{
    [HttpGet("{statusCode:int}")]
    public IActionResult Status(int statusCode)
    {
        if (statusCode is < 400 or > 599)
        {
            statusCode = StatusCodes.Status500InternalServerError;
        }

        Response.StatusCode = statusCode;
        return View(ErrorPageViewModel.ForStatusCode(statusCode));
    }
}
