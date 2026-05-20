using System.Diagnostics.CodeAnalysis;

namespace Sutando.Pipeline;

/// <summary>
/// The unit type — a singleton struct with a single value, used to represent the absence of
/// meaningful data in a stage's input or output type parameter.
/// </summary>
/// <remarks>
/// <para>
/// Sources (stages that produce frames without consuming any) are
/// <see cref="IPipelineStage{TIn,TOut}"/> with <c>TIn = Unit</c>; sinks (stages that consume
/// frames without producing any) have <c>TOut = Unit</c>. This keeps the single
/// <see cref="IPipelineStage{TIn,TOut}"/> interface usable across the whole pipeline rather
/// than introducing separate <c>ISource</c> / <c>ISink</c> interfaces.
/// </para>
/// <para>
/// The constant <see cref="Value"/> is the canonical singleton. Comparing two <see cref="Unit"/>
/// values always succeeds (they're indistinguishable by definition).
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public readonly record struct Unit
{
    /// <summary>The single inhabitant of the <see cref="Unit"/> type.</summary>
    public static Unit Value => default;
}
