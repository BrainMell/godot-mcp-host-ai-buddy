#if TOOLS
using Godot;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace GodotMCP;

// ---------------------------------------------------------------------------
// ChatDock — the UI panel that appears at the bottom of the Godot editor
//
// This is the only file that deals with UI. Everything else is logic.
//
// It does three things:
//   1. Builds the chat interface (input box, output area, send button)
//   2. When the user sends a message, passes it to GroqAgent.ChatAsync()
//   3. Displays the agent's reply (and any tool calls it made)
// ---------------------------------------------------------------------------

[Tool]
public partial class ChatDock : Control
{
    // A reference to the HTTP server (not used directly by ChatDock,
    // but stored here so the plugin can wire things up)
    // The ? means this CAN be null (the plugin sets it after creating ChatDock)
    public McpHttpServer? Server { get; set; }

    // -- UI elements (created in BuildUi) ----------------------------------
    // The "!" (null-forgiving operator) tells the compiler:
    //   "I know this is null right now, but I WILL set it in BuildUi()
    //    before anything else uses it. Trust me."
    // Without this, the compiler would warn: "field not initialized"
    private RichTextLabel _output = null!;
    private LineEdit _input = null!;
    private Button _sendBtn = null!;
    private Label _status = null!;
    private Label _statusDot = null!;
    private Button _clearBtn = null!;

    // -- State -------------------------------------------------------------
    // ? means this can be null (it's null if the API key wasn't found)
    private GroqAgent? _agent;
    private bool _waiting;             // True while we're waiting for a response

    // -- Color palette (muted, terminal-flavored) --------------------------
    // These are RGB values in the 0.0 to 1.0 range
    private static readonly Color BgColor         = new Color(0.08f, 0.09f, 0.11f);   // near-black background
    private static readonly Color PanelBgColor    = new Color(0.11f, 0.12f, 0.14f);   // slightly lighter panels
    private static readonly Color BorderColor     = new Color(0.20f, 0.22f, 0.26f);   // subtle separators
    private static readonly Color FgColor         = new Color(0.85f, 0.87f, 0.90f);   // main text color
    private static readonly Color FgDimColor      = new Color(0.50f, 0.54f, 0.58f);   // dimmed text
    private static readonly Color UserRoleColor   = new Color(0.55f, 0.78f, 1.00f);   // user messages (blue)
    private static readonly Color AiRoleColor     = new Color(0.70f, 0.90f, 0.70f);   // AI replies (green)
    private static readonly Color SysRoleColor    = new Color(0.60f, 0.62f, 0.66f);   // system messages (gray)
    private static readonly Color ErrRoleColor    = new Color(0.95f, 0.55f, 0.55f);   // errors (red)
    private static readonly Color ToolRoleColor   = new Color(0.85f, 0.78f, 0.50f);   // tool calls (yellow)
    private static readonly Color AccentColor     = new Color(1.00f, 0.65f, 0.20f);   // title, prompt char (orange)
    private static readonly Color StatusOkColor   = new Color(0.40f, 0.75f, 0.45f);   // "ready" status (green)
    private static readonly Color StatusBusyColor = new Color(0.90f, 0.78f, 0.30f);   // "thinking" status (yellow)
    private static readonly Color StatusErrColor  = new Color(0.85f, 0.40f, 0.40f);   // "error" status (red)

    // -- Cached monospace font ----------------------------------------------
    // ? because it's null until the first call to GetMonospaceFont()
    private static Font? _monospaceFont;

    // -----------------------------------------------------------------------
    // _Ready — called by Godot when this node enters the scene tree
    // -----------------------------------------------------------------------

    public override void _Ready()
    {
        BuildUi();
        TryInitAgent();

        AppendMessage("system", "godot-mcp ready. type a command or ask a question.");
        AppendMessage("system", "try: \"create a Node2D called Player at [200, 150]\"");
    }

    // -----------------------------------------------------------------------
    // TryInitAgent — create the GroqAgent (or show error if no API key)
    // -----------------------------------------------------------------------

