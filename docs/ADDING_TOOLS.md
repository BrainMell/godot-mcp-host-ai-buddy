# Adding a new tool to GodotMCP

This guide walks you through adding a new tool end-to-end. You'll touch **four
places** (two files), and there's a known-good pattern to copy from
(`create_2d_node`).

The whole flow takes about 5 minutes once you know the pattern.

---

## How tools work (quick architecture)

```
┌──────────────┐   tool_call    ┌───────────────┐  HTTP POST   ┌─────────────────┐
│  GroqAgent   │ ─────────────► │  GodotTools   │ ───────────► │  McpHttpServer  │
│  (LLM chat)  │                │  (definitions │              │  (in Godot,     │
│              │ ◄───────────── │   + dispatch) │ ◄─────────── │   executes it)  │
└──────────────┘   tool_result  └───────────────┘   JSON resp  └─────────────────┘
```

- **`GroqAgent.cs`** — LLM chat loop. You don't touch this when adding a tool.
  It automatically picks up new tools via `GodotTools.GetToolDefinitions()`.
- **`GodotTools.cs`** — client side. Defines what tools exist (schema the LLM
  sees) and forwards calls to the HTTP server. **You add 2 things here.**
- **`McpHttpServer.cs`** — server side (runs inside Godot). Actually executes
  the tool against the editor. **You add 2 things here.**

The four touch points:

| # | File | What to add |
|---|------|-------------|
| 1 | `addons/godot_mcp/GodotTools.cs` → `GetToolDefinitions()` | Tool schema (name, description, params) |
| 2 | `addons/godot_mcp/GodotTools.cs` → `ExecuteAsync()` | Dispatch case: map tool name → HTTP call |
| 3 | `addons/godot_mcp/McpHttpServer.cs` → `Dispatch()` | Dispatch case: map action name → C# method |
| 4 | `addons/godot_mcp/McpHttpServer.cs` | New private method that does the actual work |

---

## Worked example: `move_node`

Let's say you want to add a `move_node` tool that takes a node path and a new
position `[x, y]` and moves the node there. Here are all 4 changes.

### 1. Add the tool definition

Open `GodotTools.cs` and find `GetToolDefinitions()` (starts around line 18).
Add your tool to the list:

```csharp
Tool("move_node",
    "Move a node to a new position. Works on Node2D and Control-derived nodes. " +
    "For 3D nodes, use set_node_property with 'position' and a Vector3.",
    new
    {
        type = "object",
        properties = new
        {
            path = Prop("string", "Node path. E.g. 'Player' or 'Player/Sprite2D'"),
            position = new
            {
                type = "array",
                description = "New position as [x, y]. E.g. [200, 150]",
                items = new { type = "number" }
            }
        },
        required = new[] { "path", "position" }
    }),
```

**Schema helpers you can use** (already defined at the bottom of `GodotTools.cs`):

| Helper | What it returns |
|--------|-----------------|
| `Tool(name, description, parameters)` | Wraps everything in the OpenAI function-tool format |
| `Prop("string", "description")` | A `{ type, description }` object — use for simple types |
| `EmptyParams()` | Use when the tool takes no parameters |

For complex param schemas (arrays, nested objects, numbers), inline the
anonymous object like the `position` example above.

### 2. Add the dispatch case

In the same file, find `ExecuteAsync()` (around line 150). Add a case to the
`switch` expression:

```csharp
"move_node" => await Call("move_node", new
{
    path     = Str(args, "path", ""),
    position = args.TryGetValue("position", out var posEl) ? (object)posEl : (object)null
}),
```

**Arg helpers** (defined at the bottom of `GodotTools.cs`):

| Helper | Behavior |
|--------|----------|
| `Str(args, "key", "fallback")` | Reads a string arg, returns fallback if missing or non-string |
| `args.TryGetValue("key", out var el)` | Use this for non-string args (arrays, numbers, bools) — passes the raw `JsonElement` through |

The `Call(action, params)` helper POSTs to the HTTP server inside Godot and
returns the response as a string. You don't need to handle errors — `Call`
already returns a JSON error string if the server is unreachable.

### 3. Add the server-side dispatch case

Open `McpHttpServer.cs` and find `Dispatch()` (around line 74). Add a case:

```csharp
"move_node" => MoveNode(parameters),
```

### 4. Write the implementation method

In the same file, add a new private method. Put it near the other tool
implementations (after `Create2DNode` is a good spot):

```csharp
private string MoveNode(JsonElement p)
{
    var root = EditorInterface.Singleton.GetEditedSceneRoot();
    if (root == null) return Serialize(new { error = "No scene open" });

    string path = GetStr(p, "path", "");
    var node = root.GetNodeOrNull(path);
    if (node == null) return Serialize(new { error = $"Node not found: {path}" });

    if (!p.TryGetProperty("position", out var posEl) || posEl.ValueKind != JsonValueKind.Array)
        return Serialize(new { error = "position must be a [x, y] array" });

    var items = System.Linq.Enumerable.ToList(posEl.EnumerateArray());
    if (items.Count < 2)
        return Serialize(new { error = "position must have at least 2 elements [x, y]" });

    var newPos = new Vector2(
        (float)items[0].GetDouble(),
        (float)items[1].GetDouble());

    switch (node)
    {
        case Node2D n2d:
            n2d.Position = newPos;
            break;
        case Control ctrl:
            ctrl.Position = newPos;
            break;
        default:
            return Serialize(new
            {
                error = $"Node '{path}' (type {node.GetClass()}) does not have a 2D position. " +
                        $"Use set_node_property for other node types."
            });
    }

    return Serialize(new
    {
        moved = path,
        to = new { x = newPos.X, y = newPos.Y }
    });
}
```

