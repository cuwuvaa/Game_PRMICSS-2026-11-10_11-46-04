using System;
using System.Globalization;
using System.IO;
using UnityEngine;

public static class LabTelemetry
{
    private static StreamWriter writer;
    public static string DirectoryPath { get; private set; }
    public static string Argument(string name, string fallback = "")
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
        return fallback;
    }
    public static void Open(string role)
    {
        Close();
        DirectoryPath = Argument("-labOutput", Path.Combine(Application.persistentDataPath, "PredictionLab"));
        Directory.CreateDirectory(DirectoryPath);
        writer = new StreamWriter(Path.Combine(DirectoryPath, role + "_events.csv"), false) { AutoFlush = true };
        writer.WriteLine("seconds,event,details");
        Record("session", $"unity={Application.unityVersion}; role={role}; profile={NetworkTopologyManager.Instance.Profile}");
    }
    public static void Record(string name, string details)
    {
        string time = Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture);
        writer?.WriteLine(time + "," + name + ",\"" + details.Replace("\"", "\"\"") + "\"");
        if (name != "sample") Debug.Log("[PredictionLab] " + name + " " + details);
    }
    public static void Close() { writer?.Dispose(); writer = null; }
}
