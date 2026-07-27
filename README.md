# GodotMCP Host AI Buddy

A Godot 4 editor plugin that lets you control the Godot editor through natural language using browser-driven Google Gemini. Type in the chat dock, and the AI calls real editor APIs — creating scenes, adding nodes, setting properties — right in front of you.

---

## Quick start

1. Clone this repo and open it in Godot 4.
2. Build the C# solution in the editor or run `dotnet build` in the root folder.
3. Enable the plugin: **Project → Project Settings → Plugins → GodotMCP → Enable**.
4. The first time you send a message, a headed Chromium window will open:
   - **Log into your Google account** on the Gemini page.
   - Once signed in and you see the chat interface, close the browser or return to Godot.
   - All subsequent prompts will run in **stealth headless mode** in the background, utilizing your saved session profile.
5. The **AI Buddy** dock appears at the bottom of the editor — start chatting!

---

## How it works

The plugin has three moving parts that talk to each other in a loop:

```
You type in the ChatDock
        │
        ▼
┌─────────────────────────────────────────────────────────────┐
│  ChatDock.cs (Orchestrator)                                 │
│                                                             │
│  1. Primes the session on first run with the system prompt   │
│     and available tools schema.                             │
│  2. Sends user prompts and loops tool results.              │
└──────────────────────────┬──────────────────────────────────┘
                           │  SendMessageAsync()
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  AgentWrapper.cs (ChatService)                              │
│                                                             │
│  3. Automates a Chromium instance via Playwright.           │
│  4. Enters stealth headless mode after initial login.       │
│  5. Inputs prompts into Gemini and extracts streamed replies.│
└──────────────────────────┬──────────────────────────────────┘
                           │  Browser UI Automation
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  Google Gemini Web App (LLM)                                │
│                                                             │
│  6. Receives instructions and decides to trigger a tool.    │
│  7. Returns array-wrapped JSON like:                        │
│     [CALL]                                                  │
│       {"tool": "create_new_scene", "root_name": "Player"}   │
│     [/CALL]                                                 │
└──────────────────────────┬──────────────────────────────────┘
                           │  Raw reply parsed via Regex
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  GodotTools.cs (Schema + HTTP Client)                       │
│                                                             │
│  8. Matches the `[CALL]` block and deserializes arguments.  │
│  9. Sends tool call HTTP request to localhost:9876.         │
└──────────────────────────┬──────────────────────────────────┘
                           │  HTTP POST → localhost:9876
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  McpHttpServer.cs (Main Godot Thread Server)                │
│                                                             │
│  10. Receives request and dispatches to Godot's API.        │
│  11. Executes the actual editor changes (e.g. adding node). │
│  12. Returns the result JSON back to the ChatDock loop.     │
└──────────────────────────┬──────────────────────────────────┘
                           │  Loops result back as [RESULT]
                           ▼
                       (Repeat)
```

---

## File responsibilities

| File | Role |
|------|------|
| [ChatDock.cs](file:///home/mellow/godot-mcp-host-ai-buddy/addons/godot_mcp/ChatDock.cs) | **UI & Loop Orchestrator.** Handles the input/output message logs, manages multi-turn agent logic, primes Gemini with system prompt definitions, parses `[CALL]` blocks, and runs tools. |
| [AgentWrapper.cs](file:///home/mellow/godot-mcp-host-ai-buddy/addons/godot_mcp/AgentWrapper.cs) | **Browser Controller.** Interfaces with Playwright. Handles stealth headless configurations, viewport setup, session persistence, login verification, and message sending. |
| [GodotTools.cs](file:///home/mellow/godot-mcp-host-ai-buddy/addons/godot_mcp/GodotTools.cs) | **Schema + HTTP Router.** Defines the schemas of all Godot editor tools that the AI can call. Standardizes outgoing HTTP requests to the main thread server. |
| [McpHttpServer.cs](file:///home/mellow/godot-mcp-host-ai-buddy/addons/godot_mcp/McpHttpServer.cs) | **Actual Implementations.** Runs a TCP server on port 9876 on Godot's main thread. Modifies the scene tree and executes EditorInterface calls safely. |
| [GodotMcpPlugin.cs](file:///home/mellow/godot-mcp-host-ai-buddy/addons/godot_mcp/GodotMcpPlugin.cs) | **Plugin Entry Point.** Initializes the HTTP server and mounts the chat dock panel on editor startup, cleaning them up on exit. |

---

## Tool Execution Loop

The AI automatically chooses between two modes of operation:
- **Conversational Mode**: For general advice, game design explanations, and questions. The AI replies in clean markdown.
- **Action Mode**: For modifying the scene or checking files. The AI outputs a single `[CALL]...[/CALL]` JSON block.
  - The loop automatically runs the tool, retrieves the output, and feeds it back as `[RESULT]...[/RESULT]`.
  - The loop runs up to 10 consecutive times so the AI can execute complex chains of actions (like creating a scene, adding multiple nodes, and saving) in one go.

---

## Features

- **No API Keys Needed**: Reuses your standard logged-in Google browser session on Gemini.
- **Stealth Headless Mode**: Runs silently in the background after initial setup.
- **Assembly Reload Tolerant**: Implements active cancellation tokens and strict process cleanup on plugin unload to prevent Godot from hanging when compiling.
- **Copy Log**: Instant one-click clipboard copying of the chat log (automatically stripping BBCode formatting).
- **Session Priming**: System instructions and schemas are sent once at the start of a conversation, keeping subsequent user prompts lightweight and fast.

---

## Available tools

| Tool | What it does |
|------|--------------|
| `ping_godot` | Health check — is the server responding? |
| `get_editor_state` | Retrieves open scene name and path. |
| `get_scene_tree` | Lists the entire node hierarchy of the active scene. |
| `get_selected_nodes` | Tells which nodes are currently highlighted. |
| `create_node` | Generates a new node of any Type in the scene. |
| `create_2d_node` | Utility helper to instantiate 2D nodes at coordinates. |
| `delete_node` | Frees a node by its path. |
| `get_node_properties` | Inspects properties on a selected node. |
| `set_node_property` | Edits properties (Vector2, floats, strings, booleans). |
| `save_scene` | Saves current scene changes. |
| `list_project_files` | Scans directories under `res://`. |
| `create_new_scene` | Builds a new scene with root type and opens it. |
| `open_scene` | Switches the editor to open a different scene file. |

---

## Tech stack

- **Godot 4** (C# / .NET 8+)
- **Playwright for .NET** (Chromium browser controller)
- **Google Gemini Web App** (Free-tier web interface)
