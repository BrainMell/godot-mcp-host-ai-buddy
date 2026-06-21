#if TOOLS
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace GodotMCP;

public class GodotTools
{
    private const string ServerUrl = "http://localhost:9876/";
    private readonly HttpClient _http = new();

    // ── Tool definitions (what the AI sees and uses) ──────────────────────

    public List<object> GetToolDefinitions() => new()
    {
        Tool("ping_godot",
            "Check if the Godot editor plugin is running. Call this first to verify connection.",
            EmptyParams()),

        Tool("get_editor_state",
            "Get the current editor state: whether a scene is open, and its name/path.",
            EmptyParams()),

        Tool("get_scene_tree",
            "Get the full node tree of the currently open scene. Returns all nodes with their types, paths, and children.",
            EmptyParams()),

        Tool("get_selected_nodes",
            "Get the nodes currently selected in the Godot editor viewport or scene tree.",
            EmptyParams()),

        Tool("create_node",
            "Create a new node in the current scene. The scene must already be open. " +
            "Common node types: Node, Node2D, Node3D, Sprite2D, Label, Button, " +
            "CharacterBody2D, RigidBody2D, StaticBody2D, CollisionShape2D, Camera2D, " +
            "AudioStreamPlayer, AnimationPlayer, Control, Panel, VBoxContainer, HBoxContainer.",
            new
            {
                type = "object",
                properties = new
                {
                    node_type = Prop("string",
                        "The Godot node class name. E.g. 'Sprite2D', 'Label', 'CharacterBody2D'."),
                    node_name = Prop("string",
                        "Name for the new node. Defaults to the node type if not given."),
                    parent_path = Prop("string",
                        "Path to parent node relative to scene root. Empty = add to root. " +
                        "Example: 'Player' or 'World/Enemies'")
                },
                required = new[] { "node_type" }
            }),

        Tool("create_2d_node",
            "Create a new 2D node in the current scene. Convenience wrapper around " +
            "create_node specialized for 2D workflows: defaults to Node2D, accepts an " +
            "optional initial position, and validates the type is a CanvasItem-derived " +
            "2D class (Node2D, Sprite2D, CharacterBody2D, RigidBody2D, StaticBody2D, " +
            "CollisionShape2D, Camera2D, Label, Button, etc.). Use this when the user " +
            "asks for a 2D node, sprite, character, or anything that lives in 2D space.",
            new
            {
                type = "object",
                properties = new
                {
                    node_type = Prop("string",
                        "2D node class name. Defaults to 'Node2D'. Must be a 2D type " +
                        "(Node2D, Sprite2D, CharacterBody2D, RigidBody2D, StaticBody2D, " +
                        "CollisionShape2D, Camera2D, Label, Button, etc.)."),
                    node_name = Prop("string",
                        "Name for the new node. Defaults to the node type if not given."),
                    parent_path = Prop("string",
                        "Path to parent node relative to scene root. Empty = add to root. " +
                        "Example: 'Player' or 'World/Enemies'"),
                    position = new
                    {
                        type = "array",
                        description = "Initial position as [x, y] in pixels. Optional. " +
                                      "Example: [100, 200].",
                        items = new { type = "number" }
                    }
                },
                required = new string[] { }
            }),

        Tool("delete_node",
            "Delete a node from the current scene by its path.",
            new
            {
                type = "object",
                properties = new
                {
                    path = Prop("string", "Node path relative to scene root. E.g. 'Player' or 'Player/Sprite2D'")
                },
                required = new[] { "path" }
            }),

        Tool("get_node_properties",
            "Get all properties of a node so you can inspect or modify them.",
            new
            {
                type = "object",
                properties = new
                {
                    path = Prop("string", "Node path. E.g. 'Player' or 'Player/Sprite2D'")
                },
                required = new[] { "path" }
            }),

        Tool("set_node_property",
            "Set a property on a node. For Vector2 properties like position or scale, " +
            "pass value as [x, y] array. For colors pass [r, g, b, a]. For strings pass a string. " +
            "For booleans pass true/false.",
            new
            {
                type = "object",
                properties = new
                {
                    path     = Prop("string", "Node path"),
                    property = Prop("string", "Property name. E.g. 'position', 'scale', 'visible', 'modulate'"),
                    value    = new { description = "Value to set. Use [x,y] for Vector2, true/false for bool, number for float." }
                },
                required = new[] { "path", "property", "value" }
            }),

        Tool("save_scene",
            "Save the currently open scene to disk.",
            EmptyParams()),

        Tool("list_project_files",
            "List all files in the Godot project. Useful for finding scenes, scripts, and assets.",
            new
            {
                type = "object",
                properties = new
                {
                    path = Prop("string", "Directory to list. Defaults to 'res://' (project root).")
                }
            }),

        Tool("create_new_scene",
            "Create a brand-new Godot scene file (.tscn) on disk and immediately open it " +
            "in the editor. Use this when the user asks to create a scene, level, or new " +
            "screen. After calling this, the scene is open and you can call create_node " +
            "to populate it.",
            new
            {
                type = "object",
                properties = new
                {
                    scene_path = Prop("string",
                        "Full res:// path for the new scene file. E.g. 'res://scenes/Main.tscn'. " +
                        "Must end in .tscn."),
                    root_type = Prop("string",
                        "Node type for the scene root. Defaults to 'Node2D'. " +
                        "Common choices: Node, Node2D, Node3D, Control."),
                    root_name = Prop("string",
                        "Name for the root node. Defaults to the root_type if not given.")
                },
                required = new[] { "scene_path" }
            }),

        Tool("open_scene",
            "Open an existing scene file in the Godot editor. " +
            "Use list_project_files first to discover available scenes.",
            new
            {
                type = "object",
                properties = new
                {
                    scene_path = Prop("string",
                        "Full res:// path to the scene file. E.g. 'res://scenes/Main.tscn'.")
                },
                required = new[] { "scene_path" }
            })
    };

