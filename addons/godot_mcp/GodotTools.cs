#if TOOLS
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace GodotMCP;

// ---------------------------------------------------------------------------
// GodotTools — two jobs:
//
//   1. TOOL DEFINITIONS: Tell the LLM what tools exist (name, description,
//      what parameters they accept). The LLM uses this to decide what to call.
//
//   2. TOOL EXECUTION (ROUTING): When the LLM wants to call a tool, this class
//      figures out which method on the HTTP server to call, and sends the
//      request over localhost.
//
// The actual tool implementations live in McpHttpServer.cs — this file just
// defines the schemas and routes the calls.
// ---------------------------------------------------------------------------

public class GodotTools
{
    // The HTTP server running inside Godot on the main thread
    private readonly string _serverUrl;

    // An HTTP client used to talk to that local server
    private HttpClient _http;

    public GodotTools(string serverUrl = "http://localhost:9876/")
    {
        _serverUrl = serverUrl;
        _http = new HttpClient();
    }

    // =======================================================================
    // GetToolDefinitions — returns a list of all tool schemas
    //
    // Each tool schema tells the LLM:
    //   - The tool's name (e.g. "create_node")
    //   - A description of what it does
    //   - The parameters it accepts (as a JSON Schema object)
    //
    // The LLM uses these schemas to decide which tool to call and what
    // arguments to pass. This list is sent with EVERY API request.
    //
    // The format matches the OpenAI function-calling API:
    // {
    //   "type": "function",
    //   "function": {
    //     "name": "create_node",
    //     "description": "Create a new node...",
    //     "parameters": { "type": "object", "properties": { ... } }
    //   }
    // }
    // =======================================================================
    public List<object> GetToolDefinitions()
    {
        List<object> tools = new List<object>();

        // -- Health check --
        tools.Add(Tool(
            name: "ping_godot",
            description: "Check if the Godot editor plugin is running. Call this first to verify connection.",
            parameters: EmptyParams()
        ));

        // -- Editor state --
        tools.Add(Tool(
            name: "get_editor_state",
            description: "Get the current editor state: whether a scene is open, and its name/path.",
            parameters: EmptyParams()
        ));

        // -- Scene tree inspection --
        tools.Add(Tool(
            name: "get_scene_tree",
            description: "Get the full node tree of the currently open scene. Returns all nodes with their types, paths, and children.",
            parameters: EmptyParams()
        ));

        // -- Selection inspection --
        tools.Add(Tool(
            name: "get_selected_nodes",
            description: "Get the nodes currently selected in the Godot editor viewport or scene tree.",
            parameters: EmptyParams()
        ));

        // -- Create any node type --
        tools.Add(Tool(
            name: "create_node",
            description: "Create a new node in the current scene. The scene must already be open. " +
                         "Common node types: Node, Node2D, Node3D, Sprite2D, Label, Button, " +
                         "CharacterBody2D, RigidBody2D, StaticBody2D, CollisionShape2D, Camera2D, " +
                         "AudioStreamPlayer, AnimationPlayer, Control, Panel, VBoxContainer, HBoxContainer.",
            parameters: new
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
                required = new string[] { "node_type" }
            }
        ));

