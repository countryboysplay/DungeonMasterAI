// Compiled into every test project only when it is built with -p:UseNetStandardEngine=true.
// See windows/tests/Directory.Build.targets for why.
//
// This exists because the failure mode of a differential test run is not a red build, it is a
// green one that tested the wrong thing. If SetTargetFramework ever silently stops applying, the
// netstandard2.1 pass would load the net10.0 engine, pass every assertion, and report that both
// targets agree -- while having compared net10.0 against itself. This module initializer runs
// before Main and makes that impossible: it fails the process, loudly, before a single test runs.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace DungeonMasterAI.Tests.Compat;

internal static class NetStandardEngineAssertion
{
    private const string Expected = ".NETStandard,Version=v2.1";

    [ModuleInitializer]
    internal static void AssertEngineIsNetStandard21()
    {
        var self = typeof(NetStandardEngineAssertion).Assembly;
        var checkedAny = false;

        foreach (var referenced in self.GetReferencedAssemblies())
        {
            var name = referenced.Name;
            if (name is null || !name.StartsWith("DungeonMasterAI.", StringComparison.Ordinal)) continue;

            var loaded = Assembly.Load(referenced);
            var framework = loaded.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
            if (framework != Expected)
            {
                // DungeonMasterAI.AI is net10.0-only by design and is never multi-targeted: it
                // spawns a llama-server child process and speaks HTTP, and Unity handles that side
                // separately. A test project that references it therefore cannot take part in the
                // differential run at all -- it would end up with a netstandard2.1 Engine and a
                // net10.0 AI compiled against a different Engine.
                var hint = name == "DungeonMasterAI.AI"
                    ? "DungeonMasterAI.AI is net10.0-only and is not multi-targeted, so a test project that " +
                      "references it cannot be part of the differential run. Exclude it from the netstandard2.1 pass."
                    : "Check the ProjectReference Update paths in windows/tests/Directory.Build.targets.";

                throw new InvalidOperationException(
                    $"UseNetStandardEngine=true, but {name} was loaded as '{framework ?? "(no TargetFrameworkAttribute)"}' " +
                    $"instead of '{Expected}'. The differential test run is testing the wrong assembly. {hint}");
            }

            checkedAny = true;
        }

        if (!checkedAny)
        {
            throw new InvalidOperationException(
                "UseNetStandardEngine=true, but this test assembly references no DungeonMasterAI assembly at all, " +
                "so nothing was verified. A test project with no engine reference must not be part of the " +
                "differential run.");
        }
    }
}
