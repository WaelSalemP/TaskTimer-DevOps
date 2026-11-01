using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace TaskTimer.Services;

/// <summary>
/// Signs the user in to Dynamics 365 with their user name and password. Microsoft refuses a password
/// alone for accounts with multi-factor authentication; those continue in the browser. The password
/// is used once and never kept: tokens stay in a cache encrypted for the current Windows user and
/// are renewed silently until Microsoft asks for a new sign-in.
/// </summary>
public static class CrmAuth
{
    // Microsoft's public client ID for Dataverse applications, so no app registration is needed.
    private const string ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
    private const string CacheFile = "crm.msal.cache";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IPublicClientApplication? _app;

    private static async Task<IPublicClientApplication> GetAppAsync()
    {
        if (_app != null) return _app;
        var app = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, "organizations")
            .WithRedirectUri("http://localhost")
            .Build();

        var folder = Path.GetDirectoryName(SettingsStore.PathFor(CacheFile))!;
        Directory.CreateDirectory(folder);
        var storage = new StorageCreationPropertiesBuilder(CacheFile, folder).Build(); // DPAPI-protected on Windows
        (await MsalCacheHelper.CreateAsync(storage)).RegisterCache(app.UserTokenCache);
        return _app = app;
    }

    /// <summary>A token from the cache; throws <see cref="SignInRequiredException"/> when a new sign-in is needed.</summary>
    public static Task<string> GetTokenAsync(string crmUrl, string userName, CancellationToken ct = default) =>
        AcquireAsync(crmUrl, userName, null, false, ct);

    /// <param name="password">The user's password; null goes straight to the browser.</param>
    public static Task<string> SignInAsync(string crmUrl, string userName, string? password, CancellationToken ct = default) =>
        AcquireAsync(crmUrl, userName, password, true, ct);

    private static async Task<string> AcquireAsync(string crmUrl, string userName, string? password, bool signIn, CancellationToken ct)
    {
        var scopes = new[] { new Uri(crmUrl).GetLeftPart(UriPartial.Authority) + "/user_impersonation" };
        await Gate.WaitAsync(ct);
        try
        {
            var app = await GetAppAsync();
            var account = (await app.GetAccountsAsync())
                .FirstOrDefault(a => string.Equals(a.Username, userName, StringComparison.OrdinalIgnoreCase));
            if (account != null && password == null)
            {
                try
                {
                    return (await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct)).AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // Expired or new consent needed: a new sign-in follows.
                }
            }
            if (!signIn) throw new SignInRequiredException();

            if (password != null)
            {
                try
                {
#pragma warning disable CS0618 // The user asked to sign in with a password; the browser remains the fallback.
                    return (await app.AcquireTokenByUsernamePassword(scopes, userName, password).ExecuteAsync(ct)).AccessToken;
#pragma warning restore CS0618
                }
                catch (MsalUiRequiredException)
                {
                    // Multi-factor authentication or consent: continue in the browser.
                }
                catch (MsalServiceException ex) when (ex.ErrorCode == "invalid_grant")
                {
                    throw new InvalidOperationException(Loc.T("CrmBadPassword"), ex);
                }
            }
            return (await app.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false).WithLoginHint(userName).ExecuteAsync(ct)).AccessToken;
        }
        catch (MsalException ex) when (ex is not MsalUiRequiredException)
        {
            throw new InvalidOperationException("Dynamics 365 sign-in failed: " + ex.Message, ex);
        }
        finally
        {
            Gate.Release();
        }
    }
}
