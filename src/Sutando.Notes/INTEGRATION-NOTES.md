# Sutando.Notes — integration notes

## What this is

Managed second-brain layer over the workspace's `notes/` directory. Implements the
**Notes / second brain** row of [`docs/upstream-feature-gap.md`](../../docs/upstream-feature-gap.md):
upstream Sutando stores YAML-frontmatter markdown notes the agent can search and act on;
`Sutando.Workspace` has a `notes/` directory but no managed search / write / tag layer until this
project landed.

## Scope shipped now

- **`Note` model** — frontmatter pass-through map, flattened `Tags`, body, created/modified
  timestamps (frontmatter-preferred, filesystem fallback).
- **`NoteFrontmatterParser`** — YAML `---` block read/write via YamlDotNet. Pass-through map
  preserves arbitrary keys; round-trips strings / longs / bools / doubles / nested maps / lists
  without loss.
- **`INoteStore` + `FileSystemNoteStore`** — list / read / write / delete. Atomic-write semantics
  (`.tmp` staging + `File.Move(overwrite: true)`); forward-slash relative paths regardless of OS;
  path-traversal defence; malformed-note skipping in `ListAsync`.
- **`NotesService`** — managed `SearchAsync` (text + tags-all-of + frontmatter filters + limit,
  sorted by most-recently-modified), `CreateAsync`, `UpdateBodyAsync`, `AddTagAsync`,
  `RemoveTagAsync`. Tags dedupe case-insensitively; first-write wins on casing.
- **Four `ISkill` tools** under `Builtin/`:
  - `notes.search` — `text?`, `tags?` (csv), `limit?`
  - `notes.read`   — `path`
  - `notes.write`  — `path`, `body`, `tags?` (csv) — create-or-update dispatcher
  - `notes.tag`    — `path`, `add?` / `remove?` (csv)
- **`NotesSkillRegistration.RegisterAll(SkillRegistry, NotesService, INoteStore)`** —
  single-call entry point mirroring `CloudSkillRegistration.Register`.

## Explicit out-of-scope (follow-ups)

1. **No DI extension method.** The CLI integrator will add an
   `IServiceCollection.AddSutandoNotes(...)` (or similar) extension when wiring the host —
   that lives in `Sutando.Cli` (or a dedicated `Sutando.Notes.DependencyInjection` shim) and
   is intentionally separate from this library to avoid taking a transitive dependency on
   `Microsoft.Extensions.DependencyInjection.Abstractions` for callers that construct the
   service directly.
2. **No cross-node sync.** Upstream's `cross-node-sync` skill rsync's the `notes/` directory
   between machines via SSH. That's a separate skill (and a separate scope row in
   `docs/upstream-feature-gap.md`); this library only manages the on-disk shape.
3. **No vector / semantic search.** `NotesService.SearchAsync` is a substring scan over body +
   frontmatter scalars. A future embedding-backed search layer is anticipated but explicitly
   out of scope.
4. **No Markdown rendering.** `Note.Body` is the raw markdown source. Rendering to HTML / ANSI
   / Discord-flavoured markdown is a presentation concern owned by the channel layer.
5. **`created`/`modified` vs upstream `date`.** The spec for this library uses
   `created:` / `modified:` keys (managed by `NotesService`). Upstream's `CLAUDE.md` example
   uses a single `date:` key. If real-world upstream-compat round-tripping turns out to matter,
   either a frontmatter migration pass or a `date`-aware fallback in
   `FileSystemNoteStore.TimestampFromFrontmatter` is the cheapest fix — but neither is needed
   today.
6. **No `JsonSchema`/parameter schema on `ISkill`.** The current `SkillManifest` has no slot
   for typed parameter schema. The skills here document their arguments via XML doc comments
   and rely on the agent-side prompt to populate them correctly. If/when `ISkill` grows a
   parameter-schema surface, the four notes skills should publish theirs.

## How the CLI integrator wires it

The library deliberately does **not** modify `Sutando.sln`, `Sutando.Cli`, or `Sutando.Skills` —
that's a separate follow-up commit. The integrator's checklist:

1. **Add `src/Sutando.Notes/Sutando.Notes.csproj` to `Sutando.sln`** alongside the other
   `src/` projects. (`tests/Sutando.Tests/Sutando.Tests.csproj` already references the project
   so test discovery works without further changes once the sln add is done.)
2. **In `Sutando.Cli` host startup**, after the `SkillRegistry` is constructed and the
   on-disk discovery pass has run:
   ```csharp
   var notesStore = new FileSystemNoteStore(workspace);     // workspace = WorkspaceDirectory.Resolve(...)
   var notesService = new NotesService(notesStore);
   NotesSkillRegistration.RegisterAll(registry, notesService, notesStore);
   ```
   Mirror the spot where `CloudSkillRegistration.Register(registry)` is called. There are no
   environment-variable gates — the notes skills always register when this is called.
3. **(Optional)** Expose `notesService` and `notesStore` to other consumers via DI as
   singletons. Both types are thread-safe at the public-API surface (the store does
   per-operation IO, the service is stateless).

## Tests

`tests/Sutando.Tests/Notes/` — 48 tests, all green on `net10.0`:

- `NoteFrontmatterParserTests` (11) — round-trip, CRLF tolerance, malformed YAML, ExtractTags
  shapes.
- `FileSystemNoteStoreTests` (11) — list/read/write/delete, atomic-write residue, nested
  directories, path-traversal, frontmatter/filesystem timestamps.
- `NotesServiceTests` (12) — create/update/add-tag/remove-tag, search by text/tags/frontmatter,
  limit ordering, case-insensitive dedupe.
- `NotesSkillTests` (14) — one happy-path + one failure-mode per skill, registration
  end-to-end, manifest-shape guard.
