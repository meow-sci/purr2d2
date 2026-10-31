using Brutal.Numerics;
using HarmonyLib;
using KSA;
using KSA.Deformation;
using Purr2D2.Rendering;

namespace Purr2D2.Force;

/// <summary>Native dent fields on massless subparts, restored by ordinary KSA vessel saves.</summary>
internal static class ForceDebrisDeformation
{
    internal static void Install(Harmony harmony) => harmony.Patch(
        AccessTools.Method(typeof(FxDeformation), nameof(FxDeformation.CreateComponents)),
        postfix: new HarmonyMethod(typeof(ForceDebrisDeformation), nameof(CreateCarrierModule)));

    internal static bool IsCarrier(string id)
    {
        int marker = id.LastIndexOf("_dents", StringComparison.Ordinal);
        return id.StartsWith(AuthoredDebrisAssets.Prefix, StringComparison.Ordinal)
            && marker >= AuthoredDebrisAssets.Prefix.Length
            && int.TryParse(id.AsSpan(marker + 6), out int index) && index >= 0;
    }

    // Must run during construction, before subtree module aggregation and state creation.
    // Native FxDeformation normally skips subparts, so saves need this same hook on reload.
    internal static void CreateCarrierModule(Part part, PartTemplate template)
    {
        if (template.IsSubPart && IsCarrier(template.Id) && part.Modules.Get<FxDeformation>().IsEmpty)
            part.Modules.Add(new FxDeformation { Parent = part });
    }

    internal static AuthoredDebrisDeformation? Capture(Part part, double4x4 matrix)
    {
        var modules = part.Modules.Get<FxDeformation>();
        if (modules.IsEmpty) return null;
        var tree = part.Tree ?? throw new InvalidOperationException("A source dent field has no part tree.");
        if (!FxDeformation.TryGetFrom(tree.States, out var states)) return null;
        if (modules.Length != 1) throw new InvalidDataException("A source has ambiguous dent fields.");
        ref readonly var state = ref states.GetState(modules[0]);
        if (state.Count == 0) return null;
        if (state.Count < 0 || state.Count > FxDeformation.MaxDentsCeiling
            || !float.IsFinite(state.Scale) || state.Scale < 0
            || !double4x4.Decompose(matrix, out var scale, out var rotation, out var position)
            || !ForceMath.Finite(position) || !ForceMath.Finite(scale) || scale.X <= 0
            || Math.Abs(scale.Y - scale.X) > 1e-6 || Math.Abs(scale.Z - scale.X) > 1e-6
            || rotation.NormalizedOrZero() == default)
            throw new InvalidDataException("A source dent field has invalid state or transform.");
        var dents = new Dent[state.Count];
        for (int i = 0; i < dents.Length; i++)
        {
            var dent = state.Get(i);
            if (!ForceMath.Finite(double3.Unpack(dent.Center)) || !ForceMath.Finite(double3.Unpack(dent.Direction))
                || dent.Direction.LengthSquared() <= 0 || !float.IsFinite(dent.Radius) || dent.Radius <= 0
                || !float.IsFinite(dent.Depth) || dent.Depth <= 0)
                throw new InvalidDataException("A source dent field contains invalid dents.");
            dents[i] = dent;
        }
        return new(position, rotation.NormalizedOrZero(), scale, dents, state.Scale);
    }

    internal static bool Matches(Part part, AuthoredDebrisDeformation? captured, bool nativeRoundTrip = false)
    {
        var modules = part.Modules.Get<FxDeformation>();
        if (part.Tree is not { } tree) return false;
        if (modules.IsEmpty || !FxDeformation.TryGetFrom(tree.States, out var states)) return captured == null;
        if (modules.Length != 1) return false;
        ref readonly var state = ref states.GetState(modules[0]);
        if (captured == null) return state.Count == 0;
        if (state.Count != captured.Dents.Length || state.Scale != captured.EffectiveScale) return false;
        for (int i = 0; i < state.Count; i++)
        {
            var actual = state.Get(i);
            var expected = captured.Dents[i];
            // ApplySaveData normalizes directions again, as it does on ordinary reload.
            if (actual.Center != expected.Center || actual.Radius != expected.Radius || actual.Depth != expected.Depth
                || (actual.Direction != expected.Direction
                    && (!nativeRoundTrip || (actual.Direction - expected.Direction).LengthSquared() > 1e-12f))) return false;
        }
        return true;
    }

    internal static void Restore(Part carrier, AuthoredDebrisDeformation field)
    {
        var modules = carrier.Modules.Get<FxDeformation>();
        if (!IsCarrier(carrier.Template.Id) || modules.Length != 1)
            throw new InvalidOperationException("A debris dent carrier was not prepared.");
        var data = new FxDeformation.SaveData { InstanceOf = FxDeformation.TEMPLATE_ID };
        foreach (var dent in field.Dents)
            data.Dents.Add(new FxDeformation.DentData
            {
                X = dent.Center.X, Y = dent.Center.Y, Z = dent.Center.Z,
                DX = dent.Direction.X, DY = dent.Direction.Y, DZ = dent.Direction.Z,
                R = dent.Radius, D = dent.Depth
            });
        // This populates the private dent list as well as render/solver state and
        // DentedPartCount. Writing only State would lose damage on the next tick/save.
        var tree = carrier.Tree ?? throw new InvalidOperationException("A debris dent carrier has no part tree.");
        modules[0].ApplySaveData(data, tree.States);
        if (!Matches(carrier, field, nativeRoundTrip: true))
            throw new InvalidOperationException("The native dent settings changed during breakup preparation.");
    }
}
