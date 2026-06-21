#if TOOLS
using Godot;
using Godot.Collections;
using System.Text;
using System.Text.Json;

namespace GodotMCP;

[Tool]
public partial class McpHttpServer : Node
{
    public EditorPlugin? EditorPlugin { get; set; }

    private const int Port = 9876;
    private TcpServer _tcpServer = new();
    private StreamPeerTcp? _client;

    public void Start()
    {
        var err = _tcpServer.Listen(Port);
        if (err != Error.Ok)
            GD.PrintErr($"[GodotMCP] Failed to listen on port {Port}: {err}");
        else
            GD.Print($"[GodotMCP] HTTP server listening on port {Port}");
    }

    public void Stop() => _tcpServer.Stop();

    public override void _Process(double delta)
    {
        if (_tcpServer.IsConnectionAvailable())
            _client = _tcpServer.TakeConnection();

        if (_client == null) return;
        if (_client.GetStatus() != StreamPeerTcp.Status.Connected)
        {
            _client = null;
            return;
        }

        int available = (int)_client.GetAvailableBytes();
        if (available <= 0) return;

        string raw = _client.GetString(available);
        string response = HandleRequest(raw);
        _client.PutData(Encoding.UTF8.GetBytes(response));
        _client.DisconnectFromHost();
        _client = null;
    }

    private string HandleRequest(string raw)
    {
        // Extract HTTP body (after \r\n\r\n)
        int bodyStart = raw.IndexOf("\r\n\r\n");
        string body = bodyStart >= 0 ? raw[(bodyStart + 4)..] : raw;

        try
        {
            var req = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, JsonElement>>(body);
            if (req == null)
                return HttpResponse(JsonSerializer.Serialize(new { error = "Invalid request payload" }), 400);
            string action = req.TryGetValue("action", out var a) ? a.GetString() ?? "" : "";
            var paramsEl = req.TryGetValue("params", out var p) ? p : default;

            var result = Dispatch(action, paramsEl);
            return HttpResponse(result);
        }
        catch (System.Exception ex)
        {
            return HttpResponse(JsonSerializer.Serialize(new { error = ex.Message }), 400);
        }
    }

    private string Dispatch(string action, JsonElement parameters)
    {
        return action switch
        {
            "ping" => Serialize(new { status = "ok", version = "0.1.0" }),
            "get_scene_tree" => GetSceneTree(),
            "get_selected_nodes" => GetSelectedNodes(),
            "create_node" => CreateNode(parameters),
            "delete_node" => DeleteNode(parameters),
            "set_node_property" => SetNodeProperty(parameters),
            "get_node_properties" => GetNodeProperties(parameters),
            "save_scene" => SaveScene(),
            "get_editor_state" => GetEditorState(),
            "list_project_files" => ListProjectFiles(parameters),
            _ => Serialize(new { error = $"Unknown action: {action}" })
        };
    }

    // ── Tool implementations ──────────────────────────────────────────────

