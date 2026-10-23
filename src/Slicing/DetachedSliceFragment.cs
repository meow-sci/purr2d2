using KSA;
using Purr2D2.Rendering;

namespace Purr2D2.Slicing;

/// <summary>A single-use, main-thread-owned fragment. Until Vehicle.CreateVehicle
/// takes its tree, neither the part nor its tree belongs to the simulation.</summary>
internal sealed class DetachedSliceFragment : IDisposable
{
    private Part? _root;
    internal Part Root => _root ?? throw new InvalidOperationException("The prepared fragment was already transferred or released.");
    internal bool Ready => _root?.Tree is { IsDisposed: false, OwningVehicle: null };

    internal DetachedSliceFragment(PreparedFragment prepared)
    {
        var root = new Part(prepared.TemplateId, prepared.Template);
        try
        {
            if (!(root.ComputeSubtreeInertMass() > 0))
                throw new InvalidOperationException("Prepared fragment has no native mass.");
            if (root.Modules.Get<ColliderModule>().IsEmpty)
                throw new InvalidOperationException("Prepared fragment has no native collider module.");
            foreach (var collider in root.Modules.Get<ColliderModule>())
                if (!collider.Handle.Exists) throw new InvalidOperationException("Prepared fragment collider could not be cooked.");
            _root = root;
        }
        catch { root.Dispose(); throw; }
    }

    // Kept separate from construction so a complex native module set and its tree
    // bookkeeping cannot consume the same preparation frame.
    internal void PrepareTree()
    {
        var root = Root;
        if (root.Tree != null) throw new InvalidOperationException("The fragment tree was already prepared.");
        root.Tree = PartTree.CreateFromNewPartTree(root);
        if (!Ready) throw new InvalidOperationException("A prepared fragment unexpectedly entered the simulation.");
    }

    internal void TransferredTo(Vehicle vehicle)
    {
        if (Root.Tree?.OwningVehicle != vehicle)
            throw new InvalidOperationException("Native vehicle creation did not adopt its prepared fragment tree.");
        _root = null; // Native vehicle disposal owns every module from this point on.
    }

    public void Dispose()
    {
        var root = _root;
        _root = null;
        if (root == null) return;
        if (root.Tree is { } tree)
        {
            // A native constructor may have taken ownership before throwing. The
            // commit's vehicle rollback must dispose that tree, never this lease.
            if (!tree.IsDisposed && tree.OwningVehicle == null) tree.Dispose();
        }
        else root.Dispose();
    }
}
