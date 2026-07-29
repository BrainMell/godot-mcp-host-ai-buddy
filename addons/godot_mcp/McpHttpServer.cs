#if TOOLS
using Godot;
using Godot.Collections;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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
    private static readonly List<McpHttpServer> _activeServers = new List<McpHttpServer>();

    public McpHttpServer()
    {
        lock (_activeServers)
        {
            _activeServers.Add(this);
            if (_activeServers.Count == 1)
            {
                var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(McpHttpServer).Assembly);
                if (alc != null)
                {
                    alc.Unloading += OnAssemblyUnloading;
                }
            }
        }
    }

    private static void OnAssemblyUnloading(System.Runtime.Loader.AssemblyLoadContext context)
    {
        GD.Print("[GodotMCP] Assembly unloading. Stopping active McpHttpServer listeners...");
        List<McpHttpServer> toStop;
        lock (_activeServers)
        {
            toStop = new List<McpHttpServer>(_activeServers);
            _activeServers.Clear();
        }
        foreach (var server in toStop)
        {
            try
            {
                server.Stop();
            }
            catch (Exception ex)
            {
                GD.PrintErr("[GodotMCP] Error during McpHttpServer assembly unloading cleanup: " + ex.Message);
            }
        }
    }

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
        lock (_activeServers)
        {
            _activeServers.Remove(this);
        }
        if (_tcpServer != null)
        {
            _tcpServer.Stop();
            _tcpServer = null;
        }
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
        else if (action == "create_new_scene")
        {
            return CreateNewScene(parameters);
        }
        else if (action == "open_scene")
        {
            return OpenScene(parameters);
        }
        else if (action == "set_sprite_texture")
        {
            return SetSpriteTexture(parameters);
        }
        else if (action == "list_script_templates")
        {
            return ListScriptTemplates();
        }
        else if (action == "read_file")
        {
            return ReadFile(parameters);
        }
        else if (action == "write_file")
        {
            return WriteFile(parameters);
        }
        else if (action == "attach_script")
        {
            return AttachScript(parameters);
        }
        else if (action == "create_and_attach_script")
        {
            return CreateAndAttachScript(parameters);
        }
        else if (action == "instantiate_subscene")
        {
            return InstantiateSubscene(parameters);
        }
        else if (action == "create_tiled_background")
        {
            return CreateTiledBackground(parameters);
        }
        else
        {
            return Serialize(new { error = "Unknown action: " + action });
        }
    }

    // -- List script templates ------------------------------------------------
    private string ListScriptTemplates()
    {
        string templatesResPath = "res://addons/godot_mcp/templates";
        string templatesDirOs   = ProjectSettings.GlobalizePath(templatesResPath);

        if (!Directory.Exists(templatesDirOs))
            return Serialize(new { error = "Templates directory not found: " + templatesResPath });

        var templates = new List<object>();
        foreach (string file in Directory.GetFiles(templatesDirOs, "*.gd"))
        {
            string fileName = Path.GetFileName(file);
            string resPath  = templatesResPath + "/" + fileName;

            // Read the first comment line as a description
            string description = "";
            try
            {
                string firstLine = File.ReadLines(file).FirstOrDefault() ?? "";
                if (firstLine.StartsWith("# TEMPLATE:"))
                    description = firstLine.Substring("# TEMPLATE:".Length).Trim();
                else if (firstLine.StartsWith("#"))
                    description = firstLine.Substring(1).Trim();
            }
            catch { /* ignore */ }

            templates.Add(new { file = resPath, description });
        }

        return Serialize(new { templates, count = templates.Count });
    }

    // -- Read any project file -----------------------------------------------
    private string ReadFile(JsonElement p)
    {
        string resPath = GetStr(p, "path", "");
        if (string.IsNullOrEmpty(resPath))
            return Serialize(new { error = "path is required" });

        string osPath = ProjectSettings.GlobalizePath(resPath);
        if (!File.Exists(osPath))
            return Serialize(new { error = "File not found: " + resPath });

        try
        {
            string content = File.ReadAllText(osPath);
            return Serialize(new { path = resPath, content, lines = content.Split('\n').Length });
        }
        catch (Exception ex)
        {
            return Serialize(new { error = "Failed to read file: " + ex.Message });
        }
    }

    // -- Write any project file ----------------------------------------------
    private string WriteFile(JsonElement p)
    {
        string resPath = GetStr(p, "path", "");
        string content = GetStr(p, "content", "");

        if (string.IsNullOrEmpty(resPath))
            return Serialize(new { error = "path is required" });

        string osPath = ProjectSettings.GlobalizePath(resPath);

        try
        {
            // Ensure the directory exists
            string? dir = Path.GetDirectoryName(osPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(osPath, content);

            // Notify the editor filesystem so the file appears immediately
            EditorInterface.Singleton.GetResourceFilesystem().Scan();

            return Serialize(new
            {
                written = resPath,
                bytes   = System.Text.Encoding.UTF8.GetByteCount(content),
                lines   = content.Split('\n').Length
            });
        }
        catch (Exception ex)
        {
            return Serialize(new { error = "Failed to write file: " + ex.Message });
        }
    }

    // -- Attach a GDScript to a node -----------------------------------------
    private string AttachScript(JsonElement p)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
            return Serialize(new { error = "No scene open" });

        string nodePath   = GetStr(p, "node_path", "");
        string scriptPath = GetStr(p, "script_path", "");

        if (string.IsNullOrEmpty(scriptPath))
            return Serialize(new { error = "script_path is required" });

        Node? node = FindNode(root, nodePath);
        if (node == null)
            return Serialize(new { error = "Node not found: " + nodePath });

        if (!ResourceLoader.Exists(scriptPath))
            return Serialize(new { error = "Script file not found: " + scriptPath + ". Use write_file first." });

        Script? script = ResourceLoader.Load<Script>(scriptPath, "", ResourceLoader.CacheMode.Replace);
        if (script == null)
            return Serialize(new { error = "Failed to load script: " + scriptPath });

        // Force script reload to bypass cached memory version
        script.Reload(true);

        // Validate that the script inherits from a compatible class of the node
        string osPath = ProjectSettings.GlobalizePath(scriptPath);
        if (!File.Exists(osPath))
            return Serialize(new { error = "Script file not found on disk: " + scriptPath });

        string fileContent = File.ReadAllText(osPath);
        string extendsClass = "Node";
        var match = Regex.Match(fileContent, @"^\s*extends\s+(\w+)", RegexOptions.Multiline);
        if (match.Success)
        {
            extendsClass = match.Groups[1].Value;
        }

        if (ClassDB.ClassExists(extendsClass))
        {
            string nodeClass = node.GetClass();
            if (nodeClass != extendsClass && !ClassDB.IsParentClass(nodeClass, extendsClass))
            {
                return Serialize(new 
                { 
                    error = $"Script inherits from native type '{extendsClass}', so it can't be assigned to an object of type: '{nodeClass}'" 
                });
            }
        }

        if (EditorPlugin != null)
        {
            var undoRedo = EditorPlugin.GetUndoRedo();
            undoRedo.CreateAction("Attach Script");
            undoRedo.AddDoProperty(node, "script", script);
            undoRedo.AddUndoProperty(node, "script", node.GetScript());
            undoRedo.CommitAction();
            node.NotifyPropertyListChanged();
        }
        else
        {
            node.SetScript(script);
            node.NotifyPropertyListChanged();
        }

        return Serialize(new { attached = scriptPath, node = nodePath, undoable = EditorPlugin != null });
    }

    // -- Create a GDScript and attach it to a node ----------------------------
    private string CreateAndAttachScript(JsonElement p)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
            return Serialize(new { error = "No scene open" });

        string nodePath      = GetStr(p, "node_path", "");
        string scriptContent = GetStr(p, "script_content", "");
        string scriptPath    = GetStr(p, "script_path", "");

        if (string.IsNullOrEmpty(nodePath))
            return Serialize(new { error = "node_path is required" });
        if (string.IsNullOrEmpty(scriptContent))
            return Serialize(new { error = "script_content is required" });

        Node? node = FindNode(root, nodePath);
        if (node == null)
            return Serialize(new { error = "Node not found: " + nodePath });

        // If no script path is provided, default to res://scripts/{node_name}.gd
        if (string.IsNullOrEmpty(scriptPath))
        {
            string cleanName = node.Name.ToString().Replace(" ", "");
            scriptPath = "res://scripts/" + cleanName + ".gd";
        }

        string osPath = ProjectSettings.GlobalizePath(scriptPath);

        try
        {
            // Ensure parent directory exists
            string? dir = Path.GetDirectoryName(osPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Write script file
            File.WriteAllText(osPath, scriptContent);

            // Scan files so Godot registers the new resource
            EditorInterface.Singleton.GetResourceFilesystem().Scan();
        }
        catch (Exception ex)
        {
            return Serialize(new { error = "Failed to write script file: " + ex.Message });
        }

        // Wait up to 1 second for filesystem scan to notice the new file
        Script? script = null;
        for (int i = 0; i < 10; i++)
        {
            if (ResourceLoader.Exists(scriptPath))
            {
                script = ResourceLoader.Load<Script>(scriptPath, "", ResourceLoader.CacheMode.Replace);
                if (script != null)
                {
                    script.Reload(true);
                    break;
                }
            }
            System.Threading.Thread.Sleep(100);
        }

        // Safe fallback in case of slow filesystem import
        if (script == null)
        {
            try
            {
                var gdScript = new GDScript();
                gdScript.SourceCode = scriptContent;
                gdScript.Reload();
                script = gdScript;
            }
            catch (Exception ex)
            {
                return Serialize(new { error = "Failed to compile/load script: " + ex.Message });
            }
        }

        if (script == null)
            return Serialize(new { error = "Failed to load/compile script at: " + scriptPath });

        // Force script reload just in case
        script.Reload(true);

        // Validate that the script inherits from a compatible class of the node
        string extendsClass = "Node";
        var match = Regex.Match(scriptContent, @"^\s*extends\s+(\w+)", RegexOptions.Multiline);
        if (match.Success)
        {
            extendsClass = match.Groups[1].Value;
        }

        if (ClassDB.ClassExists(extendsClass))
        {
            string nodeClass = node.GetClass();
            if (nodeClass != extendsClass && !ClassDB.IsParentClass(nodeClass, extendsClass))
            {
                return Serialize(new 
                { 
                    error = $"Script inherits from native type '{extendsClass}', so it can't be assigned to an object of type: '{nodeClass}'" 
                });
            }
        }

        // Use EditorUndoRedoManager if available to make the action undoable and dirty the scene.
        if (EditorPlugin != null)
        {
            var undoRedo = EditorPlugin.GetUndoRedo();
            undoRedo.CreateAction("Attach Script");
            undoRedo.AddDoProperty(node, "script", script);
            undoRedo.AddUndoProperty(node, "script", node.GetScript());
            undoRedo.CommitAction();
            node.NotifyPropertyListChanged();
        }
        else
        {
            node.SetScript(script);
            node.NotifyPropertyListChanged();
        }

        return Serialize(new
        {
            written = scriptPath,
            attached = scriptPath,
            node = nodePath,
            undoable = EditorPlugin != null
        });
    }

    // -- Instantiate a subscene (.tscn) into the current scene ---------------
    private string InstantiateSubscene(JsonElement p)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
            return Serialize(new { error = "No scene open" });

        string scenePath  = GetStr(p, "scene_path", "");
        string parentPath = GetStr(p, "parent_path", "");
        string nodeName   = GetStr(p, "node_name", "");

        if (string.IsNullOrEmpty(scenePath))
            return Serialize(new { error = "scene_path is required" });

        if (!ResourceLoader.Exists(scenePath))
            return Serialize(new { error = "Scene file not found: " + scenePath });

        PackedScene? packedScene = ResourceLoader.Load<PackedScene>(scenePath, "", ResourceLoader.CacheMode.Replace);
        if (packedScene == null)
            return Serialize(new { error = "Failed to load scene: " + scenePath });

        Node parent = root;
        if (!string.IsNullOrEmpty(parentPath))
        {
            Node? found = FindNode(root, parentPath);
            if (found == null)
                return Serialize(new { error = "Parent not found: " + parentPath });
            parent = found;
        }

        Node instance = packedScene.Instantiate();
        if (instance == null)
            return Serialize(new { error = "Failed to instantiate scene" });

        if (!string.IsNullOrEmpty(nodeName))
            instance.Name = nodeName;

        if (EditorPlugin != null)
        {
            var undoRedo = EditorPlugin.GetUndoRedo();
            undoRedo.CreateAction("Instantiate Subscene");
            undoRedo.AddDoMethod(parent, "add_child", instance);
            undoRedo.AddDoReference(instance);
            undoRedo.AddUndoMethod(parent, "remove_child", instance);
            undoRedo.CommitAction();
            instance.Owner = root;
        }
        else
        {
            parent.AddChild(instance);
            instance.Owner = root;
        }

        return Serialize(new
        {
            instantiated = scenePath,
            name = instance.Name.ToString(),
            path = SceneRelativePath(root, instance)
        });
    }

    // -- Create a tiled background node ---------------------------------------
    private string CreateTiledBackground(JsonElement p)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
            return Serialize(new { error = "No scene open" });

        string texturePath = GetStr(p, "texture_path", "");
        double width       = GetDouble(p, "width", 2000.0);
        double height      = GetDouble(p, "height", 2000.0);
        string parentPath  = GetStr(p, "parent_path", "");
        string nodeName    = GetStr(p, "node_name", "TiledBackground");

        if (string.IsNullOrEmpty(texturePath))
            return Serialize(new { error = "texture_path is required" });

        if (!ResourceLoader.Exists(texturePath))
            return Serialize(new { error = "Texture file not found: " + texturePath });

        Texture2D? texture = ResourceLoader.Load<Texture2D>(texturePath, "", ResourceLoader.CacheMode.Replace);
        if (texture == null)
            return Serialize(new { error = "Failed to load texture: " + texturePath });

        Node parent = root;
        if (!string.IsNullOrEmpty(parentPath))
        {
            Node? found = FindNode(root, parentPath);
            if (found == null)
                return Serialize(new { error = "Parent not found: " + parentPath });
            parent = found;
        }

        Sprite2D sprite = new Sprite2D();
        sprite.Name = nodeName;
        sprite.Texture = texture;
        sprite.TextureRepeat = CanvasItem.TextureRepeatEnum.Enabled;
        sprite.RegionEnabled = true;
        sprite.RegionRect = new Rect2(0, 0, (float)width, (float)height);
        sprite.Position = Vector2.Zero;

        if (EditorPlugin != null)
        {
            var undoRedo = EditorPlugin.GetUndoRedo();
            undoRedo.CreateAction("Create Tiled Background");
            undoRedo.AddDoMethod(parent, "add_child", sprite);
            undoRedo.AddDoReference(sprite);
            undoRedo.AddUndoMethod(parent, "remove_child", sprite);
            undoRedo.CommitAction();
            sprite.Owner = root;
        }
        else
        {
            parent.AddChild(sprite);
            sprite.Owner = root;
        }

        return Serialize(new
        {
            created = sprite.Name.ToString(),
            path = SceneRelativePath(root, sprite),
            width = width,
            height = height
        });
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
            return Serialize(new { error = "No scene open" });

        return Serialize(new { root = NodeToMap(root) });
    }

    // Recursively converts a Node tree to plain C# objects so System.Text.Json
    // can serialize them without hitting Godot.Variant key issues.
    private System.Collections.Generic.Dictionary<string, object> NodeToMap(Node node)
    {
        Node sceneRoot = EditorInterface.Singleton.GetEditedSceneRoot();
        var children = new List<System.Collections.Generic.Dictionary<string, object>>();

        int childCount = node.GetChildCount();
        for (int i = 0; i < childCount; i++)
            children.Add(NodeToMap(node.GetChild(i)));

        return new System.Collections.Generic.Dictionary<string, object>
        {
            ["name"]     = node.Name.ToString(),
            ["type"]     = node.GetClass(),
            ["path"]     = sceneRoot != null ? SceneRelativePath(sceneRoot, node) : node.Name.ToString(),
            ["children"] = children
        };
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
            Node found = FindNode(root, parentPath);
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
            path = SceneRelativePath(root, newNode)
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
            Node found = FindNode(root, parentPath);
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
                List<JsonElement> items = new List<JsonElement>();
                foreach (JsonElement item in posEl.EnumerateArray()) items.Add(item);

                if (items.Count >= 2)
                {
                    double x = items[0].GetDouble();
                    double y = items[1].GetDouble();

                    if (newNode is Node2D n2d)
                    {
                        n2d.Position = new Vector2((float)x, (float)y);
                        positionNote = "[" + x + ", " + y + "]";
                    }
                    else if (newNode is Control ctrl)
                    {
                        ctrl.Position = new Vector2((float)x, (float)y);
                        positionNote = "[" + x + ", " + y + "]";
                    }
                }
            }
        }

        return Serialize(new
        {
            created = nodeName,
            type = nodeType,
            path = SceneRelativePath(root, newNode),
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
        Node node = FindNode(root, path);
        if (node == null)
        {
            return Serialize(new { error = "Node not found: " + path });
        }

        string name = node.Name.ToString();
        node.QueueFree();  // Schedules the node for deletion at the end of the frame
        return Serialize(new { deleted = name });
    }

    // -- Set a property on a node — type-aware --------------------------------
    //
    // Reads the node's property list to determine the ACTUAL Godot Variant.Type
    // of the property, then casts the JSON value appropriately.
    //
    // Supported types:
    //   Bool, Int, Float, String, StringName,
    //   Vector2, Vector3, Vector4, Color, Rect2,
    //   NodePath
    private string SetNodeProperty(JsonElement p)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
            return Serialize(new { error = "No scene open" });

        string path = GetStr(p, "path", "");
        string property = GetStr(p, "property", "");

        Node node = FindNode(root, path);
        if (node == null)
            return Serialize(new { error = "Node not found: " + path });

        if (!p.TryGetProperty("value", out JsonElement val))
            return Serialize(new { error = "No value provided" });

        // Look up the declared Variant.Type for this property
        Variant.Type propType = Variant.Type.Nil;
        var propList = node.GetPropertyList();
        for (int i = 0; i < propList.Count; i++)
        {
            var info = propList[i];
            if (info["name"].AsString() == property)
            {
                propType = (Variant.Type)info["type"].AsInt32();
                break;
            }
        }

        Variant godotVal;
        try
        {
            godotVal = ConvertToVariant(val, propType);
        }
        catch (Exception ex)
        {
            return Serialize(new { error = "Type conversion failed: " + ex.Message });
        }

        node.Set(property, godotVal);
        return Serialize(new { set = property, node = path, type = propType.ToString() });
    }

    // Convert a JsonElement to a Godot Variant using the declared property type.
    // Falls back to a best-effort guess when propType is Nil (unknown).
    private Variant ConvertToVariant(JsonElement val, Variant.Type propType)
    {
        // Helper: read a float array from the JSON element
        List<float> Floats(JsonElement el)
        {
            var list = new List<float>();
            foreach (var item in el.EnumerateArray())
                list.Add((float)item.GetDouble());
            return list;
        }

        switch (propType)
        {
            case Variant.Type.Bool:
                if (val.ValueKind == JsonValueKind.True)  return true;
                if (val.ValueKind == JsonValueKind.False) return false;
                if (val.ValueKind == JsonValueKind.Number) return val.GetDouble() != 0;
                return val.GetString()?.ToLower() == "true";

            case Variant.Type.Int:
                if (val.ValueKind == JsonValueKind.Number) return (long)val.GetDouble();
                if (long.TryParse(val.GetString(), out long lv)) return lv;
                return (long)0;

            case Variant.Type.Float:
                if (val.ValueKind == JsonValueKind.Number) return val.GetDouble();
                if (double.TryParse(val.GetString(), out double dv)) return dv;
                return 0.0;

            case Variant.Type.String:
                return val.GetString() ?? "";

            case Variant.Type.StringName:
                return new StringName(val.GetString() ?? "");

            case Variant.Type.NodePath:
                return new NodePath(val.GetString() ?? "");

            case Variant.Type.Vector2:
            {
                var f = Floats(val);
                return f.Count >= 2 ? new Vector2(f[0], f[1]) : Vector2.Zero;
            }

            case Variant.Type.Vector2I:
            {
                var f = Floats(val);
                return f.Count >= 2 ? new Vector2I((int)f[0], (int)f[1]) : Vector2I.Zero;
            }

            case Variant.Type.Vector3:
            {
                var f = Floats(val);
                return f.Count >= 3 ? new Vector3(f[0], f[1], f[2]) : Vector3.Zero;
            }

            case Variant.Type.Vector3I:
            {
                var f = Floats(val);
                return f.Count >= 3 ? new Vector3I((int)f[0], (int)f[1], (int)f[2]) : Vector3I.Zero;
            }

            case Variant.Type.Vector4:
            {
                var f = Floats(val);
                return f.Count >= 4 ? new Vector4(f[0], f[1], f[2], f[3]) : Vector4.Zero;
            }

            case Variant.Type.Color:
            {
                // Accept [r, g, b] or [r, g, b, a] (0–1 range) or a "#RRGGBB" string
                if (val.ValueKind == JsonValueKind.String)
                    return new Color(val.GetString() ?? "#ffffff");
                var f = Floats(val);
                return f.Count >= 3
                    ? new Color(f[0], f[1], f[2], f.Count >= 4 ? f[3] : 1f)
                    : Colors.White;
            }

            case Variant.Type.Rect2:
            {
                // Accept [x, y, width, height]
                var f = Floats(val);
                return f.Count >= 4
                    ? new Rect2(f[0], f[1], f[2], f[3])
                    : new Rect2();
            }

            case Variant.Type.Rect2I:
            {
                var f = Floats(val);
                return f.Count >= 4
                    ? new Rect2I((int)f[0], (int)f[1], (int)f[2], (int)f[3])
                    : new Rect2I();
            }

            default:
                // Unknown / Nil — best-effort fallback based on JSON kind
                if (val.ValueKind == JsonValueKind.True)  return true;
                if (val.ValueKind == JsonValueKind.False) return false;
                if (val.ValueKind == JsonValueKind.Number) return val.GetDouble();
                if (val.ValueKind == JsonValueKind.String) return val.GetString() ?? "";
                if (val.ValueKind == JsonValueKind.Array)
                {
                    var f = new List<float>();
                    foreach (var item in val.EnumerateArray())
                        f.Add((float)item.GetDouble());
                    if (f.Count == 2) return new Vector2(f[0], f[1]);
                    if (f.Count == 3) return new Color(f[0], f[1], f[2]);
                    if (f.Count == 4) return new Color(f[0], f[1], f[2], f[3]);
                }
                return "";
        }
    }

    // -- Get all properties of a node — with rich type metadata ---------------
    //
    // Returns a list of property descriptors so the AI knows:
    //   - the current value
    //   - the Godot Variant type (e.g. "Vector2", "Color", "Int")
    //   - the hint category (e.g. "Enum", "Range", "File", "None")
    //   - the hint_string (e.g. enum names "Inherit,Always,Pausable" or range "0,100,1")
    //
    // This lets the AI pick correct values instead of guessing.
    private string GetNodeProperties(JsonElement p)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
            return Serialize(new { error = "No scene open" });

        string path = GetStr(p, "path", "");
        Node node = FindNode(root, path);
        if (node == null)
            return Serialize(new { error = "Node not found: " + path });

        var propertyList = node.GetPropertyList();
        var result = new List<object>();

        for (int i = 0; i < propertyList.Count; i++)
        {
            var info = propertyList[i];

            string pname      = info["name"].AsString();
            int    typeInt    = info["type"].AsInt32();
            int    hintInt    = info["hint"].AsInt32();
            string hintString = info["hint_string"].AsString();
            int    usage      = info["usage"].AsInt32();

            // PropertyUsageFlags: 128 = EDITOR (internal), 512 = INTERNAL, 4 = CATEGORY
            // Skip properties that aren't useful for the AI to interact with
            const int USAGE_STORAGE   = 2;
            const int USAGE_EDITOR    = 4;    // visible in inspector
            const int USAGE_CATEGORY  = 128;
            const int USAGE_GROUP     = 256;
            const int USAGE_SUBGROUP  = 512;
            const int USAGE_INTERNAL  = 8192;

            bool isVisible = (usage & USAGE_EDITOR) != 0 || (usage & USAGE_STORAGE) != 0;
            bool isNoise   = (usage & USAGE_CATEGORY) != 0 || (usage & USAGE_GROUP) != 0
                          || (usage & USAGE_SUBGROUP) != 0 || (usage & USAGE_INTERNAL) != 0;

            if (!isVisible || isNoise) continue;
            if (pname.StartsWith("_")) continue;
            if (typeInt == 0) continue; // Variant.Type.Nil — unreadable

            // Map the Godot Variant.Type int to a human-readable name
            string typeName = ((Variant.Type)typeInt).ToString();

            // Map PropertyHint int to a human-readable category
            // Common values: 0=None, 1=Range, 2=ExpRange, 4=Enum, 17=File, 18=Dir
            string hintName = hintInt switch
            {
                0  => "None",
                1  => "Range",
                2  => "Range",
                4  => "Enum",
                17 => "File",
                18 => "Dir",
                19 => "GlobalFile",
                20 => "GlobalDir",
                24 => "NodeType",
                28 => "Color",
                _  => "Other"
            };

            // Read current value
            string currentValue = "";
            try
            {
                Variant v = node.Get(pname);
                currentValue = v.ToString();
            }
            catch { /* unreadable — leave empty */ }

            var entry = new
            {
                name         = pname,
                type         = typeName,
                hint         = hintName,
                hint_string  = hintString,
                value        = currentValue
            };

            result.Add(entry);
        }

        return Serialize(new { node = path, node_class = node.GetClass(), properties = result });
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

    // -- Create a brand-new Godot scene file -------------------------------
    private string CreateNewScene(JsonElement p)
    {
        string scenePath = GetStr(p, "scene_path", "");
        string rootType = GetStr(p, "root_type", "Node2D");
        string rootName = GetStr(p, "root_name", rootType);

        if (string.IsNullOrEmpty(scenePath) || !scenePath.EndsWith(".tscn"))
        {
            return Serialize(new { error = "Invalid scene_path. Must be a res:// path ending in .tscn" });
        }

        if (!ClassDB.ClassExists(rootType))
        {
            return Serialize(new { error = "Unknown node type: " + rootType });
        }

        Node root = ClassDB.Instantiate(rootType).As<Node>();
        if (root == null)
        {
            return Serialize(new { error = "Failed to instantiate: " + rootType });
        }
        root.Name = rootName;

        PackedScene packedScene = new PackedScene();
        Error err = packedScene.Pack(root);
        if (err != Error.Ok)
        {
            return Serialize(new { error = "Failed to pack scene: " + err });
        }

        err = ResourceSaver.Save(packedScene, scenePath);
        if (err != Error.Ok)
        {
            return Serialize(new { error = "Failed to save scene: " + err });
        }

        EditorInterface.Singleton.OpenSceneFromPath(scenePath);
        return Serialize(new { created = scenePath, root_type = rootType, root_name = rootName });
    }

    // -- Open an existing scene file ---------------------------------------
    private string OpenScene(JsonElement p)
    {
        string scenePath = GetStr(p, "scene_path", "");
        if (string.IsNullOrEmpty(scenePath) || !scenePath.EndsWith(".tscn"))
        {
            return Serialize(new { error = "Invalid scene_path. Must be a res:// path ending in .tscn" });
        }

        EditorInterface.Singleton.OpenSceneFromPath(scenePath);
        return Serialize(new { opened = scenePath });
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
            // Skip hidden files/directories (starting with ".") and noisy folders
            if (!item.StartsWith(".") && item != "PlaywrightProfile" && item != "addons" && item != "docs")
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

    // FindNode — resolves a path string to a Node.
    // Handles three special cases the AI commonly uses:
    //   ""  or "."           → the scene root itself
    //   root.Name (e.g. "Character") → the scene root itself
    //   anything else       → root.GetNodeOrNull(path)
    private static Node? FindNode(Node root, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "." || path == root.Name.ToString())
            return root;
        return root.GetNodeOrNull(path);
    }

    // Returns the path of a node relative to the scene root.
    // Godot's GetPath() on nodes inside the editor viewport returns the full
    // internal editor path like /root/@EditorNode@.../Character/player_head.
    // We want just "player_head" or "Character/player_head".
    private static string SceneRelativePath(Node sceneRoot, Node node)
    {
        string fullPath = node.GetPath().ToString();
        string rootPath = sceneRoot.GetPath().ToString();

        // Strip the scene root prefix (including the trailing slash)
        if (fullPath.StartsWith(rootPath))
        {
            string relative = fullPath.Substring(rootPath.Length);
            if (relative.StartsWith("/"))
                relative = relative.Substring(1);
            return string.IsNullOrEmpty(relative) ? sceneRoot.Name.ToString() : relative;
        }

        // Fallback: return just the node name
        return node.Name.ToString();
    }

    // -- Assign an image as the texture of a Sprite2D node -----------------
    //
    // image_path can be:
    //   - res://path/to/image.png  → loaded directly via ResourceLoader
    //   - /absolute/os/path.png   → copied into res://assets/ first, then loaded
    private string SetSpriteTexture(JsonElement p)
    {
        Node root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null)
            return Serialize(new { error = "No scene open" });

        string nodePath  = GetStr(p, "path", "");
        string imagePath = GetStr(p, "image_path", "");

        if (string.IsNullOrEmpty(imagePath))
            return Serialize(new { error = "image_path is required" });

        Node node = FindNode(root, nodePath);
        if (node == null)
            return Serialize(new { error = "Node not found: " + nodePath });

        if (node is not Sprite2D sprite)
            return Serialize(new { error = "Node is not a Sprite2D: " + nodePath });

        // --- Resolve the resource path ---
        string resPath = imagePath;

        if (!imagePath.StartsWith("res://"))
        {
            // Absolute OS path — copy the file into res://assets/
            string fileName  = System.IO.Path.GetFileName(imagePath);
            string assetsDir = ProjectSettings.GlobalizePath("res://assets");

            if (!System.IO.Directory.Exists(assetsDir))
                System.IO.Directory.CreateDirectory(assetsDir);

            string destOsPath = System.IO.Path.Combine(assetsDir, fileName);

            try
            {
                System.IO.File.Copy(imagePath, destOsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                return Serialize(new { error = "Failed to copy image: " + ex.Message });
            }

            resPath = "res://assets/" + fileName;

            // Tell the editor about the new file so it shows up in FileSystem dock
            EditorInterface.Singleton.GetResourceFilesystem().Scan();
        }

        // --- Load and assign the texture ---
        if (!ResourceLoader.Exists(resPath))
            return Serialize(new { error = "Resource not found after copy: " + resPath });

        Texture2D? texture = ResourceLoader.Load<Texture2D>(resPath);
        if (texture == null)
            return Serialize(new { error = "Failed to load texture from: " + resPath });

        sprite.Texture = texture;

        return Serialize(new
        {
            assigned = resPath,
            node = nodePath,
            width  = texture.GetWidth(),
            height = texture.GetHeight()
        });
    }

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

    private static double GetDouble(JsonElement el, string key, double fallback)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty(key, out JsonElement v))
            {
                if (v.ValueKind == JsonValueKind.Number)
                {
                    return v.GetDouble();
                }
                if (v.ValueKind == JsonValueKind.String)
                {
                    if (double.TryParse(v.GetString(), out double val))
                    {
                        return val;
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