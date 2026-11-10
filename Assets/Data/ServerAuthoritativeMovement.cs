using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(NetworkObject))]
public class ServerAuthoritativeMovement : NetworkBehaviour
{
    public bool useClientSidePrediction = true;
    private readonly NetworkVariable<MovementState> replicated = new NetworkVariable<MovementState>();
    private readonly Queue<PlayerCommand> serverQueue = new Queue<PlayerCommand>();
    private readonly List<SavedMove> pending = new List<SavedMove>();
    private MovementState authority, predicted, remoteTarget;
    private int nextSequence, lastReceived, lastAcknowledged, lastDashSeen;
    private float accumulator;
    private bool dashQueued, faultQueued;
    private float dashPressedAt;
    private sealed class DashMeasurement
    {
        public float PressedAt, ResponseMs = -1, ResultMs = -1, SnapshotMs = -1;
        public byte Result;
    }
    private readonly Dictionary<int, DashMeasurement> dashMeasurements = new Dictionary<int, DashMeasurement>();
    private struct SavedMove
    {
        public PlayerCommand Command;
        public bool Predicted, Fault;
    }
    public static ServerAuthoritativeMovement Local { get; private set; }
    public int PendingCount => pending.Count;
    public int Acknowledged => lastAcknowledged;
    public int Corrections { get; private set; }
    public int Rejections { get; private set; }
    public float LastResponseMs { get; private set; }
    public float LastConfirmationMs { get; private set; }
    public string LastAction { get; private set; } = "Готов к движению";
    public float Cooldown => predicted.Cooldown * PredictionSimulation.Step;
    public Vector2 AuthoritativePosition => replicated.Value.Position;
    public Vector2 DisplayPosition => new Vector2(transform.position.x, transform.position.z);
    public int ServerAccepted { get; private set; }
    public int ServerRejected { get; private set; }

    public override void OnNetworkSpawn()
    {
        replicated.OnValueChanged += OnSnapshot;
        if (IsServer)
        {
            authority = new MovementState
            {
                Position = new Vector2(transform.position.x, transform.position.z),
                Facing = Vector2.up
            };
            replicated.Value = authority;
        }
        predicted = remoteTarget = replicated.Value;
        transform.position = World(predicted.Position);
        if (IsOwner)
        {
            Local = this;
            useClientSidePrediction = NetworkTopologyManager.Instance.PredictionEnabled;
        }
        gameObject.name = IsOwner ? "Player (local)" : "Player (remote)";
        var renderer = GetComponent<Renderer>();
        if (renderer != null)
        {
            var color = OwnerClientId == 0 ? new Color(1f, 0.5f, 0.1f) : new Color(0.1f, 0.4f, 1f);
            renderer.material.color = color;
        }
        LabTelemetry.Record("spawn", $"owner={OwnerClientId}; local={IsOwner}; server={IsServer}");
    }

    public override void OnNetworkDespawn()
    {
        replicated.OnValueChanged -= OnSnapshot;
        if (Local == this) Local = null;
    }

    private void Update()
    {
        if (!IsSpawned) return;
        if (!IsOwner)
        {
            transform.position = Vector3.Lerp(transform.position, World(remoteTarget.Position),
                1f - Mathf.Exp(-18f * Time.deltaTime));
            Face(remoteTarget.Facing);
            return;
        }
        if (Keyboard.current != null && !NetworkTopologyManager.Instance.Automated)
        {
            if (Keyboard.current.leftShiftKey.wasPressedThisFrame || Keyboard.current.spaceKey.wasPressedThisFrame)
                QueueDash(false);
            if (Keyboard.current.fKey.wasPressedThisFrame) QueueDash(true);
            if (Keyboard.current.pKey.wasPressedThisFrame)
                NetworkTopologyManager.Instance.TogglePrediction();
        }
        accumulator += Time.deltaTime;
        int steps = 0;
        while (accumulator >= PredictionSimulation.Step && steps++ < 4)
        {
            accumulator -= PredictionSimulation.Step;
            SendInput();
        }
        accumulator = Mathf.Min(accumulator, PredictionSimulation.Step * 4);
        transform.position = World(predicted.Position);
        Face(predicted.Facing);
    }

