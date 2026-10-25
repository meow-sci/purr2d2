using System;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Capes;

/// <summary>
/// A cosmetic cape simulated by KSA's native Bepu cloth solver. Each instance owns
/// its own cloth state and Bepu simulation; it does not join the parachute pool.
/// </summary>
public sealed class CapeCloth : IDisposable
{
    private const float FixedStep = 1f / 120f;
    private const float MaxFrameDt = .1f;
    private const int MaxSubstepsPerUpdate = 4;
    private const float MaxLocalSpeed = 8f;
    private const float AirflowResponse = .5f;
    // Partial lift always leaves a downward pull, including at maximum airflow.
    private const float MaximumLift = 5.5f;
    private const float LiftPerSpeed = 1.8333334f;
    // Chest_M is about .431m above the stock kitten's mesh sole/ground level in bind pose.
    private const float HemFloorY = -.43f;

    private static readonly Lazy<ChuteClothTopology> SharedTopology = new(BuildTopology);

    private readonly ChuteClothTopology _topology;
    private readonly ChuteClothState _state;
    private float _accumulator;
    private ulong _poseRevision;
    private bool _disposed;

    public CapeCloth()
    {
        _topology = SharedTopology.Value;
        _state = CreateState(_topology);
    }

    public ReadOnlySpan<float3> Positions => _state.Positions;

    /// <summary>Changes only when a solver result or rest restore changes the rendered pose.</summary>
    public ulong PoseRevision => _poseRevision;

    public string? Failure { get; private set; }

    /// <summary>Advances the cape in local coordinates, with +Z pointing forward.</summary>
    public void Step(double playerDt, float3 localVelocity)
    {
        if (_disposed || Failure is not null || !double.IsFinite(playerDt) || playerDt <= 0d)
        {
            return;
        }

        float frameDt = (float)Math.Min(playerDt, MaxFrameDt);
        if (!float.IsFinite(frameDt) || frameDt <= 0f) return;

        float3 velocity = ClampFiniteVelocity(localVelocity);
        _state.AirVelocityLocal = velocity * AirflowResponse;
        _state.QLocal = _state.Tuning.ReferenceQForDrag;
        float horizontalSpeed = MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
        float lift = MathF.Min(MaximumLift, LiftPerSpeed * horizontalSpeed);
        _state.GravityLocal = new float3(0f, -9.81f + lift, 0f);

        _accumulator = MathF.Min(_accumulator + frameDt, MaxFrameDt);
        int steps = 0;
        while (_accumulator >= FixedStep && steps < MaxSubstepsPerUpdate)
        {
            _accumulator -= FixedStep;
            steps++;
        }

        if (steps > 0)
        {
            try
            {
                ChuteBepuClothSolver.StepDynamic(_topology, _state, steps * FixedStep);
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
                return;
            }

            if (_state.Bepu?.Poisoned == true)
            {
                Fail("KSA's cloth solver marked the cape simulation as failed.");
                return;
            }

            if (!ProjectSafeBounds())
            {
                Fail("KSA's cloth solver produced a non-finite cape position.");
                return;
            }

            AdvancePoseRevision();
        }

        // Drop backlog instead of carrying a spiral of catch-up work into later frames.
        if (steps == MaxSubstepsPerUpdate && _accumulator >= FixedStep)
        {
            _accumulator = 0f;
        }
    }

