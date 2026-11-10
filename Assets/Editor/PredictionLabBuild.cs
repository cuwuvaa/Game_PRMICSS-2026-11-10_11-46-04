using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class PredictionLabBuild
{
    [MenuItem("Practical 4/Build macOS and validate simulation")]
    public static void BuildMac()
    {
        ValidateSimulation();
        Directory.CreateDirectory("Builds");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/Demo.unity" },
            locationPathName = "Builds/PredictionLab.app",
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.Development
        });
        var summary = report.summary;
        File.WriteAllText("Builds/build-result.txt", $"Result: {summary.result}\nErrors: {summary.totalErrors}\nWarnings: {summary.totalWarnings}\nSize: {summary.totalSize}\nUnity: {Application.unityVersion}\nSimulation checks: passed\n");
        if (summary.result != BuildResult.Succeeded) throw new Exception("Build failed: " + summary.result);
    }

    [MenuItem("Practical 4/Validate simulation")]
    public static void ValidateSimulation()
    {
        var initial = new MovementState { Position = new Vector2(-5, -3), Facing = Vector2.up };
        var command = new PlayerCommand { Sequence = 1, Direction = Vector2.up, Dash = true };
        var first = PredictionSimulation.Simulate(initial, command);
        Require(first.LastDashResult == 1 && first.Cooldown == 90, "First dash accepted");
        command.Sequence++;
        var cooldown = PredictionSimulation.Simulate(first, command);
        Require(cooldown.LastDashResult == 2 && cooldown.Position.y - first.Position.y < 0.2f, "Cooldown rejected");
        var wallState = new MovementState { Position = new Vector2(0, 0), Facing = Vector2.right };
        var wall = PredictionSimulation.Simulate(wallState, new PlayerCommand { Sequence = 1, Dash = true });
        Require(wall.LastDashResult == 3 && wall.Position == wallState.Position, "Swept wall collision");
        var boundary = new MovementState { Position = new Vector2(10, 0), Facing = Vector2.right };
        Require(PredictionSimulation.Simulate(boundary, new PlayerCommand { Dash = true }).LastDashResult == 3, "Boundary rejection");
        var honest = PredictionSimulation.Simulate(first, command);
        var wrong = PredictionSimulation.Simulate(first, command, true);
        Require(Vector2.Distance(honest.Position, wrong.Position) > 7f, "Fault injection differs locally");
        var diagonal = PredictionSimulation.Simulate(initial,
            new PlayerCommand { Direction = new Vector2(1, 1) });
        Require(Vector2.Distance(diagonal.Position, initial.Position) <= PredictionSimulation.Speed * PredictionSimulation.Step + 0.0001f,
            "Diagonal input cannot increase movement speed");
        var invalid = PredictionSimulation.Simulate(initial, new PlayerCommand { Direction = new Vector2(float.NaN, float.PositiveInfinity) });
        Require(PredictionSimulation.IsFinite(invalid.Position), "Nonfinite input cannot poison state");
        Debug.Log("[PredictionLab] 7 simulation checks passed");
    }
    private static void Require(bool value, string message)
    { if (!value) throw new Exception("Simulation check failed: " + message); }
}