    private Vector2 ReadDirection()
    {
        if (NetworkTopologyManager.Instance.Automated)
            return NetworkTopologyManager.Instance.AutomationDirection;
        var k = Keyboard.current;
        if (k == null) return Vector2.zero;
        return Vector2.ClampMagnitude(new Vector2(
            ((k.dKey.isPressed || k.rightArrowKey.isPressed) ? 1 : 0) - ((k.aKey.isPressed || k.leftArrowKey.isPressed) ? 1 : 0),
            ((k.wKey.isPressed || k.upArrowKey.isPressed) ? 1 : 0) - ((k.sKey.isPressed || k.downArrowKey.isPressed) ? 1 : 0)), 1);
    }

    public void QueueDash(bool forceWrongPrediction)
    {
        if (!IsSpawned || !IsOwner) return;
        dashQueued = true;
        faultQueued |= forceWrongPrediction;
        dashPressedAt = Time.realtimeSinceStartup;
    }

    private void SendInput()
    {
        // Bounded saved-move buffer: disconnect rather than growing indefinitely on a dead connection.
        if (pending.Count >= 240)
        {
            NetworkTopologyManager.Instance.StopSession("Нет подтверждения сервера: очередь ввода переполнена.");
            return;
        }
        var command = new PlayerCommand { Sequence = ++nextSequence, Direction = ReadDirection(), Dash = dashQueued };
        var saved = new SavedMove { Command = command, Predicted = useClientSidePrediction, Fault = faultQueued };
        pending.Add(saved);
        if (command.Dash) dashMeasurements[command.Sequence] = new DashMeasurement { PressedAt = dashPressedAt };
        if (useClientSidePrediction)
        {
            predicted = PredictionSimulation.Simulate(predicted, command, faultQueued);
            if (command.Dash && predicted.LastDashResult == 1)
                MeasureResponse(command.Sequence, dashMeasurements[command.Sequence], "predicted");
        }
        if (command.Dash)
        {
            LastAction = faultQueued ? "F: локальная ошибка, ждём коррекцию" : "Рывок отправлен серверу";
            LabTelemetry.Record("dash_input", $"seq={command.Sequence}; csp={useClientSidePrediction}; fault={faultQueued}");
        }
        SubmitInputServerRpc(command);
        dashQueued = faultQueued = false;
    }

    [ServerRpc]
    private void SubmitInputServerRpc(PlayerCommand command, ServerRpcParams rpc = default)
    {
        // NGO additionally enforces ownership. Only input is accepted, never coordinates or dt.
        if (rpc.Receive.SenderClientId != OwnerClientId || command.Sequence != lastReceived + 1 ||
            !PredictionSimulation.IsFinite(command.Direction) || command.Direction.sqrMagnitude > 1.01f ||
            serverQueue.Count >= 240)
        {
            LabTelemetry.Record("invalid_input", $"owner={OwnerClientId}; seq={command.Sequence}");
            return;
        }
        lastReceived = command.Sequence;
        serverQueue.Enqueue(command);
    }

