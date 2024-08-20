#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MaaUpdateEngine
{
    public class PackageManifest
    {
        [NotNull, JsonRequired] public string Name { get; set; }
        [NotNull, JsonRequired] public string Version { get; set; }
        [MaybeNull] public string Variant { get; set; }
    }

    internal class DeltaPackageManifest
    {
        [NotNull, JsonRequired] public string[] ForVersion { get; set; }
        [NotNull, JsonRequired] public Chunk[] Chunks { get; set; }
    }

    internal class Chunk
    {
        [NotNull, JsonRequired] public JsonElement Target { get; set; }
        [JsonRequired] public long Offset { get; set; }
        [JsonRequired] public long Size { get; set; }
        [NotNull, JsonRequired] public string Hash { get; set; }

        [return: NotNull]
        public string[] GetTargetVersions()
        {
            if (Target.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            return Target.EnumerateArray().Select(e => e.GetString() ?? throw new InvalidDataException()).ToArray();
        }
    }

    internal class PatchFile
    {
        [NotNull, JsonRequired] public string File { get; set; }
        [NotNull, JsonRequired] public string Patch { get; set; }
        [JsonRequired] public long OldSize { get; set; }
        [NotNull, JsonRequired] public string OldHash { get; set; }
        [JsonRequired] public long NewSize { get; set; }
        [NotNull, JsonRequired] public string NewHash { get; set; }
        [NotNull, JsonRequired] public string NewVersion { get; set; }
        [NotNull, JsonRequired] public string PatchType { get; set; }
    }

    internal class ChunkManifest
    {
        [NotNull, JsonRequired] public string PatchBase { get; set; }
        [NotNull, JsonRequired] public string[] Base { get; set; }
        [MaybeNull] public string[] RemoveFiles { get; set; }
        [MaybeNull] public PatchFile[] PatchFiles { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(PackageManifest))]
    [JsonSerializable(typeof(DeltaPackageManifest))]
    [JsonSerializable(typeof(ChunkManifest))]
    internal partial class SourceGenerationContext : JsonSerializerContext
    {
    }
}
