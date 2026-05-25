# Feature-gap delta — 2026-05-25 follow-up sweep

Follow-up to [`upstream-feature-gap.md`](upstream-feature-gap.md) (same day). The
gap doc was already comprehensive on the skill catalogue; this sweep diffs the
upstream **`README.md` "What's inside" table** and the **`skills/MANIFEST.md`
manifest-loaded skill pattern** against the gap doc to catch capabilities the
gap doc didn't enumerate. Findings are partitioned into:

1. **Capabilities missing from the gap doc entirely** — net-new gaps.
2. **Pattern details the gap doc captured at the wrong level of resolution** —
   gap is named, but the surface area is bigger than the gap-doc row suggests.
3. **In-flight wave deltas** — what flips when the three concurrent agents
   (`Sutando.Proactive`, `Sutando.Notes`, voice-tool bridge) land.

---

## 1. Capabilities missing from the gap doc entirely

### Zoom / Google Meet join (voice-agent inline tools)

Upstream `src/inline-tools.ts` + `src/meeting-tools.ts` expose `join_zoom` and
`join_google_meet` as inline tools. The README "What's inside" table lists both
as **Verified**:

| Capability | Upstream | sutando-sharp |
|---|---|---|
| Join Zoom (computer audio) | `inline-tools.ts` (`join_zoom`) | ❌ no equivalent |
| Join Google Meet (browser audio) | `inline-tools.ts` (`join_google_meet`) | ❌ no equivalent |
| Meeting dial-in (Meet + Zoom) | `phone-conversation/` | ❌ no equivalent (`Sutando.Phone` doesn't model meeting dial-in yet) |

**Path to add (Windows):**

- Zoom: URL-handler launch (`zoommtg://`) — straightforward; same wire shape as
  upstream.
- Google Meet: open URL in the browser + Playwright-driven auto-join (we
  already have `Sutando.Browser`).
- Meeting dial-in (PSTN bridge): extends `Sutando.Phone` to accept a meeting
  ID/password and dial Twilio outbound into the conference.

These are mid-effort A-bucket items that aren't on the gap doc roadmap.

### Meeting approval workflow (tier gating before delegation)

Upstream `phone-conversation/scripts/conversation-server.ts` implements an
approval handshake: before enabling task delegation from a meeting, the agent
DMs the owner on Telegram and waits for approval. The gap doc has Telegram
(`Sutando.Channels.Telegram`) and phone (`Sutando.Phone`) but doesn't capture
this **gated approval flow** as a discrete capability — it's a coordination
pattern across both channels.

**Path to add:** a small `IMeetingApprovalGate` abstraction in `Sutando.Phone`
that publishes via any `IChannel` and resolves on the reply marker. Reuses
existing channels; no new dependency.

### Pattern detection / user modeling

README row: *"Pattern detection + user modeling — Built into Claude Code memory
system."* Sutando-sharp's auto-memory system (see `C:\Users\james\.claude\projects\...\memory\`)
is the equivalent mechanism, but **no Sutando-sharp component publishes
detected patterns**. Upstream's `daily-insight.py` (referenced in
`crons.example.json`) is the script that derives the daily behavioural insight
from the memory store.

**Status:** ◐ Partial. The substrate exists (Claude Code memory works
identically on Windows), but the periodic insight-extraction script does not.
Cheapest to bolt onto `Sutando.Proactive` once Agent A lands — it's "a script
the loop runs nightly," not a new architectural layer.

### Quota-tracker as a first-class skill

The gap doc mentions quota-tracker only in architectural gap #7 (folded into
"credential vault"). Upstream ships it as its own skill with `SKILL.md` +
`scripts/read-quota.py`, and the `proactive-loop` skill calls it per pass to
choose between FULL / MEDIUM / LIGHT / MINIMAL execution depth (`SKILL.md`
lines 40-47). That decision logic is **load-bearing for proactive behaviour** —
without it, the loop has no way to throttle itself when quota is low.

**Path to add:** small `IQuotaProbe` in `Sutando.Proactive` (Agent A is the
natural home) returning a `QuotaSnapshot { Remaining: double, ResetAt:
DateTimeOffset }`. Implementations: `AnthropicHeaderQuotaProbe` (parses rate-
limit headers from `IAgentExecutor` responses) and a `NullQuotaProbe` default.
Pass uses the snapshot to pick depth tier.

### Multi-machine onboarding handoff

README: *"Plug in a second Mac and Sutando sets it up — the original agent
opens a Discord channel, sends setup commands, and migrates services."* The
gap doc has `cross-node-sync` as 🚫 deferred (rsync-over-ssh on macOS, mostly
data-plane), but the **autonomous setup handoff** — original agent
provisioning the new node via Discord — is a separate capability that isn't
listed. This is bot-to-bot coordination + scripted setup, not file sync.

