using UnityEngine;
using UnityEngine.InputSystem;

// Plain IMGUI: two start buttons, basic controls and optional experiment statistics.
public class DemoUI : MonoBehaviour
{
    private bool settings, details;
    private GUIStyle text, heading, button;
    private readonly string[] profiles = { "Без задержки", "100 мс / 0%", "175 мс / 4%" };
    private NetworkTopologyManager Session => NetworkTopologyManager.Instance;

    private void Start() { details = Session.Automated; }

    private void Update()
    {
        var keys = Keyboard.current;
        if (keys == null) return;
        if (keys.tabKey.wasPressedThisFrame) details = !details;
        if (keys.escapeKey.wasPressedThisFrame && Session.Manager.IsListening) Session.StopSession();
    }
    private void OnGUI()
    {
        if (Session == null) return;
        if (text == null)
        {
            text = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
            heading = new GUIStyle(text) { fontSize = 25, fontStyle = FontStyle.Bold };
            button = new GUIStyle(GUI.skin.button) { fontSize = 18 };
            GUI.skin.textField.fontSize = 18;
            GUI.skin.toggle.fontSize = 18;
        }
        float scale = Mathf.Min(Screen.width / 1100f, Screen.height / 700f);
        var original = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(new Vector3((Screen.width - 1100 * scale) / 2, (Screen.height - 700 * scale) / 2),
            Quaternion.identity, new Vector3(scale, scale, 1));
        if (!Session.Manager.IsListening) DrawMenu(); else DrawGame();
        GUI.matrix = original;
    }
    private void DrawMenu()
    {
        GUILayout.BeginArea(new Rect(300, settings ? 90 : 190, 500, settings ? 540 : 350), GUI.skin.box);
        GUILayout.Space(15);
        GUILayout.Label("Практическая работа №4", heading);
        GUILayout.Label("Простая сетевая 3D-демонстрация", text);
        GUILayout.Space(20);
        GUI.enabled = !Session.Manager.ShutdownInProgress;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Сервер", button, GUILayout.Height(55))) Session.StartSession("host");
        if (GUILayout.Button("Клиент", button, GUILayout.Height(55))) Session.StartSession("client");
        GUILayout.EndHorizontal();
        GUILayout.Label("Сервер тоже играет. Клиент запускается во втором окне.", text);
        GUILayout.Space(12);
        settings = GUILayout.Toggle(settings, "Настройки подключения и задержки");
        if (settings)
        {
            GUILayout.Label("IP сервера", text);
            Session.Address = GUILayout.TextField(Session.Address, GUILayout.Height(30));
            GUILayout.Label("UDP-порт", text);
            Session.Port = GUILayout.TextField(Session.Port, GUILayout.Height(30));
            GUILayout.Label("Сетевой профиль (одинаковый в обоих окнах)", text);
            Session.Profile = GUILayout.SelectionGrid(Session.Profile, profiles, 3, button, GUILayout.Height(38));
        }
        GUI.enabled = true;
        GUILayout.Space(15);
        GUILayout.Label(Session.Status, text);
        GUILayout.EndArea();
    }
    private void DrawGame()
    {
        var player = ServerAuthoritativeMovement.Local;
        GUILayout.BeginArea(new Rect(12, 12, 480, 180), GUI.skin.box);
        string role = Session.Role == "host" ? "Сервер + игрок" : Session.Role == "server" ? "Сервер" : "Клиент";
        GUILayout.Label(role + "  |  " + profiles[Session.Profile], text);
        if (player == null) GUILayout.Label(Session.Status, text);
        else
        {
            GUILayout.Label("Рывок: " + (player.Cooldown > 0 ? $"кулдаун {player.Cooldown:F1} с" : "готов"), text);
            GUILayout.Label(player.LastAction, text);
        }
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(Session.PredictionEnabled ? "P: предсказание ВКЛ" : "P: предсказание ВЫКЛ", button, GUILayout.Height(32)))
            Session.TogglePrediction();
        if (GUILayout.Button("Esc: меню", button, GUILayout.Height(32))) Session.StopSession();
        GUILayout.EndHorizontal();
        GUILayout.Label(details ? "Tab: скрыть сетевую статистику" : "Tab: показать сетевую статистику", text);
        GUILayout.EndArea();
        if (details && player != null)
        {
            GUILayout.BeginArea(new Rect(608, 12, 480, 180), GUI.skin.box);
            GUILayout.Label($"ACK: {player.Acknowledged}   Сохранённых вводов: {player.PendingCount}\n" +
                $"Отклик: {player.LastResponseMs:F0} мс   Ответ сервера: {player.LastConfirmationMs:F0} мс\n" +
                $"Коррекций: {player.Corrections}   Отказов: {player.Rejections}\n" +
                $"Позиция клиента: {player.DisplayPosition}\nПозиция сервера: {player.AuthoritativePosition}", text);
            GUILayout.EndArea();
        }
        GUILayout.BeginArea(new Rect(12, 625, 1076, 65), GUI.skin.box);
        GUILayout.Label("WASD / стрелки — ходить и поворачивать.  Shift / пробел — рывок.  F — ошибочный рывок.", text);
        GUILayout.Label("Оранжевый — игрок сервера. Синий — клиент. Сервер вернёт ошибочный рывок назад.", text);
        GUILayout.EndArea();
    }
}
