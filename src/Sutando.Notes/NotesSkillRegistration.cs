using System.Collections.Generic;
using Sutando.Notes.Builtin;
using Sutando.Skills;

namespace Sutando.Notes;

/// <summary>
/// Entry point for registering the four built-in notes skills (<c>notes.search</c>,
/// <c>notes.read</c>, <c>notes.write</c>, <c>notes.tag</c>) with a <see cref="SkillRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>Sutando.Skills.Cloud.CloudSkillRegistration</c> in shape but is configured
/// differently: there are no environment-variable gates here — the notes skills require only a
/// working <see cref="NotesService"/>, which the host must supply. The expectation is the host
/// constructs one <see cref="FileSystemNoteStore"/> + <see cref="NotesService"/> pair at
/// startup (rooted at <see cref="Sutando.Workspace.WorkspaceDirectory.Notes"/>) and passes it
/// in here.
/// </para>
/// <para>
/// Returns the IDs of the skills that were registered (always all four when called
/// successfully) so callers can log which capabilities are now live.
/// </para>
/// </remarks>
public static class NotesSkillRegistration
{
    /// <summary>
    /// Register all four notes skills with <paramref name="registry"/>, wired against the
    /// supplied <paramref name="service"/> and <paramref name="store"/>.
    /// </summary>
    /// <param name="registry">Target registry.</param>
    /// <param name="service">Backing service (powers search / write / tag).</param>
    /// <param name="store">Backing store (powers read, and is also used by the write skill's create/update dispatcher).</param>
    /// <returns>The registered skill IDs.</returns>
    public static IReadOnlyList<string> RegisterAll(SkillRegistry registry, NotesService service, INoteStore store)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(store);

        var skills = new ISkill[]
        {
            new NotesSearchSkill(service),
            new NotesReadSkill(store),
            new NotesWriteSkill(service, store),
            new NotesTagSkill(service),
        };

        var registered = new List<string>(skills.Length);
        foreach (var skill in skills)
        {
            registry.RegisterInstance(skill);
            registered.Add(skill.Manifest.Id);
        }
        return registered;
    }
}
