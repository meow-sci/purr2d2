// A deliberately small ownership model for detached preparation. These classes
// never enter the mod build; real host member compatibility is checked separately.
namespace KSA
{
    internal sealed class PartTemplate
    {
        internal float Mass = 1;
        internal bool HasCollider = true;
    }
    internal sealed class ColliderModule
    {
        internal readonly ShapeHandle Handle = new();
        internal sealed class ShapeHandle { internal bool Exists = true; }
    }
    internal sealed class ModuleList
    {
        internal ColliderModule[] Colliders = [new()];
        internal Span<T> Get<T>() => (T[])(object)Colliders;
    }
    internal sealed class Part
    {
        internal static int Constructions;
        internal int Disposals;
        internal readonly PartTemplate Template;
        internal readonly ModuleList Modules = new();
        internal PartTree? Tree;
        internal Part(string id, PartTemplate template)
        {
            Template = template; Constructions++;
            if (!template.HasCollider) Modules.Colliders = [];
        }
        internal float ComputeSubtreeInertMass() => Template.Mass;
        internal void Dispose() => Disposals++;
    }
    internal sealed class PartTree
    {
        internal static int Constructions;
        internal static bool FailAfterTransfer;
        internal int Disposals;
        internal bool IsDisposed;
        internal Vehicle? OwningVehicle;
        internal readonly Part Root;
        private PartTree(Part root) { Root = root; Constructions++; }
        internal static PartTree CreateFromNewPartTree(Part root)
        {
            var tree = new PartTree(root);
            root.Tree = tree; // Native TransferPart installs membership before solving derived data.
            if (FailAfterTransfer) throw new InvalidOperationException("Fixture tree failure");
            return tree;
        }
        internal void Dispose() { Disposals++; IsDisposed = true; Root.Dispose(); }
    }
    internal sealed class Vehicle
    {
        internal readonly PartTree Parts;
        internal Vehicle(Part root)
        {
            Parts = root.Tree ?? throw new InvalidOperationException("Missing prepared tree");
            Parts.OwningVehicle = this;
        }
        internal void Dispose() => Parts.Dispose();
    }
}
namespace Purr2D2.Rendering
{
    internal sealed record PreparedFragment(KSA.PartTemplate Template, string TemplateId);
}