        // -- Create a 2D node (specialized, includes position) --
        tools.Add(Tool(
            name: "create_2d_node",
            description: "Create a new 2D node in the current scene. Convenience wrapper around " +
                         "create_node specialized for 2D workflows: defaults to Node2D, accepts an " +
                         "optional initial position, and validates the type is a CanvasItem-derived " +
                         "2D class (Node2D, Sprite2D, CharacterBody2D, RigidBody2D, StaticBody2D, " +
                         "CollisionShape2D, Camera2D, Label, Button, etc.). Use this when the user " +
                         "asks for a 2D node, sprite, character, or anything that lives in 2D space.",
            parameters: new
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
            }
        ));

        // -- Delete a node --
        tools.Add(Tool(
            name: "delete_node",
            description: "Delete a node from the current scene by its path.",
            parameters: new
            {
                type = "object",
                properties = new
                {
                    path = Prop("string", "Node path relative to scene root. E.g. 'Player' or 'Player/Sprite2D'")
                },
                required = new string[] { "path" }
            }
        ));

        // -- Inspect a node's properties --
        tools.Add(Tool(
            name: "get_node_properties",
            description: "Get all properties of a node so you can inspect or modify them.",
            parameters: new
            {
                type = "object",
                properties = new
                {
                    path = Prop("string", "Node path. E.g. 'Player' or 'Player/Sprite2D'")
                },
                required = new string[] { "path" }
            }
        ));

        // -- Set a property on a node --
        tools.Add(Tool(
            name: "set_node_property",
            description: "Set a property on a node. IMPORTANT: if you are unsure whether a property " +
                         "name is valid for the node type, call get_node_properties first to see the " +
                         "full list of available properties. " +
                         "For Vector2 properties like position or scale, pass value as [x, y] array. " +
                         "For colors pass [r, g, b, a]. For strings pass a string. " +
                         "For booleans pass true/false.",
            parameters: new
            {
                type = "object",
                properties = new
                {
                    path = Prop("string", "Node path"),
                    property = Prop("string", "Property name. E.g. 'position', 'scale', 'visible', 'modulate'"),
                    value = new { description = "Value to set. Use [x,y] for Vector2, true/false for bool, number for float." }
                },
                required = new string[] { "path", "property", "value" }
            }
        ));

        // -- Save the current scene --
        tools.Add(Tool(
            name: "save_scene",
            description: "Save the currently open scene to disk.",
            parameters: EmptyParams()
        ));

        // -- List project files --
        tools.Add(Tool(
            name: "list_project_files",
            description: "List all files in the Godot project. Useful for finding scenes, scripts, and assets.",
            parameters: new
            {
                type = "object",
                properties = new
                {
                    path = Prop("string", "Directory to list. Defaults to 'res://' (project root).")
                }
            }
        ));

        // -- Create a brand-new scene file --
        tools.Add(Tool(
            name: "create_new_scene",
            description: "Create a brand-new Godot scene file (.tscn) on disk and immediately open it " +
                         "in the editor. Use this when the user asks to create a scene, level, or new " +
                         "screen. After calling this, the scene is open and you can call create_node " +
                         "to populate it.",
            parameters: new
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
                required = new string[] { "scene_path" }
            }
        ));

        // -- Open an existing scene --
        tools.Add(Tool(
            name: "open_scene",
            description: "Open an existing scene file in the Godot editor. " +
                         "Use list_project_files first to discover available scenes.",
            parameters: new
            {
                type = "object",
                properties = new
                {
                    scene_path = Prop("string",
                        "Full res:// path to the scene file. E.g. 'res://scenes/Main.tscn'.")
                },
                required = new string[] { "scene_path" }
            }
        ));

        // -- Set a sprite texture from a file path --
        tools.Add(Tool(
            name: "set_sprite_texture",
            description: "Assign an image file as the texture of a Sprite2D node. " +
                         "The image_path can be a res:// path (inside the project) or an " +
                         "absolute filesystem path (e.g. /home/user/images/player.png) — " +
                         "if an absolute path is given, the file will be copied into the project first. " +
                         "The node at 'path' must be a Sprite2D or subclass.",
            parameters: new
            {
                type = "object",
                properties = new
                {
                    path = Prop("string", "Node path in the scene. E.g. 'Player/Sprite2D'"),
                    image_path = Prop("string",
                        "Path to the image. Either res:// project path or absolute OS path. " +
                        "Supported formats: png, jpg, webp, svg.")
                },
                required = new string[] { "path", "image_path" }
            }
        ));

        // -- Run a shell command --
        tools.Add(Tool(
            name: "run_shell_command",
            description: "Run a bash shell command on the host machine. Use for file operations: " +
                         "moving, copying, or deleting files anywhere on the filesystem. " +
                         "Example commands: 'mv /tmp/art.png /home/user/project/assets/art.png', " +
                         "'rm /home/user/project/old_scene.tscn', 'cp /downloads/sprite.png res://assets/'. " +
                         "CAUTION: do not run destructive commands unless the user explicitly asked. " +
                         "Returns stdout, stderr, and exit code.",
            parameters: new
            {
                type = "object",
                properties = new
                {
                    command = Prop("string", "The full bash command to run. E.g. 'mv /tmp/hero.png /home/user/project/assets/hero.png'")
                },
                required = new string[] { "command" }
            }
        ));

        // -- Script templates --
        tools.Add(Tool(
            name: "list_script_templates",
            description: "List all available GDScript template files in the plugin's addons/godot_mcp/templates/ folder. " +
                         "Each template is a ready-to-use script for common game patterns. " +
                         "Call this first when the user asks to add movement, health, enemies, etc. " +
                         "Then use read_file to read the template, adapt it, and write_file to save it.",
            parameters: EmptyParams()
        ));

        tools.Add(Tool(
            name: "read_file",
            description: "Read the full text content of any file in the project. " +
                         "Use to read script templates before adapting them, or inspect existing scripts.",
            parameters: new
            {
                type = "object",
                properties = new
                {
                    path = Prop("string", "res:// path to the file. E.g. 'res://addons/godot_mcp/templates/character_2d_platformer.gd'")
                },
                required = new string[] { "path" }
            }
        ));

        tools.Add(Tool(
            name: "write_file",
            description: "Write (create or overwrite) a text file in the project. " +
                         "Use this to write adapted scripts to the project before attaching them to nodes. " +
                         "Always write scripts to res://scripts/ or a subfolder. " +
                         "After writing, use attach_script to attach the script to a node.",
            parameters: new
            {
                type = "object",
                properties = new
                {
                    path = Prop("string", "res:// path where the file should be written. E.g. 'res://scripts/Player.gd'"),
                    content = Prop("string", "Full text content of the file to write.")
                },
                required = new string[] { "path", "content" }
            }
        ));

        tools.Add(Tool(
            name: "attach_script",
            description: "Attach a GDScript (.gd) file to a node in the current scene. " +
                         "The script file must already exist (use write_file first). " +
                         "The script's 'extends' class should match the node type.",
            parameters: new
            {
                type = "object",
                properties = new
                {
                    node_path = Prop("string", "Scene-relative path to the node. E.g. 'Player' or 'World/Enemy'"),
                    script_path = Prop("string", "res:// path to the .gd script file. E.g. 'res://scripts/Player.gd'")
                },
                required = new string[] { "node_path", "script_path" }
            }
        ));

        return tools;
    }

    // =======================================================================
    // ExecuteAsync — called when the LLM wants to call a tool
    //
    // This method receives:
    //   - toolName: e.g. "create_node"
    //   - argsJson: a JSON string like '{"node_type":"Sprite2D","node_name":"Player"}'
    //
    // It parses the arguments, then sends an HTTP request to the local
    // McpHttpServer (running on port 9876) which actually executes the tool.
    // =======================================================================
    public async Task<string> ExecuteAsync(string toolName, string argsJson)
    {
        // Parse the JSON arguments into a dictionary
        Dictionary<string, JsonElement>? args;
        try
        {
            args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
        }
        catch
        {
            args = new Dictionary<string, JsonElement>();
        }
        if (args == null)
        {
            args = new Dictionary<string, JsonElement>();
        }

        // Route each tool name to the correct HTTP call
        // The first arg to Call() is the "action" that McpHttpServer.Dispatch() will match
        // The second arg is the parameters object

        if (toolName == "ping_godot")
        {
            return await Call("ping", new { });
        }
        else if (toolName == "get_editor_state")
        {
            return await Call("get_editor_state", new { });
        }
        else if (toolName == "get_scene_tree")
        {
            return await Call("get_scene_tree", new { });
        }
        else if (toolName == "get_selected_nodes")
        {
            return await Call("get_selected_nodes", new { });
        }
        else if (toolName == "create_node")
        {
            return await Call("create_node", new
            {
                node_type = Str(args, "node_type", "Node"),
                node_name = Str(args, "node_name", Str(args, "node_type", "Node")),
                parent_path = Str(args, "parent_path", "")
            });
        }
        else if (toolName == "create_2d_node")
        {
            // For the position field, we need to pass the raw JsonElement (an array)
            // instead of converting it to a string
            object? positionValue = null;
            if (args.ContainsKey("position"))
            {
                positionValue = args["position"];
            }

            return await Call("create_2d_node", new
            {
                node_type = Str(args, "node_type", "Node2D"),
                node_name = Str(args, "node_name", Str(args, "node_type", "Node2D")),
                parent_path = Str(args, "parent_path", ""),
                position = positionValue
            });
        }
        else if (toolName == "delete_node")
        {
            return await Call("delete_node", new
            {
                path = Str(args, "path", "")
            });
        }
        else if (toolName == "get_node_properties")
        {
            return await Call("get_node_properties", new
            {
                path = Str(args, "path", "")
            });
        }
        else if (toolName == "set_node_property")
        {
            // For the value field, we need to pass the raw JsonElement
            // because it could be a string, number, bool, or array
            object valueArg = "";
            if (args.ContainsKey("value"))
            {
                valueArg = args["value"];
            }

            return await Call("set_node_property", new
            {
                path = Str(args, "path", ""),
                property = Str(args, "property", ""),
                value = valueArg
            });
        }
        else if (toolName == "save_scene")
        {
            return await Call("save_scene", new { });
        }
        else if (toolName == "list_project_files")
        {
            return await Call("list_project_files", new
            {
                path = Str(args, "path", "res://")
            });
        }
        else if (toolName == "create_new_scene")
        {
            return await Call("create_new_scene", new
            {
                scene_path = Str(args, "scene_path", "res://new_scene.tscn"),
                root_type = Str(args, "root_type", "Node2D"),
                root_name = Str(args, "root_name", Str(args, "root_type", "Node2D"))
            });
        }
        else if (toolName == "open_scene")
        {
            return await Call("open_scene", new
            {
                scene_path = Str(args, "scene_path", "")
            });
        }
        else if (toolName == "set_sprite_texture")
        {
            return await Call("set_sprite_texture", new
            {
                path = Str(args, "path", ""),
                image_path = Str(args, "image_path", "")
            });
        }
        else if (toolName == "run_shell_command")
        {
            string command = Str(args, "command", "");
            if (string.IsNullOrEmpty(command))
                return JsonSerializer.Serialize(new { error = "No command provided" });
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = "-c " + JsonSerializer.Serialize(command),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                    return JsonSerializer.Serialize(new { error = "Failed to start process" });
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                return JsonSerializer.Serialize(new
                {
                    exit_code = proc.ExitCode,
                    stdout = stdout.Trim(),
                    stderr = stderr.Trim()
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
        else if (toolName == "list_script_templates")
        {
            return await Call("list_script_templates", new { });
        }
        else if (toolName == "read_file")
        {
            return await Call("read_file", new
            {
                path = Str(args, "path", "")
            });
        }
        else if (toolName == "write_file")
        {
            return await Call("write_file", new
            {
                path = Str(args, "path", ""),
                content = Str(args, "content", "")
            });
        }
        else if (toolName == "attach_script")
        {
            return await Call("attach_script", new
            {
                node_path = Str(args, "node_path", ""),
                script_path = Str(args, "script_path", "")
            });
        }
        else
        {
            // Unknown tool — return an error so the LLM knows it messed up
            string errorJson = JsonSerializer.Serialize(new
            {
                error = "Unknown tool: " + toolName
            });
            return errorJson;
        }
    }

    // =======================================================================
    // Call — sends an HTTP POST to the local McpHttpServer
    //
    // The request body looks like:
    // {
    //   "action": "create_node",
    //   "params": { "node_type": "Sprite2D", "node_name": "Player" }
    // }
    //
    // McpHttpServer._Process() picks this up and Dispatch() routes it.
    // =======================================================================
    private async Task<string> Call(string action, object parameters)
    {
        try
        {
            // Build the JSON payload
            // Note: @params uses the @ symbol because "params" is a reserved
            //       keyword in C# (used for the "params" array parameter feature)
            string payload = JsonSerializer.Serialize(new
            {
                action = action,
                @params = parameters
            });

            // Send it to the local server
            StringContent content = new StringContent(payload, Encoding.UTF8, "application/json");
            HttpResponseMessage res = await _http.PostAsync(_serverUrl, content);
            string responseBody = await res.Content.ReadAsStringAsync();
            return responseBody;
        }
        catch (Exception ex)
        {
            // If the server is unreachable, return a helpful error
            // (this error goes back to the LLM, which explains it to the user)
            string errorJson = JsonSerializer.Serialize(new
            {
                error = "Cannot reach Godot HTTP server",
                detail = ex.Message,
                hint = "Is the GodotMCP plugin enabled in Project Settings > Plugins?"
            });
            return errorJson;
        }
    }

    // =======================================================================
    // Schema helper methods — used by GetToolDefinitions() to build the
    // tool schemas in the format the API expects
    // =======================================================================

    // Wraps a tool's info into the OpenAI function-tool format:
    // { "type": "function", "function": { "name": ..., "description": ..., "parameters": ... } }
    private static object Tool(string name, string description, object parameters)
    {
        var functionInfo = new { name = name, description = description, parameters = parameters };
        var toolObject = new { type = "function", function = functionInfo };
        return toolObject;
    }

    // Creates a simple property descriptor: { "type": "string", "description": "..." }
    // Used for parameters that are just strings
    private static object Prop(string type, string description)
    {
        return new { type = type, description = description };
    }

    // Creates an empty parameters object: { "type": "object", "properties": {} }
    // Used for tools that take no parameters (like ping_godot, save_scene)
    private static object EmptyParams()
    {
        return new { type = "object", properties = new { } };
    }

    // Safely reads a string value from the parsed JSON arguments.
    // If the key doesn't exist or isn't a string, returns the fallback value.
    //
    // Example: Str(args, "node_type", "Node")
    //   - If args has a string "node_type" key, returns that value
    //   - Otherwise, returns "Node"
    private static string Str(Dictionary<string, JsonElement> argsDict, string key, string fallback)
    {
        bool keyExists = argsDict.TryGetValue(key, out JsonElement element);
        if (keyExists && element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            if (value != null)
            {
                return value;
            }
        }
        return fallback;
    }
}
#endif