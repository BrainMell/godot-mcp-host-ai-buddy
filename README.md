# GodotMCP Host AI Buddy

A Godot 4 editor plugin that lets you control the Godot editor through natural language.
Type in the chat dock, and the AI calls real editor APIs — creating scenes, adding nodes,
setting properties — right in front of you.

---

## Quick start

1. Clone this repo and open it in Godot 4
2. Enable the plugin: **Project → Project Settings → Plugins → GodotMCP → Enable**
3. Copy `.env.example` to `.env` and paste your [Groq API key](https://console.groq.com)
4. Build the C# solution (`Ctrl+Shift+B`)
5. The **AI Buddy** dock appears at the bottom of the editor — start chatting

---

## How it works

The plugin has three moving parts that talk to each other in a loop:

```
You type in the ChatDock
        │
        ▼
┌─────────────────────────────────────────────────────────────┐
│  GroqAgent.cs                                               │
│                                                             │
│  1. Builds a JSON request to the Groq API containing:      │
│     - Your message                                          │
│     - The full list of available tools (from GodotTools)   │
└──────────────────────────┬──────────────────────────────────┘
                           │  HTTP POST → api.groq.com
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  Groq (LLM in the cloud)                                    │
│                                                             │
│  2. Reads your message + tool list                          │
│  3. Decides which tool to call (if any)                     │
│  4. Returns JSON like:                                      │
│     {                                                       │
│       "tool_calls": [{                                      │
│         "function": {                                       │
│           "name": "create_new_scene",                       │
│           "arguments": "{\"scene_path\":\"res://Main.tscn\"}"│
│         }                                                   │
│       }]                                                    │
│     }                                                       │
└──────────────────────────┬──────────────────────────────────┘
                           │  tool_calls JSON back
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  GroqAgent.cs (back in Godot)                               │
│                                                             │
│  5. Detects tool_calls in the response                      │
│  6. Calls GodotTools.ExecuteAsync("create_new_scene", ...)  │
└──────────────────────────┬──────────────────────────────────┘
                           │  HTTP POST → localhost:9876
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  McpHttpServer.cs  (also inside Godot, main thread)         │
│                                                             │
│  7. Receives request via its TCP server                     │
│  8. Dispatch() routes "create_new_scene" to CreateNewScene()│
│  9. Calls EditorInterface.Singleton.OpenSceneFromPath(...)  │
│     ← this is the REAL editor API call                      │
│  10. Returns result JSON                                    │
└──────────────────────────┬──────────────────────────────────┘
                           │  result back to GroqAgent
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  GroqAgent.cs                                               │
│                                                             │
│  11. Sends tool result back to Groq as a "tool" message     │
│  12. Groq generates a final human-readable reply            │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
                   ChatDock shows the reply
```

---

## File responsibilities

| File | Role |
|------|------|
| `ChatDock.cs` | The UI panel. Sends your message to `GroqAgent`, displays replies. |
| `GroqAgent.cs` | Orchestrator. Talks to Groq, detects `tool_calls` JSON, fires `GodotTools.ExecuteAsync`, feeds results back to Groq. |
| `GodotTools.cs` | **Schema + HTTP router.** Tells the LLM what tools exist (name, description, params). Routes tool calls to the local HTTP server. |
| `McpHttpServer.cs` | **Actual implementations.** Runs a TCP server on port 9876 inside the editor. Calls real Godot/EditorInterface APIs. |
| `GodotMcpPlugin.cs` | Plugin entry point. Spins up the HTTP server and chat dock when the editor loads. |

### Why is there an HTTP server for a local call?

`GroqAgent` runs on a **C# async/await task thread** (needed for non-blocking
HTTP calls to Groq). Godot's `EditorInterface`, `SceneTree`, and most editor
APIs are **not thread-safe** — touching them from a background thread will crash
or corrupt state.

`McpHttpServer._Process()` is called by Godot's **main game loop thread**, so
it's always safe to call editor APIs there. The HTTP hop over localhost is the
thread-boundary handoff: the async thread posts a request, the main thread
receives it via `_Process`, executes the editor call, and writes the response back.

### The LLM never runs any code

Groq just outputs structured JSON. It has no ability to execute anything itself.
The plugin reads `tool_calls` from the response and decides whether to act on it.
You could add an approval step before any tool executes — the architecture makes
that trivial.

---

## Adding new tools

See **[docs/ADDING_TOOLS.md](docs/ADDING_TOOLS.md)** for the step-by-step guide.
The short version: edit two files in four places, rebuild, done.

---

## Available tools

| Tool | What it does |
|------|--------------|
| `ping_godot` | Health check — is the plugin running? |
| `get_editor_state` | Is a scene open? What's its name/path? |
| `get_scene_tree` | Full node tree of the current scene |
| `get_selected_nodes` | Which nodes are selected in the editor |
| `create_node` | Create any node type in the current scene |
| `create_2d_node` | Create a 2D node with optional starting position |
| `delete_node` | Remove a node by path |
| `get_node_properties` | Inspect all properties on a node |
| `set_node_property` | Set a property (handles Vector2, bool, float, string) |
| `save_scene` | Save the current scene to disk |
| `list_project_files` | List files under any `res://` directory |
| `create_new_scene` | Create a new `.tscn` file and open it in the editor |
| `open_scene` | Open an existing scene file |

---

## Tech stack

- **Godot 4** (C# / .NET)
- **Groq API** (`llama-3.3-70b-versatile`) — fast, free tier available
- No external C# packages — only `System.Net.Http` and `System.Text.Json` from the BCL
