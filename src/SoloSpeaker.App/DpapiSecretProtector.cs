using System.Security.Cryptography;
using SoloSpeaker.Core.Abstractions;

namespace SoloSpeaker.App;

/// <summary>
/// <see cref="ISecretProtector"/> over DPAPI at <c>CurrentUser</c> scope, per ADR 0013.
/// </summary>
/// <remarks>
/// <para>
/// The entropy is the <c>pairId</c>, which binds the ciphertext to the pairing so that a
/// <c>config.json</c> from an older pairing on the same profile fails to unprotect rather
/// than silently yielding a key for the wrong pair. <c>pairId</c> is public and transmitted
/// in cleartext, so it adds no secrecy - that is not what it is for. The protection comes
/// from the <c>CurrentUser</c> scope; the entropy is a domain separator.
/// </para>
/// <para>
/// This is the whole Windows surface of the configuration store. Everything that reasons
/// about what <c>config.json</c> means lives in <c>SoloSpeaker.Core</c> behind this seam,
/// which is why the §10 recovery cases are reachable from a <c>net10.0</c> test project.
/// </para>
/// </remarks>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <inheritdoc/>
    public byte[] Protect(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> entropy) =>
        ProtectedData.Protect(secret.ToArray(), entropy.ToArray(), DataProtectionScope.CurrentUser);

    /// <inheritdoc/>
    /// <remarks>
    /// A wrong profile and a wrong pairing both surface as the same
    /// <see cref="CryptographicException"/>, which is why testing the wrong-entropy path
    /// exercises the same failure route the cross-profile case takes - see
    /// <c>implementation-plan.md</c> §4.1, which records that the profile case itself is
    /// only reachable by hand.
    /// </remarks>
    public bool TryUnprotect(ReadOnlySpan<byte> protectedSecret, ReadOnlySpan<byte> entropy, out byte[] secret)
    {
        secret = [];

        if (protectedSecret.IsEmpty)
        {
            return false;
        }

        try
        {
            secret = ProtectedData.Unprotect(
                protectedSecret.ToArray(), entropy.ToArray(), DataProtectionScope.CurrentUser);
            return true;
        }
        catch (CryptographicException)
        {
            // Never a crash, and never a fallback to treating the ciphertext as plaintext.
            // The caller raises error with a re-pair cause - ADR 0013.
            return false;
        }
    }
}
