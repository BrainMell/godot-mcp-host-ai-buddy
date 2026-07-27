# godot-mcp-host-ai-buddy

A Godot 4 editor plugin that gives an AI agent (Gemini, ChatGPT, or Zai) **direct control over the Godot editor** — creating nodes, editing properties, managing scenes, assigning textures, and running shell commands — all from a chat dock inside Godot itself.

No API key required. The plugin reuses your saved browser session via Playwright.

---

## How it Works

```
You type a message
      ↓
ChatDock.cs  →  AgentWrapper.cs (Playwright browser)
                      ↓
              Gemini / ChatGPT / Zai (web UI)
                      ↓
         AI responds with [CALL]{...}[/CALL]
                      ↓
           GodotTools.cs parses the JSON
                      ↓
     HTTP POST → McpHttpServer.cs (main thread)
                      ↓
        Godot editor API executes the command
                      ↓
         [RESULT]{...}[/RESULT] sent back to AI
                      ↓
         AI continues or summarises in chat
```

### Why HTTP?
Godot's editor APIs (SceneTree, EditorInterface, node manipulation) are **not thread-safe**. The Playwright browser runs async. The solution: a tiny HTTP server (`McpHttpServer`) running on `localhost:9876` that receives requests and processes them on Godot's main thread inside `_Process()`.

---

## Setup

