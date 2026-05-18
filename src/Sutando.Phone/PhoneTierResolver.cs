using Sutando.Bridge;

namespace Sutando.Phone;

/// <summary>
/// Pure mapping from <c>(callerFrom, stirAttestation, options)</c> to the canonical
/// <see cref="AccessTier"/>. Extracted so tier policy can be unit-tested without standing
/// the Twilio webhook up.
/// </summary>
/// <remarks>
/// <para>
/// Upstream's <c>conversation-server.ts</c> establishes the rule:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       Number-normalised match against the comma-separated <c>OWNER_NUMBER</c> set →
///       owner candidate.
///     </description>
///   </item>
///   <item>
///     <description>
///       Number-normalised match against <c>VERIFIED_CALLERS</c> → verified candidate.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>STIR/SHAKEN downgrade:</b> when the caller-id matches the OWNER set but the
///       inbound STIR attestation is anything other than <c>TN-Validation-Passed-A</c>, the
///       caller is downgraded to <see cref="AccessTier.Verified"/>. Spoofed caller-id can't
///       impersonate the owner.
///     </description>
///   </item>
///   <item>
///     <description>
///       When attestation is missing OR the caller is unknown, the result is
///       <see cref="AccessTier.Unverified"/>.
///     </description>
///   </item>
/// </list>
/// </remarks>
public static class PhoneTierResolver
{
    /// <summary>
    /// Twilio's "trusted" attestation marker. Anything else MUST drop the caller down at least
    /// one notch — upstream uses A-level only as the cryptographic confirmation.
    /// </summary>
    /// <remarks>
    /// See upstream <c>skills/phone-conversation/scripts/conversation-server.ts</c> for the
    /// authoritative wording. Twilio passes this string in the webhook form-body under
    /// <c>StirVerstat</c> (NOT as an HTTP header — see <c>INTEGRATION-NOTES.md</c>).
    /// </remarks>
    public const string TrustedAttestation = "TN-Validation-Passed-A";

    /// <summary>
    /// Resolve the caller's <see cref="AccessTier"/>.
    /// </summary>
    /// <param name="callerFrom">
    ///   The Twilio <c>From</c> form parameter, in any format Twilio sends (E.164 preferred but
    ///   the resolver tolerates spaces, hyphens, parens). Empty / anonymous resolves to
    ///   <see cref="AccessTier.Unverified"/>.
    /// </param>
    /// <param name="stirAttestation">
    ///   The Twilio <c>StirVerstat</c> form parameter value, or empty / null when STIR is not
    ///   present on the call leg.
    /// </param>
    /// <param name="options">Channel options carrying the OWNER + VERIFIED allow-lists.</param>
    /// <param name="downgraded">
    ///   <see langword="true"/> when the caller matched OWNER but STIR forced a drop to
    ///   <see cref="AccessTier.Verified"/>. Surfaced so callers can log the downgrade for
    ///   auditing — the policy is "log every STIR-driven downgrade".
    /// </param>
    /// <returns>The resolved tier.</returns>
    public static AccessTier Resolve(
        string callerFrom,
        string? stirAttestation,
        PhoneOptions options,
        out bool downgraded)
    {
        ArgumentNullException.ThrowIfNull(options);

        downgraded = false;

        if (string.IsNullOrWhiteSpace(callerFrom))
        {
            // Anonymous / withheld caller-id is always unverified.
            return AccessTier.Unverified;
        }

        var normalised = NormalisePhone(callerFrom);
        var owners = SplitAndNormalise(options.OwnerNumbers);
        var verified = SplitAndNormalise(options.VerifiedCallers);
        var attestationOk = string.Equals(stirAttestation, TrustedAttestation, StringComparison.Ordinal);

        if (owners.Contains(normalised))
        {
            if (attestationOk)
            {
                return AccessTier.Owner;
            }

            // Number says owner but the carrier didn't cryptographically verify it. Drop to
            // verified — the caller still proved knowledge of an allow-listed number out-of-band
            // when they configured OWNER_NUMBER, so a less-privileged but non-zero tier is the
            // upstream-aligned compromise.
            downgraded = true;
            return AccessTier.Verified;
        }

        if (verified.Contains(normalised))
        {
            return AccessTier.Verified;
        }

        return AccessTier.Unverified;
    }

    /// <summary>
    /// Strips formatting characters and folds 10-digit US numbers into their +1-prefixed
    /// canonical form (consistent with upstream <c>normalizePhone</c> in
    /// <c>conversation-server.ts</c>).
    /// </summary>
    /// <param name="raw">The raw caller-id string.</param>
    /// <returns>The digits-only canonical form.</returns>
    internal static string NormalisePhone(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        // Strip everything that isn't a digit. Inline because the input is short and a regex
        // allocation would dwarf the work.
        Span<char> buffer = stackalloc char[raw.Length];
        var written = 0;
        foreach (var ch in raw)
        {
            if (char.IsDigit(ch))
            {
                buffer[written++] = ch;
            }
        }
        var digits = new string(buffer[..written]);
        // 10-digit US number → prepend country code, so "(415) 555-0101" and "+14155550101"
        // both compare equal.
        return digits.Length == 10 ? "1" + digits : digits;
    }

    /// <summary>Split a comma-separated allow-list and normalise each entry.</summary>
    /// <param name="csv">Raw comma-separated env value, possibly null / empty / whitespace.</param>
    /// <returns>A hash set of normalised digit strings; empty when the input has no valid entries.</returns>
    internal static HashSet<string> SplitAndNormalise(string? csv)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(csv))
        {
            return result;
        }
        foreach (var token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalised = NormalisePhone(token);
            if (normalised.Length > 0)
            {
                result.Add(normalised);
            }
        }
        return result;
    }
}
