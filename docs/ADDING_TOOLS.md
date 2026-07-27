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
- **`GodotTools.cs`** — **schema + HTTP router.** Tells the LLM what tools
  exist (name, description, params) and forwards calls to the local server.
  **You add 2 things here.**
- **`McpHttpServer.cs`** — **actual implementations.** Runs inside Godot on
  the main thread and calls real `EditorInterface` / scene-tree APIs.
  **You add 2 things here.**

### Why HTTP for a localhost call?

`GroqAgent` runs on a **C# async/await task thread** (needed for non-blocking
calls to Groq). Godot's editor APIs — `EditorInterface`, `SceneTree`, node
mutation — are **not thread-safe**. Calling them from a background thread will
crash or silently corrupt state.

`McpHttpServer._Process()` is driven by Godot's **main game loop**, so it's
always safe to call editor APIs there. The HTTP hop is the thread-boundary
handoff: the async thread posts a request, the main thread picks it up in
`_Process`, does the real editor work, and writes the response back.

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

Open `GodotTools.cs` and find `GetToolDefinitions()` (starts around line 42).
Add your tool to the list using the `tools.Add(Tool(...))` pattern:

```csharp
tools.Add(Tool(
    name: "move_node",
    description: "Move a node to a new position. Works on Node2D and Control-derived nodes. " +
				 "For 3D nodes, use set_node_property with 'position' and a Vector3.",
    parameters: new
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
        required = new string[] { "path", "position" }
    }
));
```

**Schema helpers you can use** (defined at the bottom of `GodotTools.cs`):

| Helper | What it returns |
|--------|-----------------|
| `Tool(name, description, parameters)` | Wraps everything in the OpenAI function-tool format |
| `Prop("string", "description")` | A `{ type, description }` object — use for simple types |
| `EmptyParams()` | Use when the tool takes no parameters |

For complex param schemas (arrays, nested objects, numbers), inline the
anonymous object like the `position` example above.

### 2. Add the dispatch case

In the same file, find `ExecuteAsync()` (around line 200). Add an `else if`
branch to the routing chain:

```csharp
else if (toolName == "move_node")
{
    object positionValue = null;
    if (args.ContainsKey("position"))
    {
        positionValue = args["position"];
    }

    return await Call("move_node", new
    {
        path = Str(args, "path", ""),
        position = positionValue
    });
}
```

**Arg helpers** (defined at the bottom of `GodotTools.cs`):

| Helper | Behavior |
|--------|----------|
| `Str(args, "key", "fallback")` | Reads a string arg, returns fallback if missing or non-string |
| `args.TryGetValue("key", out var el)` | Use this for non-string args (arrays, numbers, bools) — check if the key exists, then pass the raw `JsonElement` |

The `Call(action, params)` helper POSTs to the HTTP server inside Godot and
returns the response as a string. You don't need to handle errors — `Call`
already returns a JSON error string if the server is unreachable.

### 3. Add the server-side dispatch case

Open `McpHttpServer.cs` and find `Dispatch()` (around line 160). Add an
`else if` branch:

```csharp
else if (action == "move_node")
{
	return MoveNode(parameters);
}
```

### 4. Write the implementation method

In the same file, add a new private method. Put it near the other tool
implementations (after `Create2DNode` is a good spot):

