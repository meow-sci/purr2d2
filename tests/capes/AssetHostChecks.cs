using System;
using System.Collections.Generic;
using System.IO;
using Brutal.GltfApi;
using Brutal.Numerics;
using RenderCore;
using RenderCore.Animation;
using RenderCore.Gltf;

internal static class AssetHostChecks
{
    private const float MatrixTolerance = .002f;

    public static void Run()
    {
        CheckCape(Path.Combine("Assets", "Models", "Purr2D2_Cape.glb"));
        Console.WriteLine("Cape GLB loads through the actual managed KSA/Brutal GLTF pipeline.");
    }

    private static void CheckCape(string path)
    {
        Check(File.Exists(path), "cape GLB exists: " + path);
        using var loader = new GltfLoader(path);
        Check(loader.GltfJson.Skins is { Length: 1 }, "cape GLB has one skin");
        Skin skin = loader.GltfJson.Skins[0];
        Check(skin.Joints.Length == 118, "CapeRoot plus 117 cloth joints are authored");
        Check(loader.GltfJson.Nodes[skin.Joints[0]].Name == "CapeRoot", "cape skin is rooted at CapeRoot");

        var mappedNodes = new Dictionary<Node, int>();
        Skeleton skeleton = loader.ExtractSkeleton(mappedNodes)
            ?? throw new InvalidOperationException("Cape GLB did not yield a KSA skeleton.");
        try
        {
            Check(skeleton.BoneCount == 118, "CapeRoot skeleton includes 117 child bones");
            Check(skeleton.GetBoneName(0) == "CapeRoot", "CapeRoot maps to skeleton joint zero");
            Check(mappedNodes.Count == 118 && mappedNodes.TryGetValue(loader.GltfJson.Nodes[skin.Joints[0]], out int root)
                && root == 0, "CapeRoot is the identity skeleton root");

            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int joint = 0; joint < 118; joint++)
            {
                string expected = joint == 0 ? "CapeRoot" : $"Cape_{joint - 1:000}";
                Check(skeleton.GetBoneName(joint) == expected, "cape skeleton bone name " + expected);
                Check(names.Add(skeleton.GetBoneName(joint)), "cape bone names are unique");
                Check(mappedNodes.TryGetValue(loader.GltfJson.Nodes[skin.Joints[joint]], out int mapped)
                    && mapped == joint, "skin joint maps to skeleton index " + joint);
            }

            float matrixError = CheckRestSkinMatrices(skeleton, "cape");
            int zeroWeightSlots = CheckSkinnedMeshes(loader, skeleton, mappedNodes, "cape");
            Console.WriteLine($"Cape GLB host check: bones={skeleton.BoneCount}, joints=117, max inverse-bind/rest error={matrixError:G4}, " +
                $"zero-weight slots={zeroWeightSlots}");
        }
        finally
        {
            // GltfUtils creates these lists, while Skeleton.Dispose owns only its three rig lists.
            skeleton.InverseBindPose?.Dispose();
            skeleton.DefaultLocalPose?.Dispose();
            skeleton.Dispose();
        }
    }

    private static int CheckSkinnedMeshes(GltfLoader loader, Skeleton skeleton, Dictionary<Node, int> mappedNodes, string label)
    {
        Skin skin = loader.GltfJson.Skins[0];
        int primitiveCount = 0;
        int zeroWeightSlots = 0;
        int vertexCount = 0;
        foreach (Brutal.GltfApi.Mesh mesh in loader.GltfJson.Meshes)
        foreach (MeshPrimitive primitive in mesh.Primitives)
        {
            primitiveCount++;
            Check(primitive.Attributes.ContainsKey("NORMAL"), label + " primitive has authored normals");
            Check(primitive.Attributes.ContainsKey("JOINTS_0") && primitive.Attributes.ContainsKey("WEIGHTS_0"),
                label + " primitive has skin indices and weights");

            using MeshAsset meshAsset = GltfUtils.LoadMeshAssetForPrim(loader, primitive,
                GltfUtils.VertexImportFlags.Normals | GltfUtils.VertexImportFlags.Skinning);
            Span<float3> normals = meshAsset.GetVertexSpan<float3>(MeshAttribute.Normal);
            Span<float4> weights = meshAsset.GetVertexSpan<float4>(MeshAttribute.SkinWeights);
            Span<int4> skinIndices = meshAsset.GetVertexSpan<int4>(MeshAttribute.SkinIndices);
            Check(normals.Length == meshAsset.VertexCount, label + " normal count matches vertex count");
            Check(weights.Length == meshAsset.VertexCount && skinIndices.Length == meshAsset.VertexCount,
                label + " skin buffers match vertex count");
            for (int vertex = 0; vertex < normals.Length; vertex++)
            {
                float3 normal = normals[vertex];
                Check(Finite(normal), label + " normal is finite at vertex " + vertex);

                int4 indices = skinIndices[vertex];
                float4 vertexWeights = weights[vertex];
                for (int component = 0; component < 4; component++)
                {
                    int sourceIndex = Component(indices, component);
                    Check((uint)sourceIndex < skin.Joints.Length,
                        label + " source joint index is valid at vertex/slot " + vertex + "/" + component);
                    if (Component(vertexWeights, component) <= 0f) zeroWeightSlots++;
                }
            }

            GltfUtils.SetSkinIndicesToSkeleton(loader, skinIndices, mappedNodes);
            for (int vertex = 0; vertex < skinIndices.Length; vertex++)
            {
                int4 indices = skinIndices[vertex];
                for (int component = 0; component < 4; component++)
                    Check((uint)Component(indices, component) < skeleton.BoneCount,
                        label + " mapped joint index is valid at vertex/slot " + vertex + "/" + component);
            }
            vertexCount += meshAsset.VertexCount;
        }

        Check(primitiveCount > 0 && vertexCount > 0, label + " contains a loadable skinned mesh primitive");
        return zeroWeightSlots;
    }

    private static float CheckRestSkinMatrices(Skeleton skeleton, string label)
    {
        var inverseBindPose = skeleton.InverseBindPose
            ?? throw new InvalidOperationException(label + " has no inverse-bind matrices.");
        Check(inverseBindPose.Count == skeleton.BoneCount,
            label + " inverse-bind matrix count matches skeleton");
        float maximumError = 0f;
        for (int joint = 0; joint < skeleton.BoneCount; joint++)
        {
            float4x4 skinMatrix = inverseBindPose[joint] * skeleton.WorldTransformList[joint];
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                float value = skinMatrix[row, column];
                Check(float.IsFinite(value), label + " rest skin matrix is finite at joint " + joint);
                float expected = row == column ? 1f : 0f;
                maximumError = MathF.Max(maximumError, MathF.Abs(value - expected));
            }
        }
        Check(maximumError <= MatrixTolerance,
            $"{label} inverseBindPose * worldPose is identity within {MatrixTolerance} (max error {maximumError:G4})");
        return maximumError;
    }

    private static int Component(int4 value, int index) => index switch
    {
        0 => value.X,
        1 => value.Y,
        2 => value.Z,
        _ => value.W
    };

    private static float Component(float4 value, int index) => index switch
    {
        0 => value.X,
        1 => value.Y,
        2 => value.Z,
        _ => value.W
    };

    private static bool Finite(float3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Cape asset host check failed: " + message);
    }
}
