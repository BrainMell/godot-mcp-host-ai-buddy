// Tests/ParserTest.cs
// Run with: dotnet script Tests/ParserTest.cs
// Or just: dotnet run --project Tests

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

class ParserTest
{
    // ---- same regex as ChatDock.cs ----
    static readonly Regex ToolCallRegex = new Regex(@"\[CALL\]([ \s\S]*?)\[/CALL\]", RegexOptions.Compiled);

    // ---- result format ----
    record TestResult(string Name, bool Passed, string Detail);

    static readonly List<TestResult> Results = new();

    static void Main()
    {
        Console.WriteLine("=== GodotMCP Parser Unit Tests ===\n");

        // Happy path — perfect single-line JSON
        Test("SingleLine_CreateNode",
            input: "[CALL]\n{\"tool\": \"create_2d_node\", \"node_type\": \"Sprite2D\", \"node_name\": \"Background\"}\n[/CALL]",
            expectedTool: "create_2d_node",
            expectedArgs: new Dictionary<string, string> { ["node_type"] = "Sprite2D", ["node_name"] = "Background" });

        // Multi-line pretty-printed JSON (Gemini likes to do this)
        Test("MultiLine_CreateScene",
            input: "[CALL]\n{\n  \"tool\": \"create_new_scene\",\n  \"scene_path\": \"res://Character.tscn\",\n  \"root_type\": \"Node2D\",\n  \"root_name\": \"Character\"\n}\n[/CALL]",
            expectedTool: "create_new_scene",
            expectedArgs: new Dictionary<string, string> {
                ["scene_path"] = "res://Character.tscn",
                ["root_type"] = "Node2D",
                ["root_name"] = "Character"
            });

        // Tool only (no args)
        Test("NoArgs_SaveScene",
            input: "[CALL]\n{\"tool\": \"save_scene\"}\n[/CALL]",
            expectedTool: "save_scene",
            expectedArgs: new Dictionary<string, string>());

        // AI wrapped in prose — prose should be stripped, call should still parse
        Test("WrappedInProse_StillParses",
            input: "Sure, I'll create that scene for you!\n[CALL]\n{\"tool\": \"create_new_scene\", \"scene_path\": \"res://Player.tscn\", \"root_type\": \"CharacterBody2D\", \"root_name\": \"Player\"}\n[/CALL]\nLet me know if you need anything else.",
            expectedTool: "create_new_scene",
            expectedArgs: new Dictionary<string, string> {
                ["scene_path"] = "res://Player.tscn",
                ["root_type"] = "CharacterBody2D",
                ["root_name"] = "Player"
            });

        // Numeric args
        Test("NumericArgs_Position",
            input: "[CALL]\n{\"tool\": \"set_node_position\", \"node_path\": \"Player\", \"x\": 300, \"y\": 150}\n[/CALL]",
            expectedTool: "set_node_position",
            expectedArgs: null); // just check it doesn't crash

        // Old malformed format — should NOT match (no crash)
        TestNoMatch("OldFormat_DoesNotMatch",
            input: "<<CALL: create_2d_node({\"node_type\": \"Sprite2D\", \"node_name\": \"Player\"})>>");

        // Missing tool key — should flag error gracefully
        TestMissingToolKey("MissingToolKey",
            input: "[CALL]\n{\"node_type\": \"Sprite2D\", \"node_name\": \"Player\"}\n[/CALL]");

        // Malformed JSON — should flag error gracefully
        TestMalformedJson("MalformedJson",
            input: "[CALL]\n{\"tool\": \"create_2d_node\", \"node_type\": 'Sprite2D'}\n[/CALL]");

        // --- summary ---
        Console.WriteLine("\n=== Results ===");
        int passed = 0, failed = 0;
        foreach (var r in Results)
        {
            string icon = r.Passed ? "✅" : "❌";
            Console.WriteLine($"{icon} {r.Name}");
            if (!r.Passed)
            {
                Console.WriteLine($"   └─ {r.Detail}");
                failed++;
            }
            else passed++;
        }
        Console.WriteLine($"\n{passed} passed, {failed} failed.");
        Environment.Exit(failed > 0 ? 1 : 0);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    static void Test(string name, string input, string expectedTool, Dictionary<string, string>? expectedArgs)
    {
        try
        {
            Match match = ToolCallRegex.Match(input);
            if (!match.Success) { Fail(name, "Regex did not match."); return; }

            string callJson = match.Groups[1].Value.Trim();
            var callObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(callJson);
            if (callObj == null || !callObj.ContainsKey("tool")) { Fail(name, "Missing 'tool' key."); return; }

            string toolName = callObj["tool"].GetString() ?? "";
            if (toolName != expectedTool) { Fail(name, $"Expected tool '{expectedTool}' but got '{toolName}'."); return; }

            callObj.Remove("tool");
            string argsJson = JsonSerializer.Serialize(callObj);

            if (expectedArgs != null)
            {
                // Re-deserialize argsJson to check values
                var argsObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                if (argsObj == null) { Fail(name, "argsJson failed to deserialize."); return; }
                foreach (var kv in expectedArgs)
                {
                    if (!argsObj.ContainsKey(kv.Key))
                    { Fail(name, $"Args missing key '{kv.Key}'."); return; }
                    if (argsObj[kv.Key].GetString() != kv.Value)
                    { Fail(name, $"Arg '{kv.Key}': expected '{kv.Value}' got '{argsObj[kv.Key].GetString()}'."); return; }
                }
            }

            Pass(name, $"tool={toolName} args={argsJson}");
        }
        catch (Exception ex)
        {
            Fail(name, $"Exception: {ex.Message}");
        }
    }

    static void TestNoMatch(string name, string input)
    {
        Match match = ToolCallRegex.Match(input);
        if (!match.Success) Pass(name, "Correctly did not match.");
        else Fail(name, $"Should NOT have matched but did: '{match.Value}'");
    }

    static void TestMissingToolKey(string name, string input)
    {
        try
        {
            Match match = ToolCallRegex.Match(input);
            if (!match.Success) { Fail(name, "Regex didn't match."); return; }
            string callJson = match.Groups[1].Value.Trim();
            var callObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(callJson);
            if (callObj == null || !callObj.ContainsKey("tool"))
                Pass(name, "Correctly identified missing 'tool' key.");
            else
                Fail(name, "Should have flagged missing tool key but didn't.");
        }
        catch (Exception ex) { Fail(name, $"Exception: {ex.Message}"); }
    }

    static void TestMalformedJson(string name, string input)
    {
        Match match = ToolCallRegex.Match(input);
        if (!match.Success) { Fail(name, "Regex didn't match."); return; }
        string callJson = match.Groups[1].Value.Trim();
        try
        {
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(callJson);
            Fail(name, "Should have thrown on malformed JSON but didn't.");
        }
        catch (JsonException)
        {
            Pass(name, "Correctly threw JsonException on malformed JSON.");
        }
    }

    static void Pass(string name, string detail)
    {
        Console.WriteLine($"  ✅ {name}: {detail}");
        Results.Add(new TestResult(name, true, detail));
    }

    static void Fail(string name, string detail)
    {
        Console.WriteLine($"  ❌ {name}: {detail}");
        Results.Add(new TestResult(name, false, detail));
    }
}