**Status:** ❌ Missing. Depends on `bot2bot-post` (already in gap doc as A-bucket)
plus a scripted-setup primitive sutando-sharp doesn't have.

---

## 2. Gap-doc rows that capture less than the upstream surface

### Manifest-tool injection (gap doc arch gap #6) — needs a phone-surface row too

Gap doc captures the voice-agent side: "wire `SkillRegistry` into VoiceSession's
tool-dispatch path." But upstream's `loadSkillManifestTools()` in
`src/inline-tools.ts` is consumed by **both** the voice agent **and** the phone
agent: `skills/phone-conversation/scripts/conversation-server.ts:587` pushes
the same `inlineTools` into the phone session's tool table (verified in
upstream `skills/MANIFEST.md` line 99).

**Implication for the in-flight Agent C work:** the voice-tool bridge lands
parity on the voice surface but **leaves `Sutando.Phone` one step behind**.
After Agent C merges, a follow-up commit should wire the same bridge into
phone conversations — small change, same registry, same dispatcher.

### Manifest-tool injection (gap doc arch gap #6) — private-skill-dir scan

Upstream's loader scans `<repo>/skills/` AND
`$SUTANDO_PRIVATE_DIR/skills/` (`MANIFEST.md` lines 53-65). All four currently
active manifest skills (`voice-context`, `talk-highlight`, `personal-deictic`,
`personal-talk-prep`) live in the private dir — i.e. the "personal extensible
tools" pattern is the **primary use** of the loader in practice, not the
secondary one.

**Implication:** Agent C's bridge takes a `SkillRegistry` reference. Whoever
wires the CLI integrator (post-merge) should add a `--private-skill-dir` CLI
flag (or `SUTANDO_PRIVATE_DIR` env) that loads extra skills into the same
registry before voice session start. Otherwise we ship the loader without its
primary use case.

### Phone-caller tier enforcement of manifest tools

Upstream `MANIFEST.md` lines 99-103: manifest-loaded tools are exposed to
**owner phone callers only**; non-owners stay on `anyCallerTools` /
`configurableTools`. The gap doc's architectural gap #4 ("3-tier access
enforcement at every channel") is the right placeholder, but Agent C's bridge
needs to honour this from day one — the bridge shouldn't blindly register all
skills for a tier-unknown caller.

**Implication for integration:** when CLI-wiring lands, the bridge's
`GetTools(callerTier)` path should respect tier; default to "owner-only" if
tier isn't resolvable to fail safe.

---

## 3. Wave deltas — what flips when the in-flight agents merge

| Gap-doc row | Today | After this wave |
|---|---|---|
| `proactive-loop` | ❌ Missing | ✓ (Agent A — plumbing; LLM-driven loop body still external) |
| `schedule-crons` | ❌ Missing | ✓ (Agent A) |
| Arch gap #1 (autonomous loop) | 0% | ≈40% — plumbing in place, executor wiring + 11-step body still external |
| Arch gap #2 (notes / second brain) | ❌ Missing | ✓ (Agent B) — managed CRUD + search + tag, no semantic search yet |
| Arch gap #6 (voice tool injection) | ❌ Missing | ✓ on voice surface (Agent C); phone surface still pending (see §2) |
| `subscription-scanner` | ❌ Missing | Unblocked — gmail skill now exists; this is the cheapest next A-bucket port (gap doc already flags as "trivial after Wave C") |
| `info-radar` daily digest | ❌ Missing — needed `schedule-crons` | Unblocked — Agent A delivers the cron driver, port the HTTP-only skill on top |

**After this wave, the next-best ROI moves to:**

1. **Phone-surface tool bridge** — extends Agent C's bridge into
   `Sutando.Phone.PhoneSession` (~1-day port; same registry, same dispatcher).
2. **`info-radar` port** — pure HTTP, no auth, lands on top of the new cron
   driver. Cheapest A-bucket skill remaining.
3. **`subscription-scanner` port** — depends on gmail (now done) + JSON diff
   logic. Low complexity.
4. **Zoom / Meet `join_*` inline tools** — first new capability not yet on the
   gap-doc roadmap.

---

## What this sweep *didn't* find

To set expectations: the gap doc was already accurate on the core spine,
cloud-skill subset, deferred-by-design `[B]/[C]` bucket, and the A-bucket port
list. Most upstream skills (`bot2bot-post`, `call-diagnostics`, `claude-*`,
`deal-finder`, `meeting-prep`, `regression-search`, `self-diagnose`, etc.) are
correctly classified — no category moves needed beyond the wave flips above.