```csharp
private string MoveNode(JsonElement p)
{
	Node root = EditorInterface.Singleton.GetEditedSceneRoot();
	if (root == null)
	{
		return Serialize(new { error = "No scene open" });
	}

	string path = GetStr(p, "path", "");
	Node node = root.GetNodeOrNull(path);
	if (node == null)
	{
		return Serialize(new { error = "Node not found: " + path });
	}

	// Validate that "position" exists and is an array
	if (!p.TryGetProperty("position", out JsonElement posEl))
	{
		return Serialize(new { error = "position must be a [x, y] array" });
	}
	if (posEl.ValueKind != JsonValueKind.Array)
	{
		return Serialize(new { error = "position must be a [x, y] array" });
	}

	// Read the [x, y] values
	List<JsonElement> items = new List<JsonElement>();
	foreach (JsonElement item in posEl.EnumerateArray())
	{
		items.Add(item);
	}
	if (items.Count < 2)
	{
		return Serialize(new { error = "position must have at least 2 elements [x, y]" });
	}

	float x = (float)items[0].GetDouble();
	float y = (float)items[1].GetDouble();
	Vector2 newPos = new Vector2(x, y);

	// Apply the position based on node type
	if (node is Node2D n2d)
	{
		n2d.Position = newPos;
	}
	else if (node is Control ctrl)
	{
		ctrl.Position = newPos;
	}
	else
	{
		return Serialize(new
		{
			error = "Node '" + path + "' (type " + node.GetClass() + ") does not have a 2D position. " +
                    "Use set_node_property for other node types."
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

In `GetToolDefinitions()`:
```csharp
tools.Add(Tool(
    name: "get_fps",
    description: "Get the current editor FPS.",
    parameters: EmptyParams()
));
```

In `ExecuteAsync()`:
```csharp
else if (toolName == "get_fps")
{
    return await Call("get_fps", new { });
}
```

In `McpHttpServer.cs`:
```csharp
else if (action == "get_fps")
{
    return GetFps();
}

// ... (later in the file)

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
else if (toolName == "set_volume")
{
    double volume = 1.0;
    if (args.TryGetValue("volume", out JsonElement vEl))
    {
        if (vEl.ValueKind == JsonValueKind.Number)
        {
            volume = vEl.GetDouble();
        }
    }

    return await Call("set_volume", new { volume = volume });
}
```

```csharp
// In McpHttpServer.cs handler:
double volume = 1.0;
if (p.TryGetProperty("volume", out JsonElement vEl))
{
    if (vEl.ValueKind == JsonValueKind.Number)
    {
        volume = vEl.GetDouble();
    }
}
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

## Parameter Types and Property Editing Guide

When defining tools in `GodotTools.cs`, you can specify different parameter types in the JSON schema. Here is a comprehensive reference for mapping JSON schema parameters to C# types on both the client (`GodotTools.cs`) and server (`McpHttpServer.cs`) sides.

### 1. Schema Parameter Types Reference

#### String
- **Schema (`GodotTools.cs`):**
  ```csharp
  path = Prop("string", "Node path. E.g. 'Player'")
  ```
  *Or as an enum (restrict to predefined values):*
  ```csharp
  anchor = new { 
	  type = "string", 
	  @enum = new string[] { "top_left", "top_right", "bottom_left", "bottom_right" }, 
	  description = "Anchor preset location" 
  }
  ```
- **Client Dispatch (`GodotTools.cs`):**
  ```csharp
  path = Str(args, "path", "")
  ```
- **Server Implementation (`McpHttpServer.cs`):**
  ```csharp
  string path = GetStr(p, "path", "");
  ```

#### Number / Integer
- **Schema (`GodotTools.cs`):**
  ```csharp
  speed = Prop("number", "Movement speed multiplier")
  // or
  count = Prop("integer", "Number of nodes to spawn")
  ```
- **Client Dispatch (`GodotTools.cs`):**
  ```csharp
  double speed = 1.0;
  if (args.TryGetValue("speed", out var speedEl) && speedEl.ValueKind == JsonValueKind.Number) {
	  speed = speedEl.GetDouble();
  }
  ```
- **Server Implementation (`McpHttpServer.cs`):**
  ```csharp
  double speed = 1.0;
  if (p.TryGetProperty("speed", out var speedEl) && speedEl.ValueKind == JsonValueKind.Number) {
	  speed = speedEl.GetDouble();
  }
  ```

#### Boolean
- **Schema (`GodotTools.cs`):**
  ```csharp
  visible = Prop("boolean", "Whether the node should be visible")
  ```
