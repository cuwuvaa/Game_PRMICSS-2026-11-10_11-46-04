using System.Net;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

// Session setup only. Movement and reconciliation live in ServerAuthoritativeMovement.
[RequireComponent(typeof(NetworkManager), typeof(UnityTransport))]
public class NetworkTopologyManager : MonoBehaviour
{
    public static NetworkTopologyManager Instance { get; private set; }
    public bool PredictionEnabled { get; private set; } = true;
    public int Profile { get; set; }
    public string Address = "127.0.0.1";
    public string Port = "7777";
    public string Status { get; private set; } = "Сначала запусти сервер, затем клиент во втором окне.";
    public string Role { get; private set; } = "offline";
    public NetworkManager Manager { get; private set; }
    public bool Automated => automation != null && automation.Enabled;
    public Vector2 AutomationDirection => automation != null ? automation.Direction : Vector2.zero;
    private DemoAutomation automation;
    private GameObject world;

    private void Awake()
    {
        Instance = this;
        Application.runInBackground = true;
        Application.targetFrameRate = 60;
        Time.fixedDeltaTime = PredictionSimulation.Step;
        Manager = GetComponent<NetworkManager>();
        world = GameObject.Find("DemoWorld");
        world.SetActive(false);
        Profile = int.TryParse(LabTelemetry.Argument("-labProfile", "0"), out int p) ? Mathf.Clamp(p, 0, 2) : 0;
        Address = LabTelemetry.Argument("-labAddress", Address);
        Port = LabTelemetry.Argument("-labPort", Port);
        PredictionEnabled = LabTelemetry.Argument("-labScenario") != "baseline";
        automation = gameObject.AddComponent<DemoAutomation>();
        gameObject.AddComponent<DemoUI>();
    }

    private void Start()
    {
        Manager.OnClientConnectedCallback += OnConnected;
        Manager.OnClientDisconnectCallback += OnDisconnected;
        Manager.OnTransportFailure += OnTransportFailure;
        string role = LabTelemetry.Argument("-labRole", "offline");
        if (role == "host" || role == "server" || role == "client") StartSession(role);
    }

    public void StartSession(string role)
    {
        if (Manager.IsListening || Manager.ShutdownInProgress) return;
        if (!ushort.TryParse(Port, out ushort port) || port == 0)
        { Status = "Порт должен быть от 1 до 65535."; return; }
        if (!IPAddress.TryParse(Address.Trim(), out _))
        { Status = "Введи IP сервера, например 127.0.0.1."; return; }
        Role = role;
        Status = "Подключение...";
        LabTelemetry.Open(role);
        UnityTransport.s_DriverConstructor = new LabNetworkSimulator(Profile);
        GetComponent<UnityTransport>().SetConnectionData(Address.Trim(), port, "0.0.0.0");
        Manager.NetworkConfig.ConnectionApproval = true;
        Manager.ConnectionApprovalCallback = Approve;
        bool started = role == "host" ? Manager.StartHost() : role == "server" ? Manager.StartServer() : Manager.StartClient();
        if (!started) Status = "Не удалось запустить сессию. Проверь IP и порт.";
        if (role == "server" && started) Status = "Выделенный сервер запущен.";
        world.SetActive(started);
        if (started) automation.Begin(role);
    }

    private void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // Find a free spawn slot even after another player disconnects.
        var used = new bool[8];
        foreach (int occupied in slots.Values) used[occupied] = true;
        int slot = System.Array.FindIndex(used, value => !value);
        response.Approved = slot >= 0;
        response.CreatePlayerObject = response.Approved;
        if (slot >= 0)
        {
            slots[request.ClientNetworkId] = slot;
            response.Position = new Vector3(-5f + (slot % 2) * 2f, 1, -3f + (slot / 2) * 2f);
        }
        else response.Reason = "Максимум 8 игроков.";
        response.Rotation = Quaternion.identity;
        response.Pending = false;
    }
    private readonly System.Collections.Generic.Dictionary<ulong, int> slots = new System.Collections.Generic.Dictionary<ulong, int>();

    private void OnConnected(ulong id)
    {
        Status = "Подключено. Сервер проверяет движение и рывки.";
        LabTelemetry.Record("connected", $"id={id}; count={(Manager.IsServer ? Manager.ConnectedClients.Count : 0)}");
        if (id == Manager.LocalClientId) automation.Connected();
    }
    private void OnDisconnected(ulong id)
    {
        slots.Remove(id);
        LabTelemetry.Record("disconnected", $"id={id}; reason={Manager.DisconnectReason}");
        if (id == Manager.LocalClientId) Status = "Соединение закрыто. Можно подключиться снова.";
    }
    private void OnTransportFailure()
    {
        Status = "Ошибка сети. Проверь адрес и не занят ли порт другим сервером.";
        LabTelemetry.Record("transport_failure", Status);
    }
    public void StopSession(string message = "Сессия завершена.")
    {
        Manager.Shutdown();
        Status = message;
        world.SetActive(false);
        slots.Clear();
        LabTelemetry.Record("stop", message);
        automation.End();
    }
    public void TogglePrediction()
    {
        PredictionEnabled = !PredictionEnabled;
        if (ServerAuthoritativeMovement.Local != null)
            ServerAuthoritativeMovement.Local.SetPrediction(PredictionEnabled);
    }
    private void Update()
    {
        if (world.activeSelf && !Manager.IsListening) world.SetActive(false);
    }
    private void OnDestroy()
    {
        if (Manager != null)
        {
            Manager.OnClientConnectedCallback -= OnConnected;
            Manager.OnClientDisconnectCallback -= OnDisconnected;
            Manager.OnTransportFailure -= OnTransportFailure;
        }
        if (Instance == this) Instance = null;
        UnityTransport.s_DriverConstructor = null;
        LabTelemetry.Close();
    }
}