    /// <summary>Returns the cape to its authored pose and releases its private simulation.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { ReleaseSimulation(); }
        finally
        {
            _accumulator = 0f;
            Failure = null;
            RestoreRestPose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ReleaseSimulation(); }
        finally { _accumulator = 0f; }
    }

    private bool ProjectSafeBounds()
    {
        bool changed = false;
        for (int i = 0; i < CapeGeometry.NodeCount; i++)
        {
            if (_state.InvMass[i] == 0f) continue;

            float3 position = _state.Positions[i];
            if (!Finite(position)) return false;

            float3 velocity = _state.Velocities[i];

            // Preserve the authored shoulder curve without pulling the short
            // first-row links away from their fixed pins.
            float backCollisionLimit = CapeRestPose.BackCollisionLimitZ(i);
            if (position.Z > backCollisionLimit)
            {
                position.Z = backCollisionLimit;
                if (velocity.Z > 0f) velocity.Z = 0f;
                changed = true;
            }
            if (position.Y < HemFloorY)
            {
                position.Y = HemFloorY;
                if (velocity.Y < 0f) velocity.Y = 0f;
                changed = true;
            }

            _state.Positions[i] = position;
            _state.Velocities[i] = velocity;
        }

        if (changed) _state.PositionsDirty = true;
        return true;
    }

    private void Fail(string message)
    {
        Failure = string.IsNullOrWhiteSpace(message) ? "Cape cloth simulation failed." : message;
        try { ReleaseSimulation(); }
        finally
        {
            _accumulator = 0f;
            RestoreRestPose();
        }
    }

    private void ReleaseSimulation()
    {
        ChuteBepuClothState? simulation = _state.Bepu;
        _state.Bepu = null;
        simulation?.Dispose();
    }

    private void RestoreRestPose()
    {
        bool poseChanged = false;
        for (int i = 0; i < CapeGeometry.NodeCount; i++)
        {
            float3 rest = CapeRestPose.Position(i);
            float3 position = _state.Positions[i];
            if (position.X != rest.X || position.Y != rest.Y || position.Z != rest.Z)
                poseChanged = true;
            _state.Positions[i] = rest;
            _state.AttractorTargets[i] = rest;
            _state.Velocities[i] = float3.Zero;
        }
        if (poseChanged)
        {
            _state.PositionsDirty = true;
            AdvancePoseRevision();
        }
        _state.AirVelocityLocal = float3.Zero;
        _state.GravityLocal = new float3(0f, -9.81f, 0f);
        _state.QLocal = 0f;
    }

    private void AdvancePoseRevision()
    {
        if (_poseRevision < ulong.MaxValue) _poseRevision++;
    }

    private static ChuteClothState CreateState(ChuteClothTopology topology)
    {
        var state = new ChuteClothState
        {
            Positions = new float3[CapeGeometry.NodeCount],
            Velocities = new float3[CapeGeometry.NodeCount],
            AttractorTargets = new float3[CapeGeometry.NodeCount],
            InvMass = new float[CapeGeometry.NodeCount],
            RestLengths = new float[topology.ConstraintA.Length],
            EnvironmentEntries = Array.Empty<ChuteEnvironmentEntry>(),
            EnvironmentEntryCount = 0,
            BoneDeltas = new floatQuat[33],
            Fill = 0f,
            QLocal = 0f,
            PhysicsWeight = 1f,
            GravityLocal = new float3(0f, -9.81f, 0f),
            AirVelocityLocal = float3.Zero,
            MaxSubstepDisplacement = .1f,
            MaxPinDistance = 1.8f,
            BakedDiameter = 1f,
            BakedLineLength = 0f,
            Tuning = CapeTuning()
        };

        for (int i = 0; i < CapeGeometry.NodeCount; i++)
        {
            float3 rest = CapeRestPose.Position(i);
            state.Positions[i] = rest;
            state.AttractorTargets[i] = rest;
            state.InvMass[i] = i / CapeGeometry.Columns == 0 ? 0f : 1f;
        }
        for (int i = 0; i < topology.ConstraintA.Length; i++)
        {
            int a = topology.ConstraintA[i];
            int b = topology.ConstraintB[i];
            state.RestLengths[i] = (state.Positions[a] - state.Positions[b]).Length();
        }
        Array.Fill(state.BoneDeltas, floatQuat.Identity);
        state.PositionsDirty = true;
        return state;
    }

    private static ChuteClothTuning CapeTuning()
    {
        ChuteClothTuning tuning = ChuteClothTuning.Default;
        tuning.AttractorGain = 0f;
        tuning.AttractorDamping = 0f;
        tuning.PressureGain = 0f;
        tuning.MinInflatedAccel = 0f;
        tuning.AeroGain = 0f;
        tuning.DampingRate = .5f;
        tuning.AirDragRate = 5f;
        tuning.ReferenceQForDrag = 1f;
        tuning.SubstepDisplacementFraction = .1f;
        tuning.PinDistanceFactor = 1.8f;
        tuning.BepuStructuralHz = 40f;
        tuning.BepuShearHz = 10f;
        tuning.BepuBendHz = 2f;
        tuning.BepuDampingRatio = 2f;
        tuning.BepuNodeRadius = .01f;
        tuning.BepuLineNodeRadius = .005f;
        tuning.BepuVelocityIterations = 4f;
        tuning.BepuSelfCollision = false;
        tuning.BepuContactFriction = 0f;
        return tuning;
    }

    private static ChuteClothTopology BuildTopology()
    {
        var topology = new ChuteClothTopology
        {
            NodeCount = CapeGeometry.NodeCount,
            CanopyNodeCount = CapeGeometry.NodeCount,
            ApexIndex = 0,
            PinIndex = 4,
            ConfluenceIndex = 0,
            LineChains = 0,
            LineSegmentsPerChain = 0,
            LineChainNodeIndices = Array.Empty<int>(),
            RiserNodeIndices = Array.Empty<int>()
        };

        int capacity = (CapeGeometry.Rows - 1) * (CapeGeometry.Columns - 1) // horizontal structural
            + (CapeGeometry.Rows - 1) * CapeGeometry.Columns // vertical structural
            + 2 * (CapeGeometry.Rows - 1) * (CapeGeometry.Columns - 1) // diagonal shear
            + CapeGeometry.Rows * (CapeGeometry.Columns - 2) - (CapeGeometry.Columns - 2) // horizontal bend, less pinned row
            + (CapeGeometry.Rows - 2) * CapeGeometry.Columns; // vertical bend
        var aNodes = new int[capacity];
        var bNodes = new int[capacity];
        var restNormalized = new float[capacity];
        var compliance = new float[capacity];
        int count = 0;

        void Add(int a, int b, float linkCompliance)
        {
            bool aPinned = a / CapeGeometry.Columns == 0;
            bool bPinned = b / CapeGeometry.Columns == 0;
            if (aPinned && bPinned) return;

            float3 delta = CapeRestPose.Position(a) - CapeRestPose.Position(b);
            aNodes[count] = a;
            bNodes[count] = b;
            restNormalized[count] = delta.Length(); // BakedDiameter is one metre.
            compliance[count] = linkCompliance;
            count++;
        }

        const float structural = 1e-7f;
        const float shear = .002f;
        const float bend = .004f;

        // Structural links.
        for (int row = 0; row < CapeGeometry.Rows; row++)
        for (int column = 0; column < CapeGeometry.Columns - 1; column++)
            Add(row * CapeGeometry.Columns + column, row * CapeGeometry.Columns + column + 1, structural);
        for (int row = 0; row < CapeGeometry.Rows - 1; row++)
        for (int column = 0; column < CapeGeometry.Columns; column++)
            Add(row * CapeGeometry.Columns + column, (row + 1) * CapeGeometry.Columns + column, structural);

        // Both diagonals in every cell resist shear.
        for (int row = 0; row < CapeGeometry.Rows - 1; row++)
        for (int column = 0; column < CapeGeometry.Columns - 1; column++)
        {
            int topLeft = row * CapeGeometry.Columns + column;
            int topRight = topLeft + 1;
            int bottomLeft = topLeft + CapeGeometry.Columns;
            int bottomRight = bottomLeft + 1;
            Add(topLeft, bottomRight, shear);
            Add(topRight, bottomLeft, shear);
        }

        // Two-step horizontal and vertical links resist broad folds.
        for (int row = 0; row < CapeGeometry.Rows; row++)
        for (int column = 0; column < CapeGeometry.Columns - 2; column++)
            Add(row * CapeGeometry.Columns + column, row * CapeGeometry.Columns + column + 2, bend);
        for (int row = 0; row < CapeGeometry.Rows - 2; row++)
        for (int column = 0; column < CapeGeometry.Columns; column++)
            Add(row * CapeGeometry.Columns + column, (row + 2) * CapeGeometry.Columns + column, bend);

        topology.ConstraintA = new int[count];
        topology.ConstraintB = new int[count];
        topology.RestLengthNormalized = new float[count];
        topology.Compliance = new float[count];
        Array.Copy(aNodes, topology.ConstraintA, count);
        Array.Copy(bNodes, topology.ConstraintB, count);
        Array.Copy(restNormalized, topology.RestLengthNormalized, count);
        Array.Copy(compliance, topology.Compliance, count);
        topology.LineLinkStartIndex = count;
        topology.RiserLinkStartIndex = count;
        return topology;
    }

    private static float3 ClampFiniteVelocity(float3 value)
    {
        if (!Finite(value)) return float3.Zero;
        double lengthSquared = (double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z;
        if (lengthSquared <= MaxLocalSpeed * MaxLocalSpeed) return value;
        float scale = (float)(MaxLocalSpeed / Math.Sqrt(lengthSquared));
        return value * scale;
    }

    private static bool Finite(float3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