**Server-side helpers** (defined at the bottom of `McpHttpServer.cs`):

| Helper | What it does |
|--------|--------------|
| `GetStr(p, "key", "fallback")` | Same as the client-side `Str` — reads a string arg |
| `Serialize(obj)` | Serializes to JSON with `snake_case` property names (matches what `GodotTools` expects) |

Always return a JSON string. The convention is:
- **Success**: return an object describing what happened (`Serialize(new { moved = path, ... })`)
- **Error**: return `Serialize(new { error = "human-readable message" })` — the LLM will see this and explain it to the user

---

## That's it — 4 edits, 2 files

After saving, **rebuild the C# solution in Godot** (Build menu or
`Ctrl+Shift+B`). The new tool automatically shows up in the next chat — the
`GroqAgent` calls `GetToolDefinitions()` on every API request, so there's no
registration step.

### Test it

1. Open a 2D scene in Godot
2. In the chat dock, type: `move the Player node to [300, 200]`
3. The agent should call `move_node(path="Player", position=[300, 200])`
4. The node should jump to the new position in the editor viewport

You can also verify the tool is registered by checking Godot's Output panel —
when a tool is called, `GroqAgent` logs `[GodotMCP] Tool call: move_node({...})`.

---

## Common patterns

### Tool with no parameters

```csharp
Tool("get_fps",
    "Get the current editor FPS.",
    EmptyParams()),
```

```csharp
"get_fps" => await Call("get_fps", new { }),
```

```csharp
"get_fps" => GetFps(),

// ...

private string GetFps()
{
    return Serialize(new { fps = Engine.GetFramesPerSecond() });
}
```

### Tool that takes a number or boolean

The `Str()` helper only works for strings. For other types, read the
`JsonElement` directly:

```csharp
// In GodotTools.cs ExecuteAsync():
"set_volume" => await Call("set_volume", new
{
    volume = args.TryGetValue("volume", out var v) && v.ValueKind == JsonValueKind.Number
        ? v.GetDouble()
        : 1.0
}),
```

```csharp
// In McpHttpServer.cs handler:
double volume = 1.0;
if (p.TryGetProperty("volume", out var vEl) && vEl.ValueKind == JsonValueKind.Number)
    volume = vEl.GetDouble();
```

### Tool that modifies the scene tree

Always set `newNode.Owner = root;` after `parent.AddChild(newNode)` — otherwise
the node won't be saved with the scene. Look at `CreateNode` /
`Create2DNode` for the full pattern.

### Tool that needs to run on the main thread

Everything in `McpHttpServer._Process()` already runs on the main thread, so
you don't need `CallDeferred` for normal editor operations. If you're doing
something exotic (loading resources, importing), check the existing
`SaveScene()` implementation.

---

## Gotchas

1. **The tool name in `GodotTools.cs` must match the action name in
   `McpHttpServer.cs`.** The `Call(action, params)` helper passes the first
   argument as the `action` field in the HTTP body, and `Dispatch()` switches
   on that string. If they don't match, you'll get `Unknown action: ...`.

2. **Don't forget `#if TOOLS` boundaries.** All four files already have
   `#if TOOLS` / `#endif` guards — keep your additions inside them. Tools only
   exist in the editor, not in exported builds.

3. **The LLM only sees the description you write.** Be specific. "Create a
   node" is bad. "Create a new 2D node in the current scene. Defaults to
   Node2D. Use this when the user asks for a 2D node, sprite, character, or
   anything that lives in 2D space." is good. Mention common use cases and
   constraints.

4. **Required vs optional params.** In the JSON schema, `required = new[] { "path" }`
   means the LLM must provide that param. Params not in `required` are
   optional — use `Str(args, "key", "default")` to handle their absence.

5. **Error messages go back to the LLM.** When you return
   `Serialize(new { error = "..." })`, the LLM sees it and will try to fix
   the issue or explain it to the user. Make error messages actionable:
   include what went wrong + what to do instead.

6. **Rebuild after every change.** C# in Godot doesn't hot-reload. Save →
   Build → the new tool is live.

---

## Reference: the existing tools

| Tool | What it does | Where to look |
|------|--------------|---------------|
| `ping_godot` | Health check | `GodotTools.cs` ~line 20 |
| `get_editor_state` | Is a scene open? What's it called? | ~line 24 |
| `get_scene_tree` | Full node tree as JSON | ~line 28 |
| `get_selected_nodes` | What's selected in the editor | ~line 32 |
| `create_node` | Generic node creator (any type) | ~line 36 |
| `create_2d_node` | 2D-specialized creator (Node2D + position) | ~line 57 |
| `delete_node` | Remove a node | ~line 89 |
| `get_node_properties` | Inspect all props on a node | ~line 99 |
| `set_node_property` | Set a single prop (handles Vector2, bool, etc.) | ~line 111 |
| `save_scene` | Save the current scene | ~line 126 |
| `list_project_files` | Walk `res://` recursively | ~line 130 |

`create_2d_node` is the cleanest reference for a tool that has optional params,
type validation, and returns a rich response — copy that pattern when in doubt.
