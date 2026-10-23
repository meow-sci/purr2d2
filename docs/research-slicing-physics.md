# Arbitrary slice physics and persistence

The slice geometry is authored in the original vehicle's assembly frame in physical metres. Every source subpart transform, including scale, is baked before clipping. `SlicePrincipalFrame` recentres each closed component on its exact volume centroid, diagonalizes its complete unit-density inertia tensor, and rotates the geometry into a proper right-handed principal frame. Its diagonal specific moments are persisted in native `CustomMass` XML. This preserves offdiagonal inertia through ordinary native save/load without a runtime-only mass override.

`NativeSlicePhysics.ValidateSource` accepts finite positive inert mass on a native-physics vessel, including occupied vessels. Tanks, service modules and capsules can become inert wreckage; their functional modules are discarded. Existing dents and deployed parachutes are refused. External connections must join actual full parts on unique parent-child boundaries; reinforced links, subpart endpoints and non-tree graphs are refused. Work and native fragment counts are bounded.

`TryReplace` is synchronous and must run at the joined simulation handoff after input events, before new workers dispatch. It requires two to eight registered, GPU-ready, durably written fragment templates and their already constructed native Part/PartTree instances. Lazy convex-hull cooking, module construction and detached tree preparation run on separate earlier preparation frames while the source remains intact. Commit checks total mass against the source inert mass, then transfers each single-use instance to its native vehicle.

After the full swing and fragment preparation, the source is isolated with stock `Vehicle.Split` calls, preserving attached neighboring branches as their own native vessels. The original external boundaries are captured before mutation: the parent is split first, then children are split from the smaller current owner. Touched owning trees use nested-safe `BeginBulkChange` / `EndBulkChange` scopes that all close before the hook returns. The final replacement occurs on a later settled update, after native derived-data flushing and worker preparation. The source remains a live intact native part in that interval; normal simulation can move separated vessels. Cancelling retains them rather than reconstructing native connections. The old target transform and its new isolated transform give the original-assembly to isolated-assembly mapping; no source scale is applied twice. Source capture and hit coordinates are rebased for that known isolation, then revalidated before replacement. Final pose, velocity and inertia are read from the live isolated owner at the swap, so the intervening physics step is not overwritten with stale motion. `Vehicle.CreateVehicle` adopts each prepared root/tree, and a narrow delegate to native `AdoptSeparatedVehicle` establishes bubble ownership and collision-avoidance links. This follows the native debris path without its repeated Part construction or temporary box-inertia override. Exact durable template inertia is present from construction, so a second full configuration pass is unnecessary. Source tank contents are drained after isolation; native mass/collision refresh preserves the source assembly pose. Sources without stores skip that work.

After creation, momenta are solved in parent CCI coordinates rather than rotating CCF physics coordinates. Let the original COM, velocity and angular momentum be `c0`, `v0`, and `L0`; let the generated group's COM be `c1`. With total momentum `P`, the group's spin must satisfy:

```text
L_spin = L0 - (c1 - c0) cross P
omega = inverse(I_group) * L_spin
v_i = P / M + omega cross (r_i - c1)
```

`I_group` includes each rotated fragment inertia plus its parallel-axis contribution. Final CCI velocities are converted back through native CCI-to-CCF helpers where needed, preserving the existing bubble origin. A gentle opening velocity is balanced to preserve group momentum. The fragment's cached `BodyRates` is synchronized through its exact private setter so a same-frame native save records the corrected angular rate. Flight-plan and navigation refresh happens once after this solve; no explosion is introduced.

Only after every fragment exists with valid mass and kinematics is the isolated source deleted with `Universe.DestroyVehicle` and `CrewDisposition.Kill`, matching native destructive events. Crew in separated neighboring branches is not removed. Before that deletion, a failure destroys newly created fragment vehicles and retains the source. Native isolation itself is not rolled back: a later failure can leave the original part intact in a separated vessel with an explicit diagnostic. Once source disposal has begun, replacements are retained even if native teardown reports an error; deleting them would lose the already removed geometry.

Uniform-density slicing conserves total source mass but can change the model's mass distribution relative to an authored primitive mass shape. Visual geometry is held fixed, so its new group COM can differ from the original physical COM. The momentum solve explicitly includes that displacement; it cannot simultaneously keep both COM locations unchanged without moving geometry. Highly inconsistent mass models can therefore require a different angular velocity. Native limits reject invalid or excessive results.

Native collision uses a convex hull of all fragment positions. The visible mesh remains the exact closed clipped surface, including holes and concavities; its exact geometric inertia is retained. The collision proxy fills holes and concavities. This is a stated experimental limitation, not an assertion of exact collision geometry. A hollow cut tube can look hollow while its collision proxy remains solid.

Durability is owned by `RuntimeFragmentAssets`: immutable content-addressed GLB/XML files are written before commit, and native `PartInstance.InstanceOf` records their stable generated template IDs. Startup loads generated XML at `StarMapImmediateLoad`, before ordinary global file loading and binding and before native missing-part validation. Old generated assets must not be removed merely because the current save does not use them; another saved vessel or save can still reference those IDs. Moving a save to another installation requires the corresponding generated assets.

Verified current-source paths under `../ksa-game-assemblies/current/decomp/`:

| Source | Integration |
|---|---|
| `KSA/Part.cs`, `GetReferenceWithChildren` | Native saves reference `Template.Id`; generated geometry must have a durable registered template. |
| `KSA/PartInstance.cs`, `CollectMissingParts` | Generated IDs must exist before native saved-vessel validation. |
| `KSA/PartTemplate.cs`, `OnDataLoad` | Registers native generated templates and initializes their component references. |
| `KSA/CustomMassTemplate.cs`, `InertiaTemplate.cs` | Native durable principal inertia and mass. |
| `KSA/ConvexHullColliderTemplate.cs`, `MeshColliderTemplate.cs` | Lazy hull cooking; the first host primitive supplies hull positions. The exporter therefore provides a separate combined collision mesh. |
| `KSA/Vehicle.cs`, `Split`, `CreateVehicle`, `AdoptSeparatedVehicle`, `UpdateAfterPartTreeModification` | Native separation, adoption, collision links and mass refresh; checked against the stock debris route. |
| `KSA/PhysicsStates.cs`, `ReadOnlyPhysicsStates.cs` | CCI state extraction and rotating-frame conversion. |
| `KSA/VehicleReferenceFrameEx.cs` | Native CCI-to-CCF position/velocity conversion. |
| `KSA/FxDeformation.cs` | Live dent-state guard. |
| `KSA/Universe.cs`, `DestroyVehicle` | Final source removal without native failure/debris/explosion behavior. |
| `KSA/Mod.cs`, `ModLibrary.cs`, `FileReference.cs`, `XmlLoader.cs` | Early generated asset registration and later native loader/binder lifecycle. |

`tests/SlicingPhysicsChecks.cs` verifies principal-axis reconstruction, centroid transforms, positive-inertia rejection, complete parallel-axis products, and conserved linear/angular momentum when the geometric COM shifts. Native GPU, collision, structural mutation and save restoration require the documented in-game acceptance pass; they cannot be executed on the current macOS ARM host.
