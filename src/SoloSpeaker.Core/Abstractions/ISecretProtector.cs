namespace SoloSpeaker.Core.Abstractions;

/// <summary>
/// Protects a secret at rest. The Windows implementation is DPAPI with
/// <c>CurrentUser</c> scope, per <c>adr/0013</c>.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that <see cref="IConfigStore"/> can live in <c>SoloSpeaker.Core</c>
/// alongside the rest of the configuration policy. <c>ProtectedData</c> is Windows-only,
/// and without this seam the one Windows call would have dragged the whole config store
/// back into <c>SoloSpeaker.App</c>, which is the boundary error finding B-4 describes.
/// </para>
/// <para>
/// Only <c>pairKey</c> is protected. Everything else in <c>config.json</c> stays readable,
/// because §7.5's cross-file check and Goal 8's hand-recoverability both assume the file
/// can be inspected.
/// </para>
/// </remarks>
public interface ISecretProtector
{
    /// <summary>
    /// Protects a secret, bound to <paramref name="entropy"/> so that a value from an
    /// older pairing on the same profile fails to unprotect rather than silently yielding
    /// a key for the wrong pair.
    /// </summary>
    byte[] Protect(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> entropy);

    /// <summary>
    /// Unprotects a secret. Returns <see langword="false"/> when the value was protected
    /// under a different user profile or a different pairing - never throws, and never
    /// falls back to treating the ciphertext as plaintext.
    /// </summary>
    bool TryUnprotect(ReadOnlySpan<byte> protectedSecret, ReadOnlySpan<byte> entropy, out byte[] secret);
}
