using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace PartnerBank.Security;

/// <summary>Where a Partner Bank deployable keeps its ASP.NET Core Data Protection keys (cookies, sign-in state, signing keys at rest).</summary>
public static class DataProtectionKeys
{
    // ┌─ :: ARCHITECTURE + SECURITY :: Data Protection keys that survive a restart ────────────────
    // │ Why : The keys encrypt the session cookies and, at the authorization server, the signing
    // │       keys on disk. In a container the default place (the user's home folder) is
    // │       read-only or lost with the container: every restart would end every session and
    // │       make the stored signing keys unreadable.
    // │ How : With a folder configured (the image sets /data/data-protection-keys, a mounted
    // │       volume), the keys are kept there under one application name per deployable. Not
    // │       configured (local runs): the framework's default, unchanged.
    // │ Ref : doc/Containers.md | OWASP A02 Cryptographic Failures
    // └────────────────────────────────────────────────────────────────────────────────────────────
    public static void AddPartnerDataProtection(this IServiceCollection services, string? keysPath, string applicationName)
    {
        if (string.IsNullOrWhiteSpace(keysPath))
            return;

        // DEBT: the key files are not encrypted at rest (no key-encryption certificate yet); the
        //       volume's access rights protect them. A production partner wraps them with a key
        //       from its key vault (ProtectKeysWith...).
        services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(Environment.ExpandEnvironmentVariables(keysPath)));
    }
}