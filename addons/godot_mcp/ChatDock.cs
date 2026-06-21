#if TOOLS
using Godot;
using System.Threading.Tasks;
using System;

namespace GodotMCP;

[Tool]
public partial class ChatDock : Control
{
    public McpHttpServer? Server { get; set; }

    private RichTextLabel _output = null!;
    private LineEdit _input = null!;
    private Button _sendBtn = null!;
    private Label _status = null!;

    private GroqAgent? _agent;
    private bool _waiting;

    public override void _Ready()
    {
        BuildUi();
        TryInitAgent();
        AppendMessage("system", "GodotMCP ready. Type a message to control the editor.");
        AppendMessage("system", "Example: 'create a Sprite2D node called Player'");
    }

    private void TryInitAgent()
    {
        try
        {
            var tools = new GodotTools();
            _agent = new GroqAgent(tools);
            SetStatus("● ready", new Color(0.3f, 0.8f, 0.3f));
        }
        catch (Exception ex)
        {
            AppendMessage("error", ex.Message);
            SetStatus("● no api key", new Color(0.8f, 0.3f, 0.3f));
        }
    }

    private void BuildUi()
    {
        // Root layout
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AnchorRight = 1.0f;
        AnchorBottom = 1.0f;
        OffsetLeft = 0;
        OffsetTop = 0;
        OffsetRight = 0;
        OffsetBottom = 0;
        CustomMinimumSize = new Vector2(0, 220);

        var root = new VBoxContainer();
        root.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AnchorRight = 1.0f;
        root.AnchorBottom = 1.0f;
        root.OffsetLeft = 0;
        root.OffsetTop = 0;
        root.OffsetRight = 0;
        root.OffsetBottom = 0;
        root.AddThemeConstantOverride("separation", 4);
        AddChild(root);

        // ── Top bar ──
        var topBar = new HBoxContainer();
        root.AddChild(topBar);

        var title = new Label();
        title.Text = "🤖 GodotMCP";
        title.AddThemeColorOverride("font_color", new Color(1f, 0.6f, 0.1f));
        topBar.AddChild(title);

        var spacer = new Control();
        spacer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        topBar.AddChild(spacer);

        _status = new Label();
        _status.Text = "● starting";
        topBar.AddChild(_status);

        var clearBtn = new Button();
        clearBtn.Text = "Clear";
        clearBtn.Flat = true;
        clearBtn.Pressed += ClearChat;
        topBar.AddChild(clearBtn);

        // ── Separator ──
        root.AddChild(new HSeparator());

        // ── Output ──
        _output = new RichTextLabel();
        _output.BbcodeEnabled = true;
        _output.ScrollFollowing = true;
        _output.SizeFlagsVertical = SizeFlags.ExpandFill;
        _output.AddThemeColorOverride("default_color", new Color(0.85f, 0.85f, 0.85f));
        root.AddChild(_output);

        // ── Input row ──
        var inputRow = new HBoxContainer();
        inputRow.AddThemeConstantOverride("separation", 6);
        root.AddChild(inputRow);

        _input = new LineEdit();
        _input.PlaceholderText = "Ask AI to control Godot... (e.g. 'create a Label node')";
        _input.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _input.TextSubmitted += OnSend;
        inputRow.AddChild(_input);

        _sendBtn = new Button();
        _sendBtn.Text = "Send";
        _sendBtn.Pressed += () => OnSend(_input.Text);
        inputRow.AddChild(_sendBtn);
    }

    private async void OnSend(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text) || _waiting) return;
        if (_agent == null)
        {
            AppendMessage("error", "Agent not initialized. Check your GROQ_API_KEY in .env");
            return;
        }

        _input.Clear();
        AppendMessage("user", text);
        SetWaiting(true);

        try
        {
            var result = await _agent.ChatAsync(text);
            AppendMessage("ai", result.Reply);

            if (result.ToolsUsed.Count > 0)
                AppendMessage("system", $"Tools used: {string.Join(", ", result.ToolsUsed)}");

            SetStatus("● ready", new Color(0.3f, 0.8f, 0.3f));
        }
        catch (Exception ex)
        {
            AppendMessage("error", $"Error: {ex.Message}");
            SetStatus("● error", new Color(0.8f, 0.3f, 0.3f));
        }
        finally
        {
            SetWaiting(false);
        }
    }

    private void AppendMessage(string role, string text)
    {
        var roleData = role switch
        {
            "user"   => (color: "#88aaff", prefix: "You"),
            "ai"     => (color: "#aaffaa", prefix: "AI"),
            "system" => (color: "#888888", prefix: "System"),
            "error"  => (color: "#ff6666", prefix: "Error"),
            _        => (color: "#ffffff", prefix: role)
        };

        // Escape brackets to prevent BBCode injection
        string safe = text.Replace("[", "[[");
        _output.AppendText($"[color={roleData.color}][b]{roleData.prefix}:[/b][/color] {safe}\n");
    }

    private void SetWaiting(bool val)
    {
        _waiting = val;
        _sendBtn.Disabled = val;
        _input.Editable = !val;
        _sendBtn.Text = val ? "..." : "Send";
        if (val) SetStatus("● thinking...", new Color(1f, 0.8f, 0.2f));
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", color);
    }

    private void ClearChat()
    {
        _output.Clear();
        AppendMessage("system", "Chat cleared.");
    }
}
#endif
