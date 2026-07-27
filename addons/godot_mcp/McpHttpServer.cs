#if TOOLS
using Godot;
using Godot.Collections;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;

namespace GodotMCP;

// ---------------------------------------------------------------------------
// McpHttpServer — runs inside the Godot editor on the MAIN THREAD
//
// WHY THIS EXISTS:
//   The browser agent runs asynchronously (needed so the editor
//   doesn't freeze while waiting for the LLM). But Godot's editor APIs
//   (EditorInterface, SceneTree, node manipulation) are NOT thread-safe —
//   calling them from background asynchronous code can crash or corrupt state.
//
//   So we use a tiny HTTP server as a thread-boundary handoff:
//     1. The agent sends an HTTP request to localhost:9876
//     2. This server's _Process() method picks it up (main thread — safe!)
//     3. The actual Godot API call happens here
//     4. The response is sent back to the agent
//
// This file has two parts:
//   - The HTTP server itself (Start, Stop, _Process, HandleRequest)
//   - The tool implementations (CreateNode, SetNodeProperty, etc.)
// ---------------------------------------------------------------------------

[Tool]
public partial class McpHttpServer : Node
{
    // The plugin that owns this server (needed for some editor operations)
    public EditorPlugin? EditorPlugin { get; set; }

    // The actual port the server is listening on (set after Start() succeeds)
    public int Port => _port;

    // The TCP server that listens for connections
    private const int PreferredPort = 9876;
    private int _port = PreferredPort;
    private TcpServer? _tcpServer;
    private StreamPeerTcp? _client;

    // -----------------------------------------------------------------------
    // Start / Stop — called by GodotMcpPlugin when the plugin loads/unloads
    // -----------------------------------------------------------------------

