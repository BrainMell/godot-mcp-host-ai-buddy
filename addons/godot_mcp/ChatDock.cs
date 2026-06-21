#if TOOLS
using Godot;
using System.Threading.Tasks;
using System;

namespace GodotMCP;

[Tool]
public partial class ChatDock : Control
{
    public McpHttpServer? Server { get; set; }

    // UI handles
    private RichTextLabel _output = null!;
    private LineEdit _input = null!;
    private Button _sendBtn = null!;
    private Label _status = null!;
    private Label _statusDot = null!;
    private Button _clearBtn = null!;

    private GroqAgent? _agent;
    private bool _waiting;

    // ── Color palette (muted, terminal-flavored) ───────────────────────────
    private static readonly Color BgColor        = new(0.08f, 0.09f, 0.11f);   // near-black
    private static readonly Color PanelBgColor   = new(0.11f, 0.12f, 0.14f);   // slightly lighter
    private static readonly Color BorderColor    = new(0.20f, 0.22f, 0.26f);   // subtle separator
    private static readonly Color FgColor        = new(0.85f, 0.87f, 0.90f);   // main text
    private static readonly Color FgDimColor     = new(0.50f, 0.54f, 0.58f);   // dim labels
    private static readonly Color UserRole       = new(0.55f, 0.78f, 1.00f);   // soft blue
    private static readonly Color AiRole         = new(0.70f, 0.90f, 0.70f);   // soft green
    private static readonly Color SysRole        = new(0.60f, 0.62f, 0.66f);   // gray
    private static readonly Color ErrRole        = new(0.95f, 0.55f, 0.55f);   // muted red
    private static readonly Color ToolRole       = new(0.85f, 0.78f, 0.50f);   // muted yellow
    private static readonly Color AccentColor    = new(1.00f, 0.65f, 0.20f);   // orange (title)
    private static readonly Color StatusOkColor  = new(0.40f, 0.75f, 0.45f);
    private static readonly Color StatusBusyColor= new(0.90f, 0.78f, 0.30f);
    private static readonly Color StatusErrColor = new(0.85f, 0.40f, 0.40f);

    public override void _Ready()
    {
        BuildUi();
        TryInitAgent();
        AppendMessage("system", "godot-mcp ready. type a command or ask a question.");
        AppendMessage("system", "try: \"create a Node2D called Player at [200, 150]\"");
    }

    private void TryInitAgent()
    {
        try
        {
            var tools = new GodotTools();
            _agent = new GroqAgent(tools);
            SetStatus("ready", StatusOkColor);
        }
        catch (Exception ex)
        {
            AppendMessage("error", ex.Message);
            SetStatus("no api key", StatusErrColor);
        }
    }

    // ── UI construction ─────────────────────────────────────────────────────
    private void BuildUi()
    {
        // Root: fill the dock
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AnchorRight = 1.0f;
        AnchorBottom = 1.0f;
        CustomMinimumSize = new Vector2(0, 240);

        // Wrap everything in a PanelContainer so the dark background fills
        // every pixel of the dock (no gaps between header / output / input).
        var dockPanel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
        };
        dockPanel.AddThemeStyleboxOverride("panel", MakeStyle(BgColor, 0, BorderColor));
        AddChild(dockPanel);

        // ── Root container ──
        var root = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", 0);
        dockPanel.AddChild(root);