    private void FixedUpdate()
    {
        if (!IsSpawned || !IsServer || serverQueue.Count == 0) return;
        // Exactly one fixed-size step per server tick: a client cannot accelerate time by RPC spam.
        var command = serverQueue.Dequeue();
        authority = PredictionSimulation.Simulate(authority, command);
        if (command.Dash)
        {
            if (authority.LastDashResult == 1) ServerAccepted++; else ServerRejected++;
            LabTelemetry.Record("server_dash", $"owner={OwnerClientId}; seq={command.Sequence}; result={authority.LastDashResult}; pos={authority.Position}");
            // A NetworkVariable can coalesce multiple states during loss. Deliver every discrete
            // action result reliably to its owner, separately from the latest movement snapshot.
            DashResultClientRpc(command.Sequence, authority.LastDashResult,
                new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } } });
        }
        replicated.Value = authority;
        // OnValueChanged executes locally on the host as well.
    }

    private void OnSnapshot(MovementState previous, MovementState state)
    {
        remoteTarget = state;
        if (!IsOwner || state.Sequence <= lastAcknowledged) return;
        lastAcknowledged = state.Sequence;
        var before = predicted.Position;
        pending.RemoveAll(move => move.Command.Sequence <= state.Sequence);
        predicted = state;
        // Restore the complete authoritative state, including cooldown, then replay saved input.
        foreach (var move in pending)
            if (move.Predicted) predicted = PredictionSimulation.Simulate(predicted, move.Command, move.Fault);
        float error = Vector2.Distance(before, predicted.Position);
        if (useClientSidePrediction && error > 0.05f)
        {
            Corrections++;
            LabTelemetry.Record("reconcile", $"ack={state.Sequence}; error={error:F3}; replay={pending.Count}");
        }
        if (state.LastDashSequence > lastDashSeen)
        {
            lastDashSeen = state.LastDashSequence;
            LastAction = state.LastDashResult == 1 ? "Сервер: рывок разрешён" :
                state.LastDashResult == 2 ? "Сервер: отказ — кулдаун" : "Сервер: отказ — стена / граница";
        }
        // ACK may advance past several dashes in one snapshot; measure each saved action.
        foreach (var pair in new List<KeyValuePair<int, DashMeasurement>>(dashMeasurements))
        {
            if (pair.Key > state.Sequence || pair.Value.SnapshotMs >= 0) continue;
            pair.Value.SnapshotMs = (Time.realtimeSinceStartup - pair.Value.PressedAt) * 1000;
            if (pair.Value.ResponseMs < 0) MeasureResponse(pair.Key, pair.Value, "server_response");
            CompleteMeasurement(pair.Key, pair.Value);
        }
    }

    [ClientRpc]
    private void DashResultClientRpc(int sequence, byte result, ClientRpcParams target = default)
    {
        if (!IsOwner || !dashMeasurements.TryGetValue(sequence, out var measurement)) return;
        measurement.Result = result;
        measurement.ResultMs = (Time.realtimeSinceStartup - measurement.PressedAt) * 1000;
        if (result != 1) Rejections++;
        CompleteMeasurement(sequence, measurement);
    }

    private void CompleteMeasurement(int sequence, DashMeasurement measurement)
    {
        if (measurement.Result == 0 || measurement.SnapshotMs < 0) return;
        LastConfirmationMs = Mathf.Max(measurement.ResultMs, measurement.SnapshotMs);
        LastResponseMs = measurement.ResponseMs;
        LabTelemetry.Record("dash_confirmed", $"seq={sequence}; result={measurement.Result}; response_ms={LastResponseMs:F1}; confirmation_ms={LastConfirmationMs:F1}");
        dashMeasurements.Remove(sequence);
    }

    private void MeasureResponse(int sequence, DashMeasurement measurement, string reason)
    {
        measurement.ResponseMs = (Time.realtimeSinceStartup - measurement.PressedAt) * 1000;
        LastResponseMs = measurement.ResponseMs;
        LabTelemetry.Record("visual_response", $"seq={sequence}; source={reason}; ms={LastResponseMs:F1}");
    }

    public void SetPrediction(bool enabled)
    {
        useClientSidePrediction = enabled;
        LabTelemetry.Record("prediction_mode", enabled.ToString());
    }
    private static Vector3 World(Vector2 p) => new Vector3(p.x, 1f, p.y);
    private void Face(Vector2 facing)
    {
        if (facing.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(new Vector3(facing.x, 0, facing.y));
    }
}
