namespace Purr2D2.Autopilot;

// Size guards apply to the complete target, including decorative/IVA subparts.
// Stock Rocket alone lists 1,032 part instances and over 365,000 render triangles.
// Keep capture, picking and search aligned so accepting a snapshot cannot trip a
// smaller input limit in the next step. Search work has a separate finite budget.
internal static class AutopilotGeometryLimits
{
    internal const int Parts = 32_768;
    internal const int Meshes = 32_768;
    internal const int Primitives = 131_072;
    internal const int Triangles = 4_000_000;
    internal const int HierarchyDepth = 256;
    internal const int RingCandidates = 8_192;
    internal const int TriangleTests = 16_000_000;
    internal const int RouteTriangles = 250_000;
}