    // ── Tool execution ────────────────────────────────────────────────────

    public async Task<string> ExecuteAsync(string toolName, string argsJson)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson)
                   ?? new();

        return toolName switch
        {
            "ping_godot"           => await Call("ping", new { }),
            "get_editor_state"     => await Call("get_editor_state", new { }),
            "get_scene_tree"       => await Call("get_scene_tree", new { }),
            "get_selected_nodes"   => await Call("get_selected_nodes", new { }),

            "create_node" => await Call("create_node", new
            {
                node_type   = Str(args, "node_type", "Node"),
                node_name   = Str(args, "node_name", Str(args, "node_type", "Node")),
                parent_path = Str(args, "parent_path", "")
            }),

            "create_2d_node" => await Call("create_2d_node", new
            {
                node_type   = Str(args, "node_type", "Node2D"),
                node_name   = Str(args, "node_name", Str(args, "node_type", "Node2D")),
                parent_path = Str(args, "parent_path", ""),
                position    = args.TryGetValue("position", out var posEl) ? (object)posEl : (object)null
            }),

            "delete_node" => await Call("delete_node", new
            {
                path = Str(args, "path", "")
            }),

            "get_node_properties" => await Call("get_node_properties", new
            {
                path = Str(args, "path", "")
            }),

            "set_node_property" => await Call("set_node_property", new
            {
                path     = Str(args, "path", ""),
                property = Str(args, "property", ""),
                value    = args.TryGetValue("value", out var v) ? (object)v : (object)""
            }),

            "save_scene"        => await Call("save_scene", new { }),

            "list_project_files" => await Call("list_project_files", new
            {
                path = Str(args, "path", "res://")
            }),

            "create_new_scene" => await Call("create_new_scene", new
            {
                scene_path = Str(args, "scene_path", "res://new_scene.tscn"),
                root_type  = Str(args, "root_type", "Node2D"),
                root_name  = Str(args, "root_name", Str(args, "root_type", "Node2D"))
            }),

            "open_scene" => await Call("open_scene", new
            {
                scene_path = Str(args, "scene_path", "")
            }),

            _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" })
        };
    }

    // ── HTTP call to McpHttpServer inside Godot ───────────────────────────

    private async Task<string> Call(string action, object parameters)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                action,
                @params = parameters
            });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var res = await _http.PostAsync(ServerUrl, content);
            return await res.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error  = "Cannot reach Godot HTTP server",
                detail = ex.Message,
                hint   = "Is the GodotMCP plugin enabled in Project Settings → Plugins?"
            });
        }
    }

    // ── Schema helpers ────────────────────────────────────────────────────

    private static object Tool(string name, string description, object parameters) =>
        new { type = "function", function = new { name, description, parameters } };

    private static object Prop(string type, string description) =>
        new { type, description };

    private static object EmptyParams() =>
        new { type = "object", properties = new { } };

    private static string Str(Dictionary<string, JsonElement> d, string key, string fallback) =>
        d.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? fallback : fallback;
}
#endif
