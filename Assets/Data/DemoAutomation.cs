using System.Collections;
using UnityEngine;

// Optional command-line replay for the five experiments; unused during a manual demonstration.
public class DemoAutomation : MonoBehaviour
{
    public bool Enabled { get; private set; }
    public Vector2 Direction { get; private set; }
    private string scenario, role;
    private float connectedAt = -1, sampleAt, duration = 16;
    private bool firstDash, faultDash, secondDash, captured, faultCaptured;
    private void Awake()
    {
        scenario = LabTelemetry.Argument("-labScenario", "manual");
        Enabled = scenario != "manual";
        if (float.TryParse(LabTelemetry.Argument("-labDuration", "16"), out float value)) duration = value;
    }
    public void Begin(string sessionRole)
    {
        role = sessionRole;
        firstDash = faultDash = secondDash = captured = faultCaptured = false;
        if (role == "server") Connected();
    }
    public void Connected() { connectedAt = Time.realtimeSinceStartup; }
    public void End() { connectedAt = -1; Direction = Vector2.zero; }
    private void Update()
    {
        if (Enabled && connectedAt < 0 && Time.realtimeSinceStartup > 25f)
        { LabTelemetry.Record("test_failed", "connection timeout"); Application.Quit(2); }
        if (connectedAt < 0) return;
        float t = Time.realtimeSinceStartup - connectedAt;
        var player = ServerAuthoritativeMovement.Local;
        if (Enabled && player != null)
        {
            Direction = role == "host" ? (t > 6f && t < 7.2f ? Vector2.up : Vector2.zero) :
                (t < 1.2f ? Vector2.up : t > 6 && t < 7.2f ? Vector2.left : Vector2.zero);
            if (role == "client")
            {
                if (t > 2f && !firstDash) { firstDash = true; player.QueueDash(false); }
                if ((scenario == "correction" || scenario == "stress") && t > 2.7f && !faultDash)
                { faultDash = true; player.QueueDash(true); }
                if (faultDash && t > 2.8f && !faultCaptured)
                { faultCaptured = true; StartCoroutine(Capture("correction_before")); }
                if (t > 8f && !secondDash) { secondDash = true; player.QueueDash(false); }
                if (t > 4.2f && !captured)
                { captured = true; StartCoroutine(Capture(scenario)); }
            }
        }
        if (player != null && Time.realtimeSinceStartup >= sampleAt)
        {
            sampleAt = Time.realtimeSinceStartup + 0.25f;
            LabTelemetry.Record("sample", $"ack={player.Acknowledged}; pending={player.PendingCount}; pos={player.DisplayPosition}; authority={player.AuthoritativePosition}; corrections={player.Corrections}; rejected={player.Rejections}; spawned={FindObjectsByType<ServerAuthoritativeMovement>().Length}");
        }
        if (Enabled && t > duration)
        {
            LabTelemetry.Record("test_complete", $"role={role}; players={FindObjectsByType<ServerAuthoritativeMovement>().Length}; corrections={player?.Corrections}; rejections={player?.Rejections}; ack={player?.Acknowledged}");
            NetworkTopologyManager.Instance.Manager.Shutdown();
            LabTelemetry.Close();
            Application.Quit();
        }
    }
    private IEnumerator Capture(string name)
    {
        yield return new WaitForEndOfFrame();
        string path = System.IO.Path.Combine(LabTelemetry.DirectoryPath, name + ".png");
        ScreenCapture.CaptureScreenshot(path);
        LabTelemetry.Record("screenshot", path);
    }
}
