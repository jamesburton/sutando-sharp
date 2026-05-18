using System.Runtime.CompilerServices;

// Tests assert on the per-call audio resampler/codec, the tier resolver, the call-metadata
// store, and the Twilio wire-message envelopes. None of these are part of the public surface;
// granting Sutando.Tests visibility keeps internals internal in production builds.
[assembly: InternalsVisibleTo("Sutando.Tests")]
