using DungeonMasterAI.Domain;

namespace DungeonMasterAI.MapAssetTests;

/// <summary>
/// r54's asset-pack suite, reduced in r63 to the part that survives the removal of the WPF front
/// end: the two <see cref="TacticalMapAssetPackValidator"/> checks, which test a Domain type and
/// are carried over verbatim.
///
/// Everything else this suite used to prove -- catalog discovery, author/license metadata
/// surviving into the catalog, deterministic seeded variant selection, decoded-bitmap caching, a
/// missing key returning false rather than throwing, and the asset-backed renderer producing a
/// 1280x720 PNG -- exercised <c>TacticalMapAssetCatalog</c> and <c>TacticalMapControl</c>, which
/// lived in <c>DungeonMasterAI.App</c>. That behaviour had no engine-side implementation to test
/// against, so it was deleted with the front end rather than relocated. The guarantees it encoded
/// are recorded as acceptance criteria for Unity's ScriptedImporter/MapAssetPackSO replacement in
/// docs/unity-migration-plan.md section 7.2 and docs/unity-target-architecture.md section 5.3.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var failures = new List<string>();
        try
        {
            var manifest = new TacticalMapAssetPackManifest
            {
                PackId = "test.hq.crypt",
                Name = "CI High Quality Test Pack",
                Version = "1.0.0",
                Author = "DungeonMasterAI CI",
                License = "Project test fixture",
                Credits = "Generated during test execution",
                Assets =
                [
                    new TacticalMapAssetDefinition
                    {
                        Key = "floor.stone.crypt_flagstone", Kind = "floor", RenderMode = "tile", AllowProceduralFallback = false,
                        Variants =
                        [
                            new TacticalMapAssetVariant { File = "floor-a.png", Weight = 3 },
                            new TacticalMapAssetVariant { File = "floor-b.png", Weight = 1, RotationDegrees = 90 }
                        ]
                    },
                    new TacticalMapAssetDefinition
                    {
                        Key = "wall.stone.crypt_block", Kind = "wall", RenderMode = "segment", AllowProceduralFallback = false,
                        Variants = [new TacticalMapAssetVariant { File = "wall.png" }]
                    },
                    new TacticalMapAssetDefinition
                    {
                        Key = "door.wood.ironbound", Kind = "door", RenderMode = "segment", AllowProceduralFallback = false,
                        Variants = [new TacticalMapAssetVariant { File = "door.png" }]
                    },
                    new TacticalMapAssetDefinition
                    {
                        Key = "prop.pillar.stone_round", Kind = "prop", RenderMode = "sprite", Scale = 0.86, AllowProceduralFallback = false,
                        Variants = [new TacticalMapAssetVariant { File = "pillar.png" }]
                    }
                ]
            };

            var validation = TacticalMapAssetPackValidator.Validate(manifest);
            Check(validation.IsValid, "Valid licensed asset manifest passes validation.", failures);

            var unsafeManifest = new TacticalMapAssetPackManifest
            {
                PackId = "unsafe",
                Name = "Unsafe",
                Author = "Test",
                License = "Test",
                Assets = [new TacticalMapAssetDefinition
                {
                    Key = "prop.bad", AllowProceduralFallback = false,
                    Variants = [new TacticalMapAssetVariant { File = "../escape.png" }]
                }]
            };
            Check(!TacticalMapAssetPackValidator.Validate(unsafeManifest).IsValid,
                "Asset manifest rejects directory traversal outside the pack.", failures);

            if (failures.Count == 0)
            {
                Console.WriteLine("MAP ASSET MANIFEST PASS");
                Console.WriteLine("Licensed manifest acceptance and directory-traversal rejection verified.");
                return 0;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Unhandled map asset test exception: {ex}");
        }

        Console.Error.WriteLine($"MAP ASSET MANIFEST FAILED: {failures.Count} issue(s)");
        foreach (var failure in failures) Console.Error.WriteLine($" - {failure}");
        return 1;
    }

    private static void Check(bool condition, string message, ICollection<string> failures)
    {
        if (!condition) failures.Add(message);
    }
}
