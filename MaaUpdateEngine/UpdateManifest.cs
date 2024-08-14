#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace MaaUpdateEngine
{
    public class PackageManifest
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string? Variant { get; set; }
    }

    internal class DeltaPackageManifest
    {
        public string[] ForVersion { get; set; }
        public Chunk[] Chunks { get; set; }
    }

    internal class Chunk
    {
        public JsonElement Target { get; set; }
        public long Offset { get; set; }
        public long Size { get; set; }
        public string Hash { get; set; }

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
        public string File { get; set; }
        public string Patch { get; set; }
        public long OldSize { get; set; }
        public string OldHash { get; set; }
        public long NewSize { get; set; }
        public string NewHash { get; set; }
        public string NewVersion { get; set; }
        public string PatchType { get; set; }
    }

    internal class ChunkManifest
    {
        public string PatchBase { get; set; }
        public string[] Base { get; set; }
        public string[]? RemoveFiles { get; set; }
        public PatchFile[]? PatchFiles { get; set; }
    }

}
