using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Controllers;
using Orbita.Web.Options;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class CrmControllerPreviewTests
{
    [Fact]
    public async Task Index_InDesignPreview_UsesDemoOfficeWhenAdminHasNoOfficeCookie()
    {
        var (controller, _) = CreateController(previewEnabled: true);

        var result = await controller.Index(
            officeId: null,
            search: null,
            scope: null,
            city: null,
            vacancy: null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Null(view.ViewName);
        Assert.IsType<CrmBoardDto>(view.Model);
    }

    [Fact]
    public async Task Index_OutsideDesignPreview_StillRequiresOfficeForAdmin()
    {
        var (controller, _) = CreateController(previewEnabled: false);

        var result = await controller.Index(
            officeId: null,
            search: null,
            scope: null,
            city: null,
            vacancy: null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Unavailable", view.ViewName);
    }

    private static (CrmController Controller, HttpClient Http) CreateController(bool previewEnabled)
    {
        var httpContext = new DefaultHttpContext
        {
            User = TestPrincipalFactory.Admin("preview-admin", "Администратор")
        };
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var officeContext = new OfficeContext();
        officeContext.Bind(httpContext);
        var session = new AuthSession(accessor);
        var previewOptions = Options.Create(new DesignPreviewOptions { Enabled = previewEnabled });
        var http = new HttpClient(new ThrowingHttpMessageHandler())
        {
            BaseAddress = new Uri("https://orbita.test/")
        };
        var api = new OrbitaApiClient(http, session, officeContext, previewOptions);
        var auth = new OrbitaAuthService(accessor, session);
        var controller = new CrmController(api, officeContext, auth, previewOptions)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        return (controller, http);
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The CRM controller test must not call the API.");
    }
}
