using KSA;

namespace Purr2D2.Rendering;

/// <summary>Register all generated identities and resolve all references before native initialization.</summary>
internal sealed class FragmentAssetRegistration
{
    // Published identities must not be reused as prepared templates while their
    // bundle is incomplete (including an interrupted or failed registration).
    private static readonly HashSet<SerializedId> Incomplete = new(ReferenceEqualityComparer.Instance);
    private readonly AssetBundle _bundle;
    private readonly KSA.Mod _mod;
    private int _registered, _initialized;
    private bool _resolved;
    internal bool Done => _initialized == _bundle.Assets.Count && _resolved;
    internal string Step => _registered < _bundle.Assets.Count ? "template registration"
        : !_resolved ? "template reference resolution" : "template initialization";

    internal FragmentAssetRegistration(AssetBundle bundle, KSA.Mod mod)
    {
        _bundle = bundle;
        _mod = mod;
        foreach (var asset in bundle.Assets) Incomplete.Add(asset);
    }

    internal void AdvanceOne()
    {
        if (Done) return;
        if (_registered < _bundle.Assets.Count)
        {
            Register(_bundle.Assets[_registered++]);
            return;
        }
        if (!_resolved)
        {
            ResolveReferences(_bundle);
            _resolved = true;
            return;
        }
        var asset = _bundle.Assets[_initialized];
        asset.OnDataLoad(_mod);
        if (asset is PartTemplate part) FragmentTemplateLoader.HideFromEditor(part);
        _initialized++;
        if (Done) foreach (var completed in _bundle.Assets) Incomplete.Remove(completed);
    }

    /// <summary>The 5801 public content collections replace removed Mod.Register overloads.</summary>
    internal static void Register(SerializedId asset)
    {
        asset.SetHash();
        var bindings = ModLibrary.TagTable.Bindings;
        var binding = bindings.FirstOrDefault(b => b.Type == asset.GetType());
        if (binding.Collection == null)
            binding = bindings.FirstOrDefault(b => b.Collection.ItemType.IsInstanceOfType(asset));
        if (binding.Collection == null)
            throw new InvalidOperationException("No native content collection holds generated " + asset.GetType().Name + ".");
        if (!binding.Collection.TryRegister(asset, out var existing))
            throw new InvalidOperationException($"Generated asset '{asset.Id}' could not be registered ({existing?.Id ?? "missing ID"}). Restart to restore the complete durable bundle.");
    }

    private static void ResolveReferences(AssetBundle bundle)
    {
        // This adapter accepts exactly our exported inert dialect. Keep its full
        // reference inventory explicit instead of reflecting arbitrary world state.
        foreach (var asset in bundle.Assets)
        {
            if (asset is not PartTemplate part)
                throw new InvalidDataException("Unexpected generated runtime asset: " + asset.GetType().Name);
            foreach (var placement in part.SubPartInstances)
            {
                if (string.IsNullOrEmpty(placement.Key)) throw new InvalidDataException("Generated subpart has no placement Key.");
                Resolve(placement, bundle);
            }
            foreach (var tag in part.EditorTagRefs) Resolve(tag, bundle);
            foreach (var component in part.Components)
                switch (component)
                {
                    case PartModelModule.Template model:
                        Resolve(model.Mesh, bundle); Resolve(model.Material, bundle); break;
                    case PartModelGlassModule.Template glass:
                        Resolve(glass.Mesh, bundle); Resolve(glass.Material, bundle); break;
                    case MeshViewModule.Template picker:
                        Resolve(picker.MeshView, bundle); break;
                    case ColliderModule.Template colliders:
                        foreach (var collider in colliders.Colliders)
                            if (collider is MeshColliderTemplate mesh) Resolve(mesh.Mesh, bundle);
                        break;
                    default: throw new InvalidDataException("Unexpected generated module: " + component.GetType().Name);
                }
        }
    }

    internal static bool TryGetAsset<T>(string id, out T? target) where T : SerializedId
    {
        target = null;
        if (string.IsNullOrEmpty(id)) return false;
        var hash = KeyHash.Make(id);
        // Native TryGet<T> omits PartTemplate, MeshReference and EditorTagDefinition
        // in 5801. Use ContentRefLookup's public bound-collection contract.
        foreach (var binding in ModLibrary.TagTable.Bindings)
            if ((typeof(T).IsAssignableFrom(binding.Collection.ItemType) || binding.Collection.ItemType.IsAssignableFrom(typeof(T)))
                && binding.Collection.TryFind(hash, out var found) && found is T candidate
                && string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase) && !candidate.LoadFailed && !Incomplete.Contains(candidate))
            { target = candidate; return true; }
        return false;
    }

    private static void Resolve<T>(DataReference<T>? reference, AssetBundle bundle) where T : SerializedId
    {
        T? target = null;
        if (reference != null && !string.IsNullOrEmpty(reference.Ref))
        {
            target = bundle.Assets.OfType<T>().SingleOrDefault(a => string.Equals(a.Id, reference.Ref, StringComparison.OrdinalIgnoreCase));
            if (target == null) TryGetAsset(reference.Ref, out target);
        }
        if (target == null || target.LoadFailed)
            throw new InvalidDataException($"Generated {typeof(T).Name} reference '{reference?.Ref}' is unavailable. Restore its source mod and complete Generated/Slices directory.");
        reference!.Value = target;
    }
}