        // ── Header bar (thin, single line) ──
        var header = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 28),
        };
        header.AddThemeConstantOverride("separation", 8);
        header.AddThemeStyleboxOverride("panel", MakeStyle(PanelBgColor, 0, BorderColor));
        // Apply some left/right padding via margin container
        var headerPad = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        headerPad.AddThemeConstantOverride("margin_left", 10);
        headerPad.AddThemeConstantOverride("margin_right", 10);
        header.AddChild(headerPad);

        var headerInner = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        headerInner.AddThemeConstantOverride("separation", 8);
        headerInner.Alignment = BoxContainer.AlignmentMode.Begin;
        headerPad.AddChild(headerInner);

        var title = new Label
        {
            Text = "godot-mcp",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyMonospaceFont(title);
        title.AddThemeColorOverride("font_color", AccentColor);
        title.AddThemeFontSizeOverride("font_size", 13);
        title.VerticalAlignment = VerticalAlignment.Center;
        headerInner.AddChild(title);

        var version = new Label
        {
            Text = "v0.1.0",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyMonospaceFont(version);
        version.AddThemeColorOverride("font_color", FgDimColor);
        version.AddThemeFontSizeOverride("font_size", 11);
        version.VerticalAlignment = VerticalAlignment.Center;
        headerInner.AddChild(version);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        headerInner.AddChild(spacer);

        // status dot + text
        _statusDot = new Label
        {
            Text = "●",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyMonospaceFont(_statusDot);
        _statusDot.AddThemeColorOverride("font_color", StatusBusyColor);
        _statusDot.AddThemeFontSizeOverride("font_size", 12);
        _statusDot.VerticalAlignment = VerticalAlignment.Center;
        headerInner.AddChild(_statusDot);

        _status = new Label
        {
            Text = "starting",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyMonospaceFont(_status);
        _status.AddThemeColorOverride("font_color", FgDimColor);
        _status.AddThemeFontSizeOverride("font_size", 11);
        _status.VerticalAlignment = VerticalAlignment.Center;
        headerInner.AddChild(_status);

        var headerSpacer2 = new Control { CustomMinimumSize = new Vector2(16, 0) };
        headerInner.AddChild(headerSpacer2);

        _clearBtn = new Button
        {
            Text = "clear",
            Flat = true,
            CustomMinimumSize = new Vector2(0, 22),
            MouseFilter = MouseFilterEnum.Stop,
        };
        ApplyMonospaceFont(_clearBtn);
        _clearBtn.AddThemeFontSizeOverride("font_size", 11);
        _clearBtn.AddThemeColorOverride("font_color", FgDimColor);
        _clearBtn.AddThemeColorOverride("font_hover_color", FgColor);
        _clearBtn.Pressed += ClearChat;
        headerInner.AddChild(_clearBtn);

        root.AddChild(header);

        // Thin separator line
        root.AddChild(MakeSeparator());

        // ── Output area ──
        _output = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _output.AddThemeColorOverride("default_color", FgColor);
        _output.AddThemeColorOverride("font_selected_color", new Color(1f, 1f, 1f));
        _output.AddThemeColorOverride("selection_color", new Color(0.30f, 0.45f, 0.70f));
        _output.AddThemeStyleboxOverride("normal", MakeStyle(BgColor, 8, BgColor));
        _output.AddThemeStyleboxOverride("focus", MakeStyle(BgColor, 8, BgColor));
        // Use the engine's monospace font
        _output.AddThemeFontOverride("normal_font", GetMonospaceFont());
        _output.AddThemeFontOverride("bold_font", GetMonospaceFont());
        _output.AddThemeFontOverride("mono_font", GetMonospaceFont());
        _output.AddThemeFontSizeOverride("normal_font_size", 12);
        _output.AddThemeConstantOverride("line_separation", 2);

        // Wrap the output in a margin container for padding
        var outputMargin = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        outputMargin.AddThemeConstantOverride("margin_left", 12);
        outputMargin.AddThemeConstantOverride("margin_right", 12);
        outputMargin.AddThemeConstantOverride("margin_top", 8);
        outputMargin.AddThemeConstantOverride("margin_bottom", 8);
        outputMargin.AddChild(_output);
        root.AddChild(outputMargin);

        // Thin separator above input
        root.AddChild(MakeSeparator());

        // ── Input row ──
        var inputPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 36),
        };
        inputPanel.AddThemeStyleboxOverride("panel", MakeStyle(PanelBgColor, 0, BorderColor));
        root.AddChild(inputPanel);

        var inputMargin = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        inputMargin.AddThemeConstantOverride("margin_left", 12);
        inputMargin.AddThemeConstantOverride("margin_right", 12);
        inputMargin.AddThemeConstantOverride("margin_top", 4);
        inputMargin.AddThemeConstantOverride("margin_bottom", 4);
        inputPanel.AddChild(inputMargin);

        var inputRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        inputRow.AddThemeConstantOverride("separation", 8);
        inputRow.Alignment = BoxContainer.AlignmentMode.Begin;
        inputMargin.AddChild(inputRow);

        // > prompt prefix
        var promptGlyph = new Label
        {
            Text = ">",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyMonospaceFont(promptGlyph);
        promptGlyph.AddThemeColorOverride("font_color", AccentColor);
        promptGlyph.AddThemeFontSizeOverride("font_size", 13);
        promptGlyph.VerticalAlignment = VerticalAlignment.Center;
        inputRow.AddChild(promptGlyph);

        _input = new LineEdit
        {
            PlaceholderText = "ask the agent to control godot...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        ApplyMonospaceFont(_input);
        _input.AddThemeFontSizeOverride("font_size", 12);
        _input.AddThemeColorOverride("font_color", FgColor);
        _input.AddThemeColorOverride("font_placeholder_color", FgDimColor);
        _input.AddThemeColorOverride("caret_color", AccentColor);
        _input.AddThemeStyleboxOverride("normal", MakeStyle(new Color(0, 0, 0, 0), 0, new Color(0, 0, 0, 0)));
        _input.AddThemeStyleboxOverride("focus", MakeStyle(new Color(0, 0, 0, 0), 0, new Color(0, 0, 0, 0)));
        _input.AddThemeStyleboxOverride("read_only", MakeStyle(new Color(0, 0, 0, 0), 0, new Color(0, 0, 0, 0)));
        _input.TextSubmitted += OnSend;
        inputRow.AddChild(_input);

        _sendBtn = new Button
        {
            Text = "send",
            CustomMinimumSize = new Vector2(60, 24),
        };
        ApplyMonospaceFont(_sendBtn);
        _sendBtn.AddThemeFontSizeOverride("font_size", 11);
        _sendBtn.AddThemeColorOverride("font_color", FgColor);
        _sendBtn.AddThemeColorOverride("font_hover_color", AccentColor);
        _sendBtn.AddThemeColorOverride("font_disabled_color", FgDimColor);
        _sendBtn.AddThemeStyleboxOverride("normal", MakeStyle(PanelBgColor, 0, BorderColor));
        _sendBtn.AddThemeStyleboxOverride("hover", MakeStyle(new Color(0.16f, 0.17f, 0.20f), 0, BorderColor));
        _sendBtn.AddThemeStyleboxOverride("disabled", MakeStyle(PanelBgColor, 0, BorderColor));
        _sendBtn.AddThemeStyleboxOverride("focus", MakeStyle(PanelBgColor, 0, BorderColor));
        _sendBtn.AddThemeStyleboxOverride("pressed", MakeStyle(PanelBgColor, 0, AccentColor));
        _sendBtn.Pressed += () => OnSend(_input.Text);
        inputRow.AddChild(_sendBtn);
    }

    // ── Sending ─────────────────────────────────────────────────────────────
    private async void OnSend(string text)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text) || _waiting) return;
        if (_agent == null)
        {
            AppendMessage("error", "agent not initialized — check GROQ_API_KEY in .env");
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
                AppendMessage("tool", "called: " + string.Join(", ", result.ToolsUsed));

            SetStatus("ready", StatusOkColor);
        }
        catch (Exception ex)
        {
            AppendMessage("error", ex.Message);
            SetStatus("error", StatusErrColor);
        }
        finally
        {
            SetWaiting(false);
        }
    }

    // ── Message rendering ───────────────────────────────────────────────────
    private void AppendMessage(string role, string text)
    {
        // Pick prefix + color per role. Prefix is short, lowercase, fixed-width.
        var (prefix, color, isDim) = role switch
        {
            "user"   => ("you",    UserRole,  false),
            "ai"     => ("ai",     AiRole,    false),
            "system" => ("sys",    SysRole,   true),
            "error"  => ("err",    ErrRole,   false),
            "tool"   => ("tool",   ToolRole,  true),
            _        => (role,     FgColor,   false),
        };

        // Escape BBCode. Newlines get a 2-space indent so wrapped lines
        // line up under the message text (not under the prefix).
        string safe = text.Replace("[", "[[").Replace("\n", "\n  ");

        string colorHex = ColorToHex(color);
        Color bodyColor = isDim ? FgDimColor : FgColor;

        // Render as:  <prefix>  <message text>
        //             (wrapped lines indented by two spaces)
        _output.AppendText(
            $"[color={colorHex}][b]{prefix}[/b][/color]  " +
            $"[color={ColorToHex(bodyColor)}]{safe}[/color]\n");
    }

    private static string ColorToHex(Color c) =>
        $"#{(int)(c.R * 255):X2}{(int)(c.G * 255):X2}{(int)(c.B * 255):X2}";

    // ── State setters ───────────────────────────────────────────────────────
    private void SetWaiting(bool val)
    {
        _waiting = val;
        _sendBtn.Disabled = val;
        _input.Editable = !val;
        _sendBtn.Text = val ? "..." : "send";
        if (val) SetStatus("thinking", StatusBusyColor);
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", color);
        _statusDot.AddThemeColorOverride("font_color", color);
    }

    private void ClearChat()
    {
        _output.Clear();
        AppendMessage("system", "chat cleared.");
    }

    // ── Font + stylebox helpers ─────────────────────────────────────────────
    private static Font? _monospaceFont;
    private Font GetMonospaceFont()
    {
        if (_monospaceFont != null) return _monospaceFont;
        // Try to load a monospace font from the engine's default theme
        var font = GetThemeDefaultFont();
        _monospaceFont = font;
        return font;
    }

    private void ApplyMonospaceFont(Control node)
    {
        // For Label / Button / LineEdit, overriding the default font works
        if (node is Label lbl)
            lbl.AddThemeFontOverride("font", GetMonospaceFont());
        else if (node is Button btn)
            btn.AddThemeFontOverride("font", GetMonospaceFont());
        else if (node is LineEdit le)
            le.AddThemeFontOverride("font", GetMonospaceFont());
    }

    private static StyleBoxFlat MakeStyle(Color bg, int borderWidth, Color borderColor)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            BorderWidthLeft = 0,
            BorderWidthRight = 0,
            BorderWidthTop = 0,
            BorderWidthBottom = 0,
            ContentMarginLeft = 0,
            ContentMarginRight = 0,
            ContentMarginTop = 0,
            ContentMarginBottom = 0,
        };
        if (borderWidth > 0)
        {
            sb.BorderWidthBottom = borderWidth;
            sb.BorderColor = borderColor;
        }
        return sb;
    }

    private static HSeparator MakeSeparator()
    {
        var sep = new HSeparator
        {
            CustomMinimumSize = new Vector2(0, 1),
        };
        sep.AddThemeColorOverride("separator", BorderColor);
        return sep;
    }
}
#endif