    private string GetSceneTree()
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null) return Serialize(new { error = "No scene open" });
        return Serialize(new { root = NodeToDict(root) });
    }

    private Dictionary NodeToDict(Node node)
    {
        var children = new Godot.Collections.Array<Dictionary>();
        foreach (Node child in node.GetChildren())
            children.Add(NodeToDict(child));

        return new Dictionary
        {
            ["name"] = node.Name.ToString(),
            ["type"] = node.GetClass(),
            ["path"] = node.GetPath().ToString(),
            ["children"] = children
        };
    }

    private string GetSelectedNodes()
    {
        var selected = EditorInterface.Singleton.GetSelection().GetSelectedNodes();
        var nodes = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(selected, n => new
        {
            name = n.Name.ToString(),
            type = n.GetClass(),
            path = n.GetPath().ToString()
        }));
        return Serialize(new { selected = nodes });
    }

    private string CreateNode(JsonElement p)
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null) return Serialize(new { error = "No scene open. Create or open a scene first." });

        string nodeType = GetStr(p, "node_type", "Node");
        string nodeName = GetStr(p, "node_name", nodeType);
        string parentPath = GetStr(p, "parent_path", "");

        Node parent = root;
        if (!string.IsNullOrEmpty(parentPath))
        {
            var found = root.GetNodeOrNull(parentPath);
            if (found == null) return Serialize(new { error = $"Parent not found: {parentPath}" });
            parent = found;
        }

        // Instantiate the node type
        var newNode = ClassDB.Instantiate(nodeType).As<Node>();
        if (newNode == null) return Serialize(new { error = $"Unknown node type: {nodeType}" });

        newNode.Name = nodeName;
        parent.AddChild(newNode);
        newNode.Owner = root;

        return Serialize(new
        {
            created = nodeName,
            type = nodeType,
            path = newNode.GetPath().ToString()
        });
    }

    private string DeleteNode(JsonElement p)
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null) return Serialize(new { error = "No scene open" });

        string path = GetStr(p, "path", "");
        var node = root.GetNodeOrNull(path);
        if (node == null) return Serialize(new { error = $"Node not found: {path}" });

        string name = node.Name.ToString();
        node.QueueFree();
        return Serialize(new { deleted = name });
    }

    private string SetNodeProperty(JsonElement p)
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null) return Serialize(new { error = "No scene open" });

        string path = GetStr(p, "path", "");
        string property = GetStr(p, "property", "");
        var node = root.GetNodeOrNull(path);
        if (node == null) return Serialize(new { error = $"Node not found: {path}" });

        if (p.TryGetProperty("value", out var val))
        {
            // Convert JsonElement to Godot Variant
            Variant godotVal = val.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => val.GetDouble(),
                JsonValueKind.String => val.GetString() ?? "",
                JsonValueKind.Array => ParseVector2(val),
                _ => Variant.From("")
            };
            node.Set(property, godotVal);
        }

        return Serialize(new { set = property, node = path });
    }

    private Variant ParseVector2(JsonElement arr)
    {
        // Allow [x, y] arrays to be set as Vector2
        var items = System.Linq.Enumerable.ToList(arr.EnumerateArray());
        if (items.Count >= 2)
            return new Vector2((float)items[0].GetDouble(), (float)items[1].GetDouble());
        return Variant.From("");
    }

    private string GetNodeProperties(JsonElement p)
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root == null) return Serialize(new { error = "No scene open" });

        string path = GetStr(p, "path", "");
        var node = root.GetNodeOrNull(path);
        if (node == null) return Serialize(new { error = $"Node not found: {path}" });

        var props = new Dictionary();
        foreach (var prop in node.GetPropertyList())
        {
            if (prop["name"].AsString() is string pname && !pname.StartsWith("_"))
            {
                try { props[pname] = node.Get(pname).ToString(); }
                catch { /* skip unreadable props */ }
            }
        }

        return Serialize(new { node = path, properties = props });
    }

    private string SaveScene()
    {
        EditorInterface.Singleton.SaveScene();
        return Serialize(new { saved = true });
    }

    private string GetEditorState()
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        return Serialize(new
        {
            has_open_scene = root != null,
            scene_name = root?.Name.ToString() ?? "",
            scene_path = root?.SceneFilePath ?? ""
        });
    }

    private string ListProjectFiles(JsonElement p)
    {
        string dirPath = GetStr(p, "path", "res://");
        var files = new System.Collections.Generic.List<string>();
        WalkDir(dirPath, files);
        return Serialize(new { path = dirPath, files });
    }

    private void WalkDir(string path, System.Collections.Generic.List<string> output)
    {
        using var dir = DirAccess.Open(path);
        if (dir == null) return;
        dir.ListDirBegin();
        string item = dir.GetNext();
        while (item != "")
        {
            if (!item.StartsWith("."))
            {
                string full = path.PathJoin(item);
                if (dir.CurrentIsDir()) WalkDir(full, output);
                else output.Add(full);
            }
            item = dir.GetNext();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string GetStr(JsonElement el, string key, string fallback)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(key, out var v) &&
            v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? fallback;
        return fallback;
    }

    private static string Serialize(object obj) =>
        JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

    private static string HttpResponse(string body, int code = 200) =>
        $"HTTP/1.1 {code} OK\r\n" +
        $"Content-Type: application/json\r\n" +
        $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
        $"Access-Control-Allow-Origin: *\r\n" +
        $"\r\n{body}";
}
#endif