    private void TryInitAgent()
    {
        try
        {
            GodotTools tools = new GodotTools();
            _agent = new GroqAgent(tools);
            SetStatus("ready", StatusOkColor);
        }
        catch (Exception ex)
        {
            AppendMessage("error", ex.Message);
            SetStatus("no api key", StatusErrColor);
        }
    }

    // =======================================================================
    // UI CONSTRUCTION
    //
    // This builds the entire chat interface in code (no .tscn scene file).
    // In Godot you can either build UI in the editor or in code — this
    // plugin does it all in code so there are no extra files to manage.
    // =======================================================================

    private void BuildUi()
    {
        // -- Make the dock fill the entire panel area ----------------------
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AnchorRight = 1.0f;
        AnchorBottom = 1.0f;
        CustomMinimumSize = new Vector2(0, 240);

        // -- Outer panel (dark background for the whole dock) ---------------
        PanelContainer dockPanel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
        };
        dockPanel.AddThemeStyleboxOverride("panel", MakeStyle(BgColor, 0, BorderColor));
        AddChild(dockPanel);

        // -- Root layout (vertical: header / output / input) ----------------
        VBoxContainer root = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", 0);
        dockPanel.AddChild(root);

        // -- HEADER BAR ----------------------------------------------------
        HBoxContainer header = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 28),
        };
        header.AddThemeConstantOverride("separation", 8);
        header.AddThemeStyleboxOverride("panel", MakeStyle(PanelBgColor, 0, BorderColor));
        root.AddChild(header);

        // Padding inside the header
        MarginContainer headerPad = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        headerPad.AddThemeConstantOverride("margin_left", 10);
        headerPad.AddThemeConstantOverride("margin_right", 10);
        header.AddChild(headerPad);

        // Inner horizontal layout for header contents
        HBoxContainer headerInner = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        headerInner.AddThemeConstantOverride("separation", 8);
        headerInner.Alignment = BoxContainer.AlignmentMode.Begin;
        headerPad.AddChild(headerInner);

        // Title: "godot-mcp"
        Label title = new Label
        {
            Text = "godot-mcp",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyMonospaceFont(title);
        title.AddThemeColorOverride("font_color", AccentColor);
        title.AddThemeFontSizeOverride("font_size", 13);
        title.VerticalAlignment = VerticalAlignment.Center;
        headerInner.AddChild(title);

        // Version: "v0.1.0"
        Label version = new Label
        {
            Text = "v0.1.0",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyMonospaceFont(version);
        version.AddThemeColorOverride("font_color", FgDimColor);
        version.AddThemeFontSizeOverride("font_size", 11);
        version.VerticalAlignment = VerticalAlignment.Center;
        headerInner.AddChild(version);

        // Spacer (pushes status to the right)
        Control spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        headerInner.AddChild(spacer);

        // Status dot (colored circle)
        _statusDot = new Label
        {
            Text = "\u25CF",  // Unicode filled circle ●
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyMonospaceFont(_statusDot);
        _statusDot.AddThemeColorOverride("font_color", StatusBusyColor);
        _statusDot.AddThemeFontSizeOverride("font_size", 12);
        _statusDot.VerticalAlignment = VerticalAlignment.Center;
        headerInner.AddChild(_statusDot);

        // Status text
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

        // Small spacer before the clear button
        Control headerSpacer = new Control { CustomMinimumSize = new Vector2(16, 0) };
        headerInner.AddChild(headerSpacer);

        // Clear button
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

        // -- SEPARATOR (thin line below header) -----------------------------
        root.AddChild(MakeSeparator());

        // -- OUTPUT AREA (scrollable text showing the conversation) ---------
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
        _output.AddThemeFontOverride("normal_font", GetMonospaceFont());
        _output.AddThemeFontOverride("bold_font", GetMonospaceFont());
        _output.AddThemeFontOverride("mono_font", GetMonospaceFont());
        _output.AddThemeFontSizeOverride("normal_font_size", 12);
        _output.AddThemeConstantOverride("line_separation", 2);

        // Padding around the output text
        MarginContainer outputMargin = new MarginContainer
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

        // -- SEPARATOR (thin line above input) ------------------------------
        root.AddChild(MakeSeparator());

        // -- INPUT ROW (text box + send button) ----------------------------
        PanelContainer inputPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 36),
        };
        inputPanel.AddThemeStyleboxOverride("panel", MakeStyle(PanelBgColor, 0, BorderColor));
        root.AddChild(inputPanel);

        MarginContainer inputMargin = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        inputMargin.AddThemeConstantOverride("margin_left", 12);
        inputMargin.AddThemeConstantOverride("margin_right", 12);
        inputMargin.AddThemeConstantOverride("margin_top", 4);
        inputMargin.AddThemeConstantOverride("margin_bottom", 4);
        inputPanel.AddChild(inputMargin);

        HBoxContainer inputRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        inputRow.AddThemeConstantOverride("separation", 8);
        inputRow.Alignment = BoxContainer.AlignmentMode.Begin;
        inputMargin.AddChild(inputRow);

        // ">" prompt character
        Label promptGlyph = new Label
        {
            Text = ">",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyMonospaceFont(promptGlyph);
        promptGlyph.AddThemeColorOverride("font_color", AccentColor);
        promptGlyph.AddThemeFontSizeOverride("font_size", 13);
        promptGlyph.VerticalAlignment = VerticalAlignment.Center;
        inputRow.AddChild(promptGlyph);

        // Text input box
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

        // Make the input box transparent (no visible border/background)
        Color transparent = new Color(0, 0, 0, 0);
        _input.AddThemeStyleboxOverride("normal", MakeStyle(transparent, 0, transparent));
        _input.AddThemeStyleboxOverride("focus", MakeStyle(transparent, 0, transparent));
        _input.AddThemeStyleboxOverride("read_only", MakeStyle(transparent, 0, transparent));

        // When the user presses Enter in the input box, send the message
        _input.TextSubmitted += OnSend;
        inputRow.AddChild(_input);

        // Send button
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

        // When the send button is clicked, send the current input text
        _sendBtn.Pressed += () => OnSend(_input.Text);
        inputRow.AddChild(_sendBtn);
    }

    // =======================================================================
    // SENDING MESSAGES
    // =======================================================================

    // Called when the user presses Enter or clicks "send"
    // The "async void" return type means this runs asynchronously but
    // doesn't return a value. The UI stays responsive while waiting.
    private async void OnSend(string text)
    {
        text = text.Trim();

        // Don't send empty messages or send while already waiting
        if (text == "" || _waiting)
        {
            return;
        }

        if (_agent == null)
        {
            AppendMessage("error", "agent not initialized - check GROQ_API_KEY in .env");
            return;
        }

        // Clear the input box and show the user's message
        _input.Clear();
        AppendMessage("user", text);
        SetWaiting(true);

        try
        {
            // Send the message to the agent and wait for a response
            ChatResult result = await _agent.ChatAsync(text);

            // Show the AI's reply
            AppendMessage("ai", result.Reply);

            // If any tools were called, show which ones
            if (result.ToolsUsed.Count > 0)
            {
                string toolList = string.Join(", ", result.ToolsUsed);
                AppendMessage("tool", "called: " + toolList);
            }

            SetStatus("ready", StatusOkColor);
        }
        catch (Exception ex)
        {
            AppendMessage("error", ex.Message);
            SetStatus("error", StatusErrColor);
        }
        finally
        {
            // Always re-enable the input, even if an error occurred
            SetWaiting(false);
        }
    }

    // =======================================================================
    // MESSAGE DISPLAY
    // =======================================================================

    // Adds a message to the output area with color-coding by role
    private void AppendMessage(string role, string text)
    {
        // Pick the prefix label and color based on the role
        string prefix;
        Color prefixColor;
        bool isDim;

        if (role == "user")
        {
            prefix = "you";
            prefixColor = UserRoleColor;
            isDim = false;
        }
        else if (role == "ai")
        {
            prefix = "ai";
            prefixColor = AiRoleColor;
            isDim = false;
        }
        else if (role == "system")
        {
            prefix = "sys";
            prefixColor = SysRoleColor;
            isDim = true;
        }
        else if (role == "error")
        {
            prefix = "err";
            prefixColor = ErrRoleColor;
            isDim = false;
        }
        else if (role == "tool")
        {
            prefix = "tool";
            prefixColor = ToolRoleColor;
            isDim = true;
        }
        else
        {
            prefix = role;
            prefixColor = FgColor;
            isDim = false;
        }

        // Escape BBCode special characters ([ becomes [[)
        // and add indentation after newlines so wrapped lines align
        string safe = text.Replace("[", "[[");
        safe = safe.Replace("\n", "\n  ");

        // Convert the prefix color to hex (e.g. "#8CC8FF")
        string prefixHex = ColorToHex(prefixColor);

        // Body text color: use dim color for system/tool messages, normal for others
        Color bodyColor;
        if (isDim)
        {
            bodyColor = FgDimColor;
        }
        else
        {
            bodyColor = FgColor;
        }
        string bodyHex = ColorToHex(bodyColor);

        // Append to the output using BBCode markup
        // Format: [color=#hex][b]prefix[/b][/color]  [color=#hex]message[/color]\n
        _output.AppendText(
            "[color=" + prefixHex + "][b]" + prefix + "[/b][/color]  " +
            "[color=" + bodyHex + "]" + safe + "[/color]\n");
    }

    // Convert a Godot Color to a hex string like "#FF8C33"
    private string ColorToHex(Color c)
    {
        int r = (int)(c.R * 255);
        int g = (int)(c.G * 255);
        int b = (int)(c.B * 255);
        return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
    }

    // =======================================================================
    // STATE MANAGEMENT
    // =======================================================================

    // Enable/disable the input while waiting for a response
    private void SetWaiting(bool waiting)
    {
        _waiting = waiting;
        _sendBtn.Disabled = waiting;
        _input.Editable = !waiting;

        if (waiting)
        {
            _sendBtn.Text = "...";
            SetStatus("thinking", StatusBusyColor);
        }
        else
        {
            _sendBtn.Text = "send";
        }
    }

    // Update the status text and color in the header
    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", color);
        _statusDot.AddThemeColorOverride("font_color", color);
    }

    // Clear the chat output
    private void ClearChat()
    {
        _output.Clear();
        AppendMessage("system", "chat cleared.");
    }

    // =======================================================================
    // FONT + STYLE HELPERS
    // =======================================================================

    // Get (or cache) the engine's default monospace font
    private Font? GetMonospaceFont()
    {
        if (_monospaceFont != null)
        {
            return _monospaceFont;
        }

        _monospaceFont = GetThemeDefaultFont();
        return _monospaceFont;
    }

    // Apply the monospace font to a Label, Button, or LineEdit
    private void ApplyMonospaceFont(Control node)
    {
        Font? monoFont = GetMonospaceFont();

        if (node is Label lbl)
        {
            lbl.AddThemeFontOverride("font", monoFont);
        }
        else if (node is Button btn)
        {
            btn.AddThemeFontOverride("font", monoFont);
        }
        else if (node is LineEdit le)
        {
            le.AddThemeFontOverride("font", monoFont);
        }
    }

    // Create a flat style box (used for backgrounds and borders)
    // borderWidth > 0 adds a bottom border; 0 means no border
    private static StyleBoxFlat MakeStyle(Color bgColor, int borderWidth, Color borderColor)
    {
        StyleBoxFlat sb = new StyleBoxFlat();
        sb.BgColor = bgColor;
        sb.BorderWidthLeft = 0;
        sb.BorderWidthRight = 0;
        sb.BorderWidthTop = 0;
        sb.BorderWidthBottom = 0;
        sb.ContentMarginLeft = 0;
        sb.ContentMarginRight = 0;
        sb.ContentMarginTop = 0;
        sb.ContentMarginBottom = 0;

        if (borderWidth > 0)
        {
            sb.BorderWidthBottom = borderWidth;
            sb.BorderColor = borderColor;
        }

        return sb;
    }

    // Create a thin horizontal separator line
    private static HSeparator MakeSeparator()
    {
        HSeparator sep = new HSeparator
        {
            CustomMinimumSize = new Vector2(0, 1),
        };
        sep.AddThemeColorOverride("separator", BorderColor);
        return sep;
    }
}
#endif