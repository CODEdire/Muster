namespace Muster.AppHost.Options;

/// <summary>
/// Custom domain for the web Container App. Bound from <c>WebCustomDomainOptions</c> (appsettings / user-secrets /
/// App Configuration). An empty/unset <see cref="Domain"/> disables custom-domain wiring entirely — the app keeps
/// its default <c>*.azurecontainerapps.io</c> hostname + Azure-managed cert.
///
/// <para>The hostname lives here because it's known up front. The managed-<b>certificate name</b> does NOT — it's
/// a <b>prompted Aspire parameter</b> (<c>webCustomDomainCertificateName</c>), because Azure issues the cert only
/// after the domain + CNAME validate, which can't happen until the hostname is already live on the app. So binding
/// is two-phase (see <c>docs/deployment.md</c> "Custom domain + SSL for the web"):</para>
/// <list type="number">
///   <item>Set <see cref="Domain"/>, deploy, leave the cert-name prompt blank → hostname binds unbound (no TLS).</item>
///   <item>Issue the managed cert, deploy again, enter its name at the prompt → TLS attaches. (CI supplies it via
///   the config key <c>Parameters:webCustomDomainCertificateName</c> instead of the prompt.)</item>
/// </list>
/// </summary>
public record WebCustomDomainOptions
{
    /// <summary>FQDN to bind, e.g. <c>app.musterbot.com</c>. Null/empty = no custom domain.</summary>
    public string? Domain { get; init; }
}
