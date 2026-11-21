using KSA;

namespace Purr2D2.Cutting;

/// <summary>Native inside-out interior views are render-only copies of another hull mesh.</summary>
internal static class NativeInteriorShell
{
    // KSA 5901 adds MeshAtlas Inverted="true": a listed atlas mesh is loaded again with
    // reversed winding and negated normals as <name>_Inverted. Stock HM-2/HM-3 habitat
    // modules place those copies of the fuel-tank skin/endcaps exactly on the exterior
    // hull as Internal PartModels. Slice capture would reorient such a closed island
    // outward onto the exterior, and generated fragments (which carry no Internal flag)
    // would gain a coincident second hull. Only that combination is excluded; ordinary
    // internal IVA geometry keeps its existing capture behaviour.
    internal static bool ExcludedFromCapture(PartModelModule.Template template) =>
        template.Internal && template.Mesh?.Value?.Inverted == true;
}
