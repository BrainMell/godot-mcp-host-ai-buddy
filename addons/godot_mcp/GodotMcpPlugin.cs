#if TOOLS
using Godot;

namespace GodotMCP;

[Tool]
public partial class GodotMcpPlugin : EditorPlugin
{
    private McpHttpServer? _server;
    private ChatDock? _dock;

    public override void _EnterTree()
    {
        // Start the HTTP server that receives tool execution requests
        _server = new McpHttpServer();
        _server.EditorPlugin = this;
        AddChild(_server);
        _server.Start();

        // Create and register the chat dock in the bottom panel
        _dock = new ChatDock();
        _dock.Server = _server;
        AddControlToBottomPanel(_dock, "🤖 AI Chat");

        GD.Print("[GodotMCP] Plugin loaded. Chat dock ready.");
    }

    public override void _ExitTree()
    {
        if (_dock != null)
        {
            RemoveControlFromBottomPanel(_dock);
            _dock.QueueFree();
            _dock = null;
        }

        if (_server != null)
        {
            _server.Stop();
            _server.QueueFree();
            _server = null;
        }

        GD.Print("[GodotMCP] Plugin unloaded.");
    }
}
#endif