- **Client Dispatch (`GodotTools.cs`):**
  ```csharp
  bool visible = true;
  if (args.TryGetValue("visible", out var visEl)) {
	  visible = visEl.ValueKind == JsonValueKind.True;
  }
  ```
- **Server Implementation (`McpHttpServer.cs`):**
  ```csharp
  bool visible = true;
  if (p.TryGetProperty("visible", out var visEl)) {
	  visible = visEl.ValueKind == JsonValueKind.True;
  }
  ```

#### Array
- **Schema (`GodotTools.cs`):**
  ```csharp
  tags = new { 
	  type = "array", 
	  items = new { type = "string" }, 
	  description = "List of node tags" 
  }
  ```
- **Client Dispatch (`GodotTools.cs`):**
  Pass the array object directly to the HTTP payload:
  ```csharp
  tags = args.ContainsKey("tags") ? args["tags"] : null
  ```
- **Server Implementation (`McpHttpServer.cs`):**
  Iterate over the array elements:
  ```csharp
  if (p.TryGetProperty("tags", out JsonElement tagsEl) && tagsEl.ValueKind == JsonValueKind.Array) {
	  foreach (JsonElement tagEl in tagsEl.EnumerateArray()) {
		  string tag = tagEl.GetString() ?? "";
	  }
  }
  ```

---

### 2. Handling Positions and Vectors

Positions are normally passed as JSON number arrays (e.g., `[x, y]` for 2D, or `[x, y, z]` for 3D).

#### Vector2 (2D Positions)
- **Schema (`GodotTools.cs`):**
  ```csharp
  position = new {
	  type = "array",
	  items = new { type = "number" },
	  description = "2D coordinates as [x, y]"
  }
  ```
- **Client Dispatch (`GodotTools.cs`):**
  ```csharp
  position = args.ContainsKey("position") ? args["position"] : null
  ```
- **Server Parse Helper (`McpHttpServer.cs`):**
  ```csharp
  private Vector2 ParseVector2(JsonElement arr)
  {
	  List<JsonElement> items = new List<JsonElement>();
	  foreach (JsonElement item in arr.EnumerateArray()) {
		  items.Add(item);
	  }
	  if (items.Count >= 2) {
		  float x = (float)items[0].GetDouble();
		  float y = (float)items[1].GetDouble();
		  return new Vector2(x, y);
	  }
	  return Vector2.Zero;
  }
  ```

#### Vector3 (3D Positions)
- **Schema (`GodotTools.cs`):**
  ```csharp
  position_3d = new {
	  type = "array",
	  items = new { type = "number" },
	  description = "3D coordinates as [x, y, z]"
  }
  ```
- **Client Dispatch (`GodotTools.cs`):**
  ```csharp
  position_3d = args.ContainsKey("position_3d") ? args["position_3d"] : null
  ```
- **Server Parse Helper (`McpHttpServer.cs`):**
  ```csharp
  private Vector3 ParseVector3(JsonElement arr)
  {
	  List<JsonElement> items = new List<JsonElement>();
	  foreach (JsonElement item in arr.EnumerateArray()) {
		  items.Add(item);
	  }
	  if (items.Count >= 3) {
		  float x = (float)items[0].GetDouble();
		  float y = (float)items[1].GetDouble();
		  float z = (float)items[2].GetDouble();
		  return new Vector3(x, y, z);
	  }
	  return Vector3.Zero;
  }
  ```

---

### 3. Editing Node Properties

Godot properties are set dynamically using `node.Set(propertyName, variantValue)`.

#### The Generic Property Setter Pattern
You can implement a tool that allows general property modifications by translating incoming JSON types to Godot `Variant` types. Here is the pattern used in `set_node_property`:

1. **Check value JSON type:** Use `ValueKind` to differentiate JSON types.
2. **Convert to C# type:** Map JSON booleans/numbers/strings directly, and arrays to `Vector2` / `Vector3` / `Color`.
3. **Apply via `node.Set()`:** Godot's C# API allows assigning native types directly to `Variant` structs.

```csharp
private string SetNodeProperty(JsonElement p)
{
    // ... load node path ...
    if (p.TryGetProperty("value", out JsonElement val)) {
        Variant godotVal;

        if (val.ValueKind == JsonValueKind.True) {
            godotVal = true;
        }
        else if (val.ValueKind == JsonValueKind.False) {
            godotVal = false;
        }
        else if (val.ValueKind == JsonValueKind.Number) {
            godotVal = val.GetDouble();
        }
        else if (val.ValueKind == JsonValueKind.String) {
            godotVal = val.GetString() ?? "";
        }
        else if (val.ValueKind == JsonValueKind.Array) {
            // Translate arrays into Vector2 (or Color/Vector3 depending on property length)
            godotVal = ParseVector2(val);
        }
        else {
            godotVal = "";
        }

        node.Set(property, godotVal);
    }
    return Serialize(new { set = property, node = path });
}
```

#### Color Properties
If you need to edit color properties (like modulation or light colors), pass them as `[r, g, b, a]` or `[r, g, b]` arrays (values ranging `0.0` to `1.0`):

- **Schema:**
  ```csharp
  color = new {
      type = "array",
      items = new { type = "number" },
      description = "Color as [r, g, b, a] or [r, g, b]"
  }
  ```
- **Server Parser:**
  ```csharp
  private Color ParseColor(JsonElement arr)
  {
      List<float> c = new List<float>();
      foreach (JsonElement el in arr.EnumerateArray()) {
          c.Add((float)el.GetDouble());
      }
      if (c.Count == 3) {
          return new Color(c[0], c[1], c[2]);
      }
      if (c.Count >= 4) {
          return new Color(c[0], c[1], c[2], c[3]);
      }
      return Colors.White; // fallback
  }
  ```

---


## Gotchas

1. **The tool name in `GodotTools.cs` must match the action name in
   `McpHttpServer.cs`.** The `Call(action, params)` helper passes the first
   argument as the `action` field in the HTTP body, and `Dispatch()` matches
   on that string. If they don't match, you'll get `Unknown action: ...`.

2. **Don't forget `#if TOOLS` boundaries.** All four files already have
   `#if TOOLS` / `#endif` guards — keep your additions inside them. Tools only
   exist in the editor, not in exported builds.

3. **The LLM only sees the description you write.** Be specific. "Create a
   node" is bad. "Create a new 2D node in the current scene. Defaults to
   Node2D. Use this when the user asks for a 2D node, sprite, character, or
   anything that lives in 2D space." is good. Mention common use cases and
   constraints.

4. **Required vs optional params.** In the JSON schema, `required = new string[] { "path" }`
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
| `ping_godot` | Health check | `GodotTools.cs` ~line 48 |
| `get_editor_state` | Is a scene open? What's it called? | ~line 53 |
| `get_scene_tree` | Full node tree as JSON | ~line 58 |
| `get_selected_nodes` | What's selected in the editor | ~line 63 |
| `create_node` | Generic node creator (any type) | ~line 68 |
| `create_2d_node` | 2D-specialized creator (Node2D + position) | ~line 89 |
| `delete_node` | Remove a node | ~line 122 |
| `get_node_properties` | Inspect all props on a node | ~line 131 |
| `set_node_property` | Set a single prop (handles Vector2, bool, etc.) | ~line 140 |
| `save_scene` | Save the current scene | ~line 157 |
| `list_project_files` | Walk `res://` recursively | ~line 163 |
| `create_new_scene` | Create a `.tscn` file and open it in the editor | ~line 173 |
| `open_scene` | Open an existing scene by path | ~line 191 |

`create_2d_node` is the cleanest reference for a tool that has optional params,
type validation, and returns a rich response — copy that pattern when in doubt.
