using Sutando.Bridge;
using Sutando.Phone;

namespace Sutando.Tests.Phone;

/// <summary>
/// Unit tests for the pure tier-policy resolver. No host, no Twilio, just the mapping.
/// </summary>
public sealed class PhoneTierResolverTests
{
    [Fact]
    public void Empty_caller_resolves_to_unverified()
    {
        var opts = new PhoneOptions { OwnerNumbers = "+14155550101" };
        var tier = PhoneTierResolver.Resolve(string.Empty, "TN-Validation-Passed-A", opts, out var downgraded);

        Assert.Equal(AccessTier.Unverified, tier);
        Assert.False(downgraded);
    }

    [Fact]
    public void Owner_with_A_attestation_resolves_to_owner()
    {
        var opts = new PhoneOptions { OwnerNumbers = "+14155550101" };
        var tier = PhoneTierResolver.Resolve("+14155550101", "TN-Validation-Passed-A", opts, out var downgraded);

        Assert.Equal(AccessTier.Owner, tier);
        Assert.False(downgraded);
    }

    [Fact]
    public void Owner_with_B_attestation_is_downgraded_to_verified()
    {
        // Anything other than A-level proves the caller-id isn't cryptographically verified —
        // upstream's policy: drop to verified. The downgraded flag must be true so the host
        // can log the security event.
        var opts = new PhoneOptions { OwnerNumbers = "+14155550101" };
        var tier = PhoneTierResolver.Resolve("+14155550101", "TN-Validation-Passed-B", opts, out var downgraded);

        Assert.Equal(AccessTier.Verified, tier);
        Assert.True(downgraded);
    }

    [Fact]
    public void Owner_with_no_attestation_is_downgraded()
    {
        var opts = new PhoneOptions { OwnerNumbers = "+14155550101" };
        var tier = PhoneTierResolver.Resolve("+14155550101", null, opts, out var downgraded);

        Assert.Equal(AccessTier.Verified, tier);
        Assert.True(downgraded);
    }

    [Fact]
    public void Verified_caller_does_not_require_attestation()
    {
        // Verified-tier callers go through the same allow-list lookup but the STIR downgrade
        // doesn't apply — they already start at verified, no further drop is meaningful.
        var opts = new PhoneOptions
        {
            OwnerNumbers = "+14155550101",
            VerifiedCallers = "+14155550202",
        };
        var tier = PhoneTierResolver.Resolve("+14155550202", "No-TN-Validation", opts, out var downgraded);

        Assert.Equal(AccessTier.Verified, tier);
        Assert.False(downgraded);
    }

    [Fact]
    public void Unknown_caller_is_always_unverified()
    {
        var opts = new PhoneOptions
        {
            OwnerNumbers = "+14155550101",
            VerifiedCallers = "+14155550202",
        };
        var tier = PhoneTierResolver.Resolve("+14155559999", "TN-Validation-Passed-A", opts, out var downgraded);

        Assert.Equal(AccessTier.Unverified, tier);
        Assert.False(downgraded);
    }

    [Fact]
    public void Phone_normalisation_folds_us_10_digit_to_11_digit()
    {
        // Upstream contract: "(415) 555-0101" normalises to "14155550101" so it matches an
        // E.164-formatted OWNER allow-list entry.
        var opts = new PhoneOptions { OwnerNumbers = "+14155550101" };
        var tier = PhoneTierResolver.Resolve("(415) 555-0101", "TN-Validation-Passed-A", opts, out var downgraded);

        Assert.Equal(AccessTier.Owner, tier);
        Assert.False(downgraded);
    }

    [Fact]
    public void Multiple_owners_resolved_independently()
    {
        var opts = new PhoneOptions { OwnerNumbers = "+14155550101,+14155550202" };

        var tier1 = PhoneTierResolver.Resolve("+14155550101", "TN-Validation-Passed-A", opts, out _);
        var tier2 = PhoneTierResolver.Resolve("+14155550202", "TN-Validation-Passed-A", opts, out _);

        Assert.Equal(AccessTier.Owner, tier1);
        Assert.Equal(AccessTier.Owner, tier2);
    }
}