### Prerequisites
- Godot 4.x with .NET (C#) support
- .NET 8 SDK
- A Google account signed in to [gemini.google.com](https://gemini.google.com) (or ChatGPT / Zai)

### Install
1. Clone this repo into your Godot project's root (or alongside it)
2. Enable the plugin: **Project → Project Settings → Plugins → GodotMCP → Enable**
3. Build: click **Build** in the top-right of the Godot editor, or run `dotnet build`
4. The **godot-mcp** chat dock appears at the bottom of the editor

### First Run
The plugin opens a persistent Chromium browser window using your saved login session. If you're not signed in, it will navigate to the login page — sign in once and it persists.

---

## Chat UI

### Roles / Prefixes
| Prefix | Meaning |
|---|---|
| `💻` / `sys` | System messages (plugin status, hints) |
| `🧔` / `you` | Your messages |
| `🤖` / `ai` | AI responses |
| `🔧` / `tool` | Tool call logs (truncated for readability; full output in Godot Output panel) |
| `err` | Errors |

### Slash Commands
| Command | What it does |
|---|---|
| `/clear` | Clear chat and start a fresh AI session |
| `/copy` | Copy full chat log to clipboard |
| `/model <name>` | Switch between `gemini`, `Chatgpt`, `Zai` |
| `/help` | Show available commands |

### Customising Prefixes / Colors
Edit `AppendMessage()` in [`ChatDock.cs`](addons/godot_mcp/ChatDock.cs) — each role maps to a prefix string and a `Color`. Change the strings to whatever you want (emoji, words, etc.).

### Adding Slash Commands
In `OnSend(string text)` in [`ChatDock.cs`](addons/godot_mcp/ChatDock.cs), intercept at the top of the method before the AI send logic:

```csharp
if (text.StartsWith("/mycommand"))
{
    AppendMessage("system", "did the thing!");
    _input.Clear();
    return; // stops it from going to the AI
}
```

### Truncating Long Tool Output
Tool output is truncated in the chat UI via `Truncate(string s, int maxLen)` in [`ChatDock.cs`](addons/godot_mcp/ChatDock.cs). Change the `120` (args) and `200` (result) limits in `OnSend()` to whatever you like. Full output always goes to Godot's **Output** panel.

---

## Available Tools

The AI picks which tool to call based on your request. You can see all tool definitions in [`GodotTools.cs`](addons/godot_mcp/GodotTools.cs).

### Scene Management

| Tool | Description |
|---|---|
| `get_editor_state` | Is a scene open? Returns name and `res://` path |
| `create_new_scene` | Creates a new `.tscn` file and opens it in the editor |
| `open_scene` | Opens an existing `.tscn` file |
| `save_scene` | Saves the current scene to disk |

**Example prompts:**
- *"Create a new 2D scene called Player"*
- *"Open the scene at res://levels/Level1.tscn"*
- *"Save the scene"*

---

### Node Inspection

| Tool | Description |
|---|---|
| `get_scene_tree` | Returns the full node hierarchy with types and paths |
| `get_selected_nodes` | Returns currently selected nodes in the editor |
| `get_node_properties` | Returns ALL properties a node exposes, with type, hint, valid enum values, ranges, and current value |

**`get_node_properties` returns rich metadata per property:**
```json
{
  "name": "process_mode",
  "type": "Int",
  "hint": "Enum",
  "hint_string": "Inherit,Always,Pausable,WhenPaused,Always,Disabled",
  "value": "0"
}
```
This tells the AI exactly what values are valid — the AI should call this before `set_node_property` if it's unsure about a property.

---

### Node Creation

| Tool | Description |
|---|---|
| `create_node` | Create any Godot node by class name |
| `create_2d_node` | Create a 2D node (validates it's a CanvasItem subclass) with optional position |

**Any valid Godot class works** — `ClassDB.Instantiate()` handles the lookup internally. You're not limited to a list.

**Example prompts:**
- *"Add a CharacterBody2D called Player"*
- *"Create a Sprite2D called Background at [0, 0]"*
- *"Add a CollisionShape2D as a child of Player"*

**Node paths:** After creation, nodes are referred to by their **scene-relative path** (e.g. `Player`, `Player/Sprite2D`, `World/Enemies/Goblin`). The root node can be referred to by its name (e.g. `Character`) or left blank.

---

### Node Editing

| Tool | Description |
|---|---|
| `delete_node` | Delete a node by path |
| `set_node_property` | Set any property on a node — type-aware |

**`set_node_property` supports all Godot property types:**

| Property type | How to pass the value |
|---|---|
| `bool` | `true` or `false` |
| `int` / `float` | Number: `42`, `1.5` |
| `string` | String: `"hello"` |
| `Vector2` | Array: `[x, y]` e.g. `[100, 200]` |
| `Vector3` | Array: `[x, y, z]` |
| `Color` | Array `[r, g, b]` or `[r, g, b, a]` (0–1 range), or hex string `"#FF8833"` |
| `Rect2` | Array: `[x, y, width, height]` |
| `Enum` | Integer value (check `get_node_properties` hint_string for names→ints) |
| `NodePath` | String path |

**Example prompts:**
- *"Move player_head to position [200, 150]"*
- *"Set the modulate color of Background to red"*
- *"Hide the player_body node"*
- *"Set the scale of Player to [2, 2]"*

---

### Textures & Assets

| Tool | Description |
|---|---|
| `set_sprite_texture` | Assign an image as the texture of a Sprite2D node |

`image_path` can be:
- A `res://` path already inside the project: `"res://assets/hero.png"`
- An **absolute OS path** anywhere on your computer: `"/home/user/Downloads/hero.png"`

If an absolute path is given, the plugin automatically:
1. Copies the file into `res://assets/`
2. Triggers an editor filesystem scan (it shows up in FileSystem dock)
3. Loads and assigns the `Texture2D`

**Example prompts:**
- *"Use res://icon.svg as the texture for the player_head node"*
- *"There's an image at /home/mellow/Downloads/player.png — add it to the Sprite2D on Player"*

> **Note:** The node must be a `Sprite2D` or subclass. If you want to set a texture on a different node type (e.g. `TextureRect`), use `set_node_property` with property `"texture"` and a `res://` path string.

---

### File Operations

| Tool | Description |
|---|---|
| `list_project_files` | List files in the project (filters out `addons`, `docs`, `PlaywrightProfile`) |
| `run_shell_command` | Run any bash command on the host machine |

`run_shell_command` returns `exit_code`, `stdout`, and `stderr`.

**Example prompts:**
- *"List all files in the project"*
- *"Move /tmp/art.png into the project assets folder"*
- *"Delete res://old_scene.tscn from disk"*
- *"Copy all .png files from ~/Downloads/sprites into the project"*

**⚠️ Caution:** `run_shell_command` can run any bash command. The AI is instructed not to run destructive commands unless you explicitly ask, but be clear in your prompts.

---

## Editing the Tool Schema

All tool definitions (what tools exist, their parameters, descriptions) live in [`GodotTools.cs`](addons/godot_mcp/GodotTools.cs) in `GetToolDefinitions()`.

All tool implementations (the actual Godot API calls) live in [`McpHttpServer.cs`](addons/godot_mcp/McpHttpServer.cs) in `Dispatch()` and the methods below it.

**To add a new tool:**
1. Add a `Tool(name, description, parameters)` entry in `GetToolDefinitions()` in `GodotTools.cs`
2. Add routing in `ExecuteAsync()` in `GodotTools.cs` (either call `Call(action, params)` to forward to McpHttpServer, or handle it directly)
3. If forwarding: add the action to `Dispatch()` in `McpHttpServer.cs` and implement the method
4. Rebuild

---

## Editing the System Prompt

The system prompt (the AI's instructions and personality) is built in `GetSystemPrompt()` in [`ChatDock.cs`](addons/godot_mcp/ChatDock.cs). It includes:
- Mode instructions (Conversational vs Action)
- The `[CALL]` / `[RESULT]` tag format
- The full tool schema (injected automatically from `GodotTools.GetToolDefinitions()`)

Edit `GetSystemPrompt()` to change how the AI behaves. After editing, rebuild and click **clear** in the chat dock so the new prompt is sent on the next message.

---

## Tag Format

The AI communicates tool calls using bracket tags (not HTML angle brackets, which get eaten by the browser DOM):

```
[CALL]
{"tool": "create_node", "node_type": "Sprite2D", "node_name": "Background"}
[/CALL]
```

The plugin responds with:
```
[RESULT]
{"created": "Background", "type": "Sprite2D", "path": "Background"}
[/RESULT]
```

The AI can chain multiple tool calls — it outputs one `[CALL]`, gets a `[RESULT]`, then outputs the next `[CALL]`, up to 10 turns per user message.

---

## Architecture

| File | Responsibility |
|---|---|
| [`ChatDock.cs`](addons/godot_mcp/ChatDock.cs) | UI panel, input handling, slash commands, session management, tool call loop |
| [`AgentWrapper.cs`](addons/godot_mcp/AgentWrapper.cs) | Playwright browser controller — sends messages and reads responses from the AI web UI |
| [`GodotTools.cs`](addons/godot_mcp/GodotTools.cs) | Tool schema definitions + HTTP routing to McpHttpServer |
| [`McpHttpServer.cs`](addons/godot_mcp/McpHttpServer.cs) | Main-thread Godot API server — receives HTTP, executes tool commands, returns JSON |
| [`ChatService.cs`](addons/godot_mcp/ChatService.cs) | Thin wrapper around AgentWrapper |

---

## Known Limitations

- One tool call per AI turn (sequential, not parallel)
- Gemini's web UI is the target — selector changes in the Gemini frontend may break `AgentWrapper.cs`
- `set_sprite_texture` only works on `Sprite2D` nodes (use `set_node_property` + `"texture"` for others)
- The AI sometimes gets node paths wrong on the first try — if it does, it will call `get_scene_tree` to correct itself

---

## License

MIT
