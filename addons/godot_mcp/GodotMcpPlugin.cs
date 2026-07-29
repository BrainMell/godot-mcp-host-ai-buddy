#if TOOLS
using Godot;

namespace GodotMCP;

// ---------------------------------------------------------------------------
// GodotMcpPlugin — the entry point for the plugin
//
// Godot calls _EnterTree() when the plugin is enabled, and _ExitTree() when
// it's disabled. This is where we:
//   1. Start the local HTTP server (McpHttpServer)
//   2. Create and register the chat dock (ChatDock)
//
// That's it. This file doesn't do any logic itself — it just wires things up.
//
// The [Tool] attribute tells Godot this script runs in the editor (not just
// in-game). EditorPlugin is the base class for all editor plugins.
// ---------------------------------------------------------------------------

[Tool]
public partial class GodotMcpPlugin : EditorPlugin
{
    // The HTTP server that handles tool execution requests
    // ? means these can be null (they're null before _EnterTree runs
    // and after _ExitTree cleans up)
    private McpHttpServer? _server;

    // The chat dock UI
    private ChatDock? _dock;

    // The EditorDock wrapper used with the new AddDock API
    private EditorDock? _editorDock;

    // -----------------------------------------------------------------------
    // _EnterTree — called when the user enables the plugin
    // -----------------------------------------------------------------------

    public override void _EnterTree()
    {
        // 1. Create and start the HTTP server
        //    This runs on the main thread and will receive tool execution
        //    requests from the browser agent (which runs asynchronously)
        _server = new McpHttpServer();
        _server.EditorPlugin = this;
        AddChild(_server);     // Adding as a child means _Process() gets called
        _server.Start();       // Start listening on port 9876 (or next available)

        // 2. Create the chat dock and add it to the bottom panel
        //    AddControlToBottomPanel makes it appear alongside Output, Debug, etc.
        _dock = new ChatDock();
        _dock.Server = _server;
        _editorDock = new EditorDock();
        _editorDock.DefaultSlot = EditorDock.DockSlot.Bottom;
        _editorDock.Name = "AI Chat";
        _editorDock.AddChild(_dock);
        AddDock(_editorDock);

        GD.Print("[GodotMCP] Plugin loaded. Chat dock ready.");
    }

    // -----------------------------------------------------------------------
    // _Build — called by the engine just before C# rebuilds / unloads
    // -----------------------------------------------------------------------

    public override bool _Build()
    {
        // 1. Force the agent to dispose before the build starts
        if (_dock != null)
        {
            _dock.ForceDisposeAgent();
        }

        // 2. Stop the HTTP server to free up the socket
        if (_server != null)
        {
            _server.Stop();
        }

        GD.Print("[GodotMCP] Prepared for build/hot-reload by stopping background processes.");
        return true;
    }

    // -----------------------------------------------------------------------
    // _ExitTree — called when the user disables the plugin
    // -----------------------------------------------------------------------

    public override void _ExitTree()
    {
        // Remove and destroy the chat dock
        if (_editorDock != null)
        {
            RemoveDock(_editorDock);
            _editorDock.QueueFree();
            _editorDock = null;
            _dock = null;
        }

        // Stop and destroy the HTTP server
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