using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAutoLoginRecoveryTests
{
    [Fact]
    public void TryParseProbe_GuestHeader_DetectsNeedsLogin()
    {
        const string json = """
            {
              "needsLogin": true,
              "isAuthorized": false,
              "hasCaptcha": false,
              "hasLoginForm": false,
              "hasUsersList": false,
              "hasGuestLoginButton": true,
              "hasLoggedInProfile": false,
              "hasPasswordValue": false,
              "hasSubmitButton": false,
              "url": "https://www.avito.ru/elniki"
            }
            """;

        var state = AvitoAutoLoginRecovery.TryParseProbe(json);

        Assert.NotNull(state);
        Assert.True(state!.NeedsLogin);
        Assert.True(state.HasGuestLoginButton);
        Assert.False(state.IsAuthorized);
    }

    [Fact]
    public void TryParseProbe_SavedPasswordForm_DetectsSubmitReady()
    {
        const string json = """
            {
              "needsLogin": true,
              "isAuthorized": false,
              "hasCaptcha": false,
              "hasLoginForm": true,
              "hasUsersList": false,
              "hasGuestLoginButton": false,
              "hasLoggedInProfile": false,
              "hasPasswordValue": true,
              "hasSubmitButton": true,
              "url": "https://www.avito.ru/#login"
            }
            """;

        var state = AvitoAutoLoginRecovery.TryParseProbe(json);

        Assert.NotNull(state);
        Assert.True(state!.HasPasswordValue);
        Assert.True(state.HasSubmitButton);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    public void IsSessionRecovered_MatchesExpected(bool isAuthorized, bool needsLogin, bool hasCaptcha, bool expected)
    {
        var state = new AvitoAutoLoginRecovery.ProbeState(
            NeedsLogin: needsLogin,
            IsAuthorized: isAuthorized,
            HasCaptcha: hasCaptcha,
            HasLoginForm: false,
            HasUsersList: false,
            HasGuestLoginButton: false,
            HasLoggedInProfile: false,
            HasPasswordValue: false,
            HasSubmitButton: false,
            Url: "https://www.avito.ru/profile/dashboard");

        Assert.Equal(expected, AvitoAutoLoginRecovery.IsSessionRecovered(state));
    }

    [Fact]
    public void IsSessionRecovered_NullState_ReturnsFalse() =>
        Assert.False(AvitoAutoLoginRecovery.IsSessionRecovered(null));

    [Fact]
    public void TryParseProbe_AuthorizedProfile_ReturnsRecoveredState()
    {
        const string json = """
            {
              "needsLogin": false,
              "isAuthorized": true,
              "hasCaptcha": false,
              "hasLoginForm": false,
              "hasUsersList": false,
              "hasGuestLoginButton": false,
              "hasLoggedInProfile": true,
              "hasPasswordValue": false,
              "hasSubmitButton": false,
              "url": "https://www.avito.ru/profile/job/responses"
            }
            """;

        var state = AvitoAutoLoginRecovery.TryParseProbe(json);

        Assert.NotNull(state);
        Assert.True(state!.IsAuthorized);
        Assert.False(state.NeedsLogin);
    }
}