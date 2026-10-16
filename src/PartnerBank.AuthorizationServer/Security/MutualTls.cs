namespace PartnerBank.AuthorizationServer.Security;

/// <summary>Names shared by the certificate handler and IdentityServer's mutual-TLS options.</summary>
public static class MutualTls
{
    /// <summary>The authentication scheme that validates client certificates on the /connect/mtls/* endpoints.</summary>
    public const string Scheme = "Certificate";
}