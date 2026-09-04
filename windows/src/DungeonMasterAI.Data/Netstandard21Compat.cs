// Compatibility surface for the netstandard2.1 leg of this project's <TargetFrameworks>.
//
// Unity's scripting runtime is .NET Standard 2.1, so a net10.0-only assembly will not load at all.
// docs/unity-migration-plan.md 1.4 is the compiler-verified inventory of what netstandard2.1 lacks;
// this file holds this assembly's share of the remediation.
//
// The rule this file follows: prefer ONE implementation that serves both targets over a #if fork.
// A fork is a permanent invitation for the two legs to drift apart silently, which is the exact
// failure mode -- "it compiles and quietly behaves differently" -- this migration is designed
// against. Only IsExternalInit is #if-gated, and only because net10.0 already defines the real type
// and a second definition of the same fully-qualified name would collide.

using System.Diagnostics.CodeAnalysis;

#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Marker the C# compiler requires in order to emit <c>init</c> accessors. netstandard2.1
    /// predates it, and every positional <c>record</c> in this assembly is lowered through
    /// <c>init</c>, so without this type the netstandard2.1 leg does not compile.
    /// Not needed on net10.0, whose runtime supplies the real type.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}
#endif

namespace DungeonMasterAI.Data
{
    /// <summary>
    /// Argument guards. Replaces <c>ArgumentNullException.ThrowIfNull</c>, which is .NET 6+ and
    /// cannot be polyfilled -- <c>ArgumentNullException</c> already exists in netstandard2.1
    /// (without the method) and C# cannot add a static member to a type it does not own.
    /// </summary>
    /// <remarks>
    /// This type is deliberately NOT <c>#if</c>-gated: it replaces <c>ThrowIfNull</c> identically on
    /// both targets, so this remediation cannot itself become a source of net10.0/netstandard2.1
    /// behavioural drift.
    ///
    /// <para>Every call site the rewrite touched passed a bare identifier, so <c>nameof(x)</c> is a
    /// faithful substitute for the <c>[CallerArgumentExpression]</c> text the real
    /// <c>ThrowIfNull</c> would have captured, and the thrown exception's <c>ParamName</c> is
    /// unchanged.</para>
    /// </remarks>
    internal static class Guard
    {
        /// <summary>
        /// Throws <see cref="ArgumentNullException"/> with <paramref name="paramName"/> when
        /// <paramref name="value"/> is null. Behaviourally identical to
        /// <c>ArgumentNullException.ThrowIfNull(value)</c> for a bare-identifier argument.
        /// </summary>
        public static void NotNull<T>([NotNull] T? value, string paramName)
            where T : class
        {
            if (value is null) throw new ArgumentNullException(paramName);
        }
    }
}
