using System;
using Unity.Netcode;
using UnityEngine;

// Shared deterministic 30 Hz simulation. No client position or deltaTime crosses the wire.
public static class PredictionSimulation
{
    public const int TickRate = 30;
    public const float Step = 1f / TickRate;
    public const float Speed = 5f;
    public const float DashDistance = 4f;
    public const int CooldownTicks = 90;
    public const float ArenaLimit = 10.5f;
    public static readonly Vector2 WallMin = new Vector2(1.5f, -2.5f);
    public static readonly Vector2 WallMax = new Vector2(2.5f, 2.5f);
    private const float Radius = 0.45f;

    public static bool IsFinite(Vector2 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y);

    public static MovementState Simulate(MovementState state, PlayerCommand command,
        bool deliberatelyWrongPrediction = false)
    {
        state.Sequence = command.Sequence;
        state.Cooldown = Mathf.Max(0, state.Cooldown - 1);
        var direction = IsFinite(command.Direction) ? Vector2.ClampMagnitude(command.Direction, 1f) : Vector2.zero;
        if (direction.sqrMagnitude > 0.01f) state.Facing = direction.normalized;
        var movement = direction * (Speed * Step);
        state.Position = Slide(state.Position, movement);
        if (!command.Dash) return state;
        state.LastDashSequence = command.Sequence;
        var dash = state.Facing * DashDistance;
        if (deliberatelyWrongPrediction)
        {
            state.Position += dash * 1.8f;
            state.Cooldown = CooldownTicks;
            state.LastDashResult = 1;
        }
        else if (state.Cooldown > 0)
            state.LastDashResult = 2; // cooldown rejected
        else if (!ClearPath(state.Position, state.Position + dash))
            state.LastDashResult = 3; // obstacle or arena boundary rejected
        else
        {
            state.Position += dash;
            state.Cooldown = CooldownTicks;
            state.LastDashResult = 1;
        }
        return state;
    }

    public static bool ClearPath(Vector2 from, Vector2 to)
    {
        if (Mathf.Abs(to.x) > ArenaLimit || Mathf.Abs(to.y) > ArenaLimit) return false;
        // Sampled swept capsule footprint; spacing is smaller than the expanded wall thickness.
        int count = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to) / 0.1f));
        for (int i = 0; i <= count; i++)
            if (InWall(Vector2.Lerp(from, to, (float)i / count))) return false;
        return true;
    }

    private static Vector2 Slide(Vector2 from, Vector2 delta)
    {
        var x = from + new Vector2(delta.x, 0);
        if (ClearPath(from, x)) from = x;
        var z = from + new Vector2(0, delta.y);
        if (ClearPath(from, z)) from = z;
        return from;
    }

    private static bool InWall(Vector2 p) => p.x >= WallMin.x - Radius &&
        p.x <= WallMax.x + Radius && p.y >= WallMin.y - Radius && p.y <= WallMax.y + Radius;
}

public struct PlayerCommand : INetworkSerializable
{
    public int Sequence;
    public Vector2 Direction;
    public bool Dash;
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref Sequence);
        serializer.SerializeValue(ref Direction);
        serializer.SerializeValue(ref Dash);
    }
}

public struct MovementState : INetworkSerializable, IEquatable<MovementState>
{
    public int Sequence;
    public Vector2 Position;
    public Vector2 Facing;
    public int Cooldown;
    public int LastDashSequence;
    public byte LastDashResult;
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref Sequence);
        serializer.SerializeValue(ref Position);
        serializer.SerializeValue(ref Facing);
        serializer.SerializeValue(ref Cooldown);
        serializer.SerializeValue(ref LastDashSequence);
        serializer.SerializeValue(ref LastDashResult);
    }
    public bool Equals(MovementState other) => Sequence == other.Sequence && Position == other.Position &&
        Facing == other.Facing && Cooldown == other.Cooldown && LastDashSequence == other.LastDashSequence &&
        LastDashResult == other.LastDashResult;
}