    public void Start()
    {
        // Always create a fresh TcpServer here — field initializers on Godot
        // Node subclasses tagged with [Tool] can be silently reset by the
        // scripting bridge before _EnterTree runs, leaving the field null.
        //
        // NOTE: TcpServer.Listen() can throw a NullReferenceException from
        // Godot's native layer when the port is already in use (e.g. from a
        // prior crashed plugin load).  We catch that and try the next port.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int candidatePort = PreferredPort + attempt;
            try
            {
                _tcpServer = new TcpServer();
                Error err = _tcpServer.Listen((ushort)candidatePort);
                if (err == Error.Ok)
                {
                    _port = candidatePort;
                    GD.Print("[GodotMCP] HTTP server listening on port " + _port);
                    return;
                }
                GD.PrintErr("[GodotMCP] Port " + candidatePort + " unavailable (" + err + "), trying next…");
                _tcpServer.Stop();
            }
            catch (System.Exception ex)
            {
                GD.PrintErr("[GodotMCP] Exception trying port " + candidatePort + ": " + ex.Message + " — trying next…");
                try { _tcpServer?.Stop(); } catch { /* ignore */ }
            }
        }
        GD.PrintErr("[GodotMCP] Could not bind to any port in range " + PreferredPort + "–" + (PreferredPort + 9) + ". Server not started.");
        _tcpServer = null;
    }

    public void Stop()
    {
        _tcpServer?.Stop();
        if (_client != null)
        {
            _client.DisconnectFromHost();
            _client = null;
        }
    }

    // -----------------------------------------------------------------------
    // _Process — called every frame by Godot's main loop
    //
    // This checks if a client connected, reads their request, handles it,
    // sends the response, and disconnects. One request per frame, one client
    // at a time (which is fine since we're the only caller).
    // -----------------------------------------------------------------------

    public override void _Process(double delta)
    {
        // If the server hasn't been started yet, do nothing
        if (_tcpServer == null) return;

        // Check if a new client is trying to connect
        if (_tcpServer.IsConnectionAvailable())
        {
            _client = _tcpServer.TakeConnection();
        }

        // No client connected? Nothing to do.
        if (_client == null)
        {
            return;
        }

        // Client disconnected? Clean up and wait for next one.
        if (_client.GetStatus() != StreamPeerTcp.Status.Connected)
        {
            _client = null;
            return;
        }

        // Check if the client sent any data
        int availableBytes = (int)_client.GetAvailableBytes();
        if (availableBytes <= 0)
        {
            return;
        }

        // Read the raw HTTP request from the client
        string rawRequest = _client.GetString(availableBytes);

        // Process the request and get the HTTP response
        string response = HandleRequest(rawRequest);

        // Send the response back to the client
        byte[] responseBytes = Encoding.UTF8.GetBytes(response);
        _client.PutData(responseBytes);

        // Disconnect — we're done with this request
        _client.DisconnectFromHost();
        _client = null;
    }

    // -----------------------------------------------------------------------
    // HandleRequest — parses an HTTP request and routes it to Dispatch()
    //
    // The request from GodotTools.Call() looks like:
    //   POST / HTTP/1.1
    //   Content-Type: application/json
    //   Content-Length: 64
    //
    //   {"action":"create_node","params":{"node_type":"Sprite2D"}}
    //
    // We only care about the body (the JSON after the blank line).
    // -----------------------------------------------------------------------

    private string HandleRequest(string rawRequest)
    {
        // Find the blank line that separates HTTP headers from the body
        // HTTP headers end with \r\n\r\n
        int bodyStart = rawRequest.IndexOf("\r\n\r\n");

        string body;
        if (bodyStart >= 0)
        {
            // Everything after \r\n\r\n is the JSON body
            body = rawRequest.Substring(bodyStart + 4);
        }
        else
        {
            // No headers found — treat the whole thing as the body
            body = rawRequest;
        }

        try
        {
            // Parse the JSON body into a dictionary
            // Fully qualified because Godot also has a Dictionary class
            System.Collections.Generic.Dictionary<string, JsonElement>? request;
            try
            {
                request = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, JsonElement>>(body);
            }
            catch
            {
                request = default;
            }

            if (request == null)
            {
                string errorJson = JsonSerializer.Serialize(new { error = "Invalid request payload" });
                return HttpResponse(errorJson, 400);
            }

            // Extract the "action" field (e.g. "create_node")
            string action = "";
            if (request.TryGetValue("action", out JsonElement actionElement))
            {
                if (actionElement.ValueKind == JsonValueKind.String)
                {
                    action = actionElement.GetString() ?? "";
                }
            }

            // Extract the "params" field (the tool's arguments)
            JsonElement parameters = default;
            if (request.TryGetValue("params", out JsonElement paramsElement))
            {
                parameters = paramsElement;
            }

            // Route to the correct tool implementation
            string result = Dispatch(action, parameters);
            return HttpResponse(result);
        }
        catch (System.Exception ex)
        {
            string errorJson = JsonSerializer.Serialize(new { error = ex.Message });
            return HttpResponse(errorJson, 400);
        }
    }

    // -----------------------------------------------------------------------
    // Dispatch — routes an action string to the correct method
    //
    // This is the server-side equivalent of GodotTools.ExecuteAsync().
    // The action name MUST match what GodotTools sends.
    // -----------------------------------------------------------------------

    private string Dispatch(string action, JsonElement parameters)
    {
        if (action == "ping")
        {
            return Serialize(new { status = "ok", version = "0.1.0" });
        }
        else if (action == "get_scene_tree")
        {
            return GetSceneTree();
        }
        else if (action == "get_selected_nodes")
        {
            return GetSelectedNodes();
        }
        else if (action == "create_node")
        {
            return CreateNode(parameters);
        }
        else if (action == "create_2d_node")
        {
            return Create2DNode(parameters);
        }
        else if (action == "delete_node")
        {
            return DeleteNode(parameters);
        }
        else if (action == "set_node_property")
        {
            return SetNodeProperty(parameters);
        }
        else if (action == "get_node_properties")
        {
            return GetNodeProperties(parameters);
        }
        else if (action == "save_scene")
        {
            return SaveScene();
        }
        else if (action == "get_editor_state")
        {
            return GetEditorState();
        }
        else if (action == "list_project_files")
        {
            return ListProjectFiles(parameters);
        }
        else
        {
            return Serialize(new { error = "Unknown action: " + action });
        }
    }

    // =======================================================================
    // TOOL IMPLEMENTATIONS
    //
    // These are the methods that actually touch Godot's editor APIs.
    // Each one returns a JSON string (success or error).
    // =======================================================================

    // -- Get the full node tree of the currently open scene ----------------
    private string GetSceneTree()
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
        {
            return Serialize(new { error = "No scene open" });
        }

        // Convert the node tree into a nested dictionary structure
        Dictionary tree = NodeToDict(root);
        return Serialize(new { root = tree });
    }

    // Recursively converts a Node and its children into a dictionary:
    // { "name": "Player", "type": "CharacterBody2D", "path": "/root/Player", "children": [...] }
    private Dictionary NodeToDict(Node node)
    {
        Godot.Collections.Array<Dictionary> children = new Godot.Collections.Array<Dictionary>();

        int childCount = node.GetChildCount();
        for (int i = 0; i < childCount; i++)
        {
            Node child = node.GetChild(i);
            Dictionary childDict = NodeToDict(child);
            children.Add(childDict);
        }

        Dictionary result = new Dictionary
        {
            ["name"] = node.Name.ToString(),
            ["type"] = node.GetClass(),
            ["path"] = node.GetPath().ToString(),
            ["children"] = children
        };
        return result;
    }

    // -- Get the nodes currently selected in the editor --------------------
    private string GetSelectedNodes()
    {
        Godot.Collections.Array<Node> selected = EditorInterface.Singleton.GetSelection().GetSelectedNodes();

        // Build a list of { name, type, path } for each selected node
        List<object> nodeList = new List<object>();
        for (int i = 0; i < selected.Count; i++)
        {
            Node n = selected[i];
            nodeList.Add(new
            {
                name = n.Name.ToString(),
                type = n.GetClass(),
                path = n.GetPath().ToString()
            });
        }

        return Serialize(new { selected = nodeList });
    }

    // -- Create any node type in the current scene -------------------------
    private string CreateNode(JsonElement p)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
        {
            return Serialize(new { error = "No scene open. Create or open a scene first." });
        }

        // Read the parameters from the JSON
        string nodeType = GetStr(p, "node_type", "Node");
        string nodeName = GetStr(p, "node_name", nodeType);
        string parentPath = GetStr(p, "parent_path", "");

        // Find the parent node (defaults to the scene root)
        Node parent = root;
        if (parentPath != "")
        {
            Node found = root.GetNodeOrNull(parentPath);
            if (found == null)
            {
                return Serialize(new { error = "Parent not found: " + parentPath });
            }
            parent = found;
        }

        // Create the node using Godot's ClassDB (like Object.new() in GDScript)
        Node newNode = ClassDB.Instantiate(nodeType).As<Node>();
        if (newNode == null)
        {
            return Serialize(new { error = "Unknown node type: " + nodeType });
        }

        // Add it to the scene tree
        newNode.Name = nodeName;
        parent.AddChild(newNode);
        newNode.Owner = root;  // Required for the node to be saved with the scene

        return Serialize(new
        {
            created = nodeName,
            type = nodeType,
            path = newNode.GetPath().ToString()
        });
    }

    // -- Create a 2D node (specialized version with position support) ------
    //
    // Same as CreateNode but:
    //   - Defaults to Node2D
    //   - Validates that the type is a CanvasItem (2D node)
    //   - Optionally sets the initial position
    private string Create2DNode(JsonElement p)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
        {
            return Serialize(new { error = "No scene open. Create or open a scene first." });
        }

        string nodeType = GetStr(p, "node_type", "Node2D");
        string nodeName = GetStr(p, "node_name", nodeType);
        string parentPath = GetStr(p, "parent_path", "");

        // Find the parent node
        Node parent = root;
        if (parentPath != "")
        {
            Node found = root.GetNodeOrNull(parentPath);
            if (found == null)
            {
                return Serialize(new { error = "Parent not found: " + parentPath });
            }
            parent = found;
        }

        // Validate it's a 2D type — must be a CanvasItem subclass
        // (Node2D, Sprite2D, CharacterBody2D, Label, Button, etc. all inherit from CanvasItem)
        if (!ClassDB.ClassExists(nodeType))
        {
            return Serialize(new { error = "Unknown node type: " + nodeType });
        }

        if (!ClassDB.IsParentClass(nodeType, "CanvasItem"))
        {
            return Serialize(new
            {
                error = "Type '" + nodeType + "' is not a 2D node. " +
                        "create_2d_node only accepts CanvasItem-derived classes " +
                        "(Node2D, Sprite2D, CharacterBody2D, RigidBody2D, StaticBody2D, " +
                        "CollisionShape2D, Camera2D, Label, Button, etc.). " +
                        "Use create_node for non-2D types."
            });
        }

        // Create the node
        Node newNode = ClassDB.Instantiate(nodeType).As<Node>();
        if (newNode == null)
        {
            return Serialize(new { error = "Failed to instantiate: " + nodeType });
        }

        newNode.Name = nodeName;
        parent.AddChild(newNode);
        newNode.Owner = root;

        // If a position was provided, try to apply it
        string positionNote = "default";

        if (p.TryGetProperty("position", out JsonElement posEl))
        {
            if (posEl.ValueKind == JsonValueKind.Array)
            {
                // Read the [x, y] values from the array
                List<JsonElement> items = new List<JsonElement>();
                foreach (JsonElement item in posEl.EnumerateArray())
                {
                    items.Add(item);
                }

                if (items.Count >= 2)
                {
                    double x = items[0].GetDouble();
                    double y = items[1].GetDouble();

                    if (newNode is Node2D n2d)
                    {
                        // Node2D and subclasses (Sprite2D, CharacterBody2D, etc.)
                        n2d.Position = new Vector2((float)x, (float)y);
                        positionNote = "position=[" + x + ", " + y + "]";
                    }
                    else if (newNode is Control ctrl)
                    {
                        // Control and subclasses (Label, Button, Panel, etc.)
                        ctrl.Position = new Vector2((float)x, (float)y);
                        positionNote = "position=[" + x + ", " + y + "]";
                    }
                    else
                    {
                        positionNote = "position ignored (node type does not expose a 2D position)";
                    }
                }
            }
        }

        return Serialize(new
        {
            created = nodeName,
            type = nodeType,
            path = newNode.GetPath().ToString(),
            parent = parent.GetPath().ToString(),
            position = positionNote
        });
    }

    // -- Delete a node from the scene --------------------------------------
    private string DeleteNode(JsonElement p)
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

        string name = node.Name.ToString();
        node.QueueFree();  // Schedules the node for deletion at the end of the frame
        return Serialize(new { deleted = name });
    }

    // -- Set a property on a node ------------------------------------------
    //
    // Handles multiple value types:
    //   - string  → pass as-is
    //   - number  → pass as double/float
    //   - boolean → true/false
    //   - array   → try to parse as Vector2 [x, y]
    private string SetNodeProperty(JsonElement p)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
        {
            return Serialize(new { error = "No scene open" });
        }

        string path = GetStr(p, "path", "");
        string property = GetStr(p, "property", "");

        Node node = root.GetNodeOrNull(path);
        if (node == null)
        {
            return Serialize(new { error = "Node not found: " + path });
        }

        // Check if a "value" was provided
        if (p.TryGetProperty("value", out JsonElement val))
        {
            // Convert the JSON value to a Godot Variant based on its type
            // In Godot C#, Variant has implicit conversion from C# types —
            // you just assign the value directly, no "new Variant()" needed
            Variant godotVal;

            if (val.ValueKind == JsonValueKind.True)
            {
                godotVal = true;
            }
            else if (val.ValueKind == JsonValueKind.False)
            {
                godotVal = false;
            }
            else if (val.ValueKind == JsonValueKind.Number)
            {
                godotVal = val.GetDouble();
            }
            else if (val.ValueKind == JsonValueKind.String)
            {
                string strVal = val.GetString() ?? "";
                godotVal = strVal;
            }
            else if (val.ValueKind == JsonValueKind.Array)
            {
                // Arrays are treated as Vector2
                godotVal = ParseVector2(val);
            }
            else
            {
                godotVal = "";
            }

            node.Set(property, godotVal);
        }

        return Serialize(new { set = property, node = path });
    }

    // Parse a JSON array [x, y] into a Godot Vector2
    private Variant ParseVector2(JsonElement arr)
    {
        List<JsonElement> items = new List<JsonElement>();
        foreach (JsonElement item in arr.EnumerateArray())
        {
            items.Add(item);
        }

        if (items.Count >= 2)
        {
            float x = (float)items[0].GetDouble();
            float y = (float)items[1].GetDouble();
            return new Vector2(x, y);
        }

        return "";
    }

    // -- Get all properties of a node --------------------------------------
    private string GetNodeProperties(JsonElement p)
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

        // Iterate over all properties the node exposes
        // and build a dictionary of { property_name: value_as_string }
        Dictionary props = new Dictionary();
        Godot.Collections.Array<Godot.Collections.Dictionary> propertyList = node.GetPropertyList();

        for (int i = 0; i < propertyList.Count; i++)
        {
            Godot.Collections.Dictionary propInfo = (Godot.Collections.Dictionary)propertyList[i];

            // propInfo["name"] returns a Variant — we need to cast it to string
            string pname = propInfo["name"].AsString();

            // Skip internal/hidden properties (they start with underscore)
            if (pname.StartsWith("_"))
            {
                continue;
            }

            try
            {
                Variant propValue = node.Get(pname);
                props[pname] = propValue.ToString();
            }
            catch
            {
                // Some properties can't be read — skip them silently
            }
        }

        return Serialize(new { node = path, properties = props });
    }

    // -- Save the current scene to disk ------------------------------------
    private string SaveScene()
    {
        EditorInterface.Singleton.SaveScene();
        return Serialize(new { saved = true });
    }

    // -- Get the current editor state --------------------------------------
    private string GetEditorState()
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();

        bool hasOpenScene = root != null;
        string sceneName = "";
        string scenePath = "";

        if (root != null)
        {
            sceneName = root.Name.ToString();
            scenePath = root.SceneFilePath;
        }

        return Serialize(new
        {
            has_open_scene = hasOpenScene,
            scene_name = sceneName,
            scene_path = scenePath
        });
    }

    // -- List all files in a project directory -----------------------------
    private string ListProjectFiles(JsonElement p)
    {
        string dirPath = GetStr(p, "path", "res://");

        List<string> files = new List<string>();
        WalkDir(dirPath, files);

        return Serialize(new { path = dirPath, files = files });
    }

    // Recursively walk a directory and collect all file paths
    private void WalkDir(string path, List<string> output)
    {
        DirAccess dir = DirAccess.Open(path);
        if (dir == null)
        {
            return;
        }

        dir.ListDirBegin();
        string item = dir.GetNext();

        // dir.GetNext() returns "" when there are no more entries
        while (item != "")
        {
            // Skip hidden files and directories (starting with ".")
            if (!item.StartsWith("."))
            {
                string fullPath = path.PathJoin(item);

                if (dir.CurrentIsDir())
                {
                    // Recurse into subdirectories
                    WalkDir(fullPath, output);
                }
                else
                {
                    output.Add(fullPath);
                }
            }

            item = dir.GetNext();
        }
    }

    // =======================================================================
    // Helper methods
    // =======================================================================

    // Safely read a string from a JsonElement.
    // Returns the fallback if the key doesn't exist or isn't a string.
    private static string GetStr(JsonElement el, string key, string fallback)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty(key, out JsonElement v))
            {
                if (v.ValueKind == JsonValueKind.String)
                {
                    string? result = v.GetString();
                    if (result != null)
                    {
                        return result;
                    }
                }
            }
        }
        return fallback;
    }

    // Serialize any object to a JSON string with snake_case property names
    private static string Serialize(object obj)
    {
        JsonSerializerOptions opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        return JsonSerializer.Serialize(obj, opts);
    }

    // Build a minimal HTTP response with a JSON body
    private static string HttpResponse(string body, int code = 200)
    {

        int byteCount = Encoding.UTF8.GetByteCount(body);

        return "HTTP/1.1 " + code + " OK\r\n" +
               "Content-Type: application/json\r\n" +
               "Content-Length: " + byteCount + "\r\n" +
               "Access-Control-Allow-Origin: *\r\n" +
               "\r\n" +
               body;
    }
}
#endif