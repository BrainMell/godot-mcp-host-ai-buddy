#if TOOLS
using Godot;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace GodotMCP;

// ---------------------------------------------------------------------------
// ChatDock — the UI panel that appears at the bottom of the Godot editor
//
// This is the only file that deals with UI. Everything else is logic.
//
// It does three things:
//   1. Builds the chat interface (input box, output area, send button)
//   2. When the user sends a message, passes it to ChatService.SendMessageAsync()
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
    private Button _copyBtn  = null!;
    private Button _sessionsBtn = null!;
    private Button _newChatBtn = null!;
    private Button _prevSessionBtn = null!;
    private Button _nextSessionBtn = null!;
    private Label _sessionIndexLbl = null!;

    // -- State -------------------------------------------------------------
    private int _currentSessionIndex = -1;
    private int _totalSessionsCount = 0;
    // ? means this can be null (it's null if the API key wasn't found)
    private ChatService? _agent;
    private GodotTools _tools = null!;
    private bool _waiting;             // True while we're waiting for a response

    // Session state — tracks whether Gemini has been primed with the system prompt.
    // _sessionActive : true = browser is on an active Gemini chat URL (keepSession can be true)
    // _sessionPrimed : true = system prompt has already been sent, AI knows its role
    private bool _sessionActive = false;
    private bool _sessionPrimed = false;
    private Task? _primingTask;
    private string _currentModel = "gemini";

    // Tool call parser: matches [CALL]{...json...}[/CALL]
    // The JSON object must have a "tool" key (tool name) and optionally other keys as args.
    private static readonly Regex ToolCallRegex = new Regex(@"\[CALL\]([\ \s\S]*?)\[/CALL\]");

    // -- Color palette (Godot Editor flavored) -------------------------------
    // These match Godot 4's editor default dark theme colors
    private static readonly Color BgColor         = new Color(0.13f, 0.15f, 0.20f);   // #202531 - main background
    private static readonly Color PanelBgColor    = new Color(0.16f, 0.19f, 0.25f);   // #2a303f - panels & header
    private static readonly Color BorderColor     = new Color(0.25f, 0.28f, 0.35f);   // #404759 - border line
    private static readonly Color FgColor         = new Color(0.88f, 0.90f, 0.93f);   // #e0e0e0 - default white text
    private static readonly Color FgDimColor      = new Color(0.55f, 0.57f, 0.65f);   // #8b92a5 - muted gray text
    private static readonly Color UserRoleColor   = new Color(0.47f, 0.73f, 0.95f);   // #78baec - godot blue for user
    private static readonly Color AiRoleColor     = new Color(0.55f, 0.85f, 0.65f);   // #8cda8c - assistant green
    private static readonly Color SysRoleColor    = new Color(0.60f, 0.62f, 0.66f);   // system messages
    private static readonly Color ErrRoleColor    = new Color(0.95f, 0.40f, 0.40f);   // #f26666 - error red
    private static readonly Color ToolRoleColor   = new Color(0.90f, 0.75f, 0.45f);   // #e6bf73 - tool amber
    private static readonly Color AccentColor     = new Color(0.28f, 0.55f, 0.75f);   // #478cbf - primary godot blue accent
    private static readonly Color StatusOkColor   = new Color(0.40f, 0.75f, 0.45f);   // ready green
    private static readonly Color StatusBusyColor = new Color(0.90f, 0.78f, 0.30f);   // busy yellow
    private static readonly Color StatusErrColor  = new Color(0.85f, 0.40f, 0.40f);   // error red

    // -- Cached monospace font ----------------------------------------------
    // ? because it's null until the first call to GetMonospaceFont()
    private Font? _monospaceFont;

    // -----------------------------------------------------------------------
    // _Ready — called by Godot when this node enters the scene tree
    // -----------------------------------------------------------------------

    public override void _Ready()
    {
        BuildUi();
        TryInitAgent();

        AppendMessage("system", "godot-mcp ready. type a command or ask a question.");
        AppendMessage("system", "try: \"create a Node2D called Player at [200, 150]\"");

        _primingTask = AutoPrimeSessionAsync();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete)
        {
            _agent?.Dispose();
        }
    }

    public override void _ExitTree()
    {
        if (_clearBtn != null && _clearBtn.IsConnected("pressed", new Callable(this, nameof(ClearChat))))
            _clearBtn.Disconnect("pressed", new Callable(this, nameof(ClearChat)));
        if (_copyBtn != null && _copyBtn.IsConnected("pressed", new Callable(this, nameof(CopyChat))))
            _copyBtn.Disconnect("pressed", new Callable(this, nameof(CopyChat)));
        if (_input != null && _input.IsConnected("text_submitted", new Callable(this, nameof(OnSend))))
            _input.Disconnect("text_submitted", new Callable(this, nameof(OnSend)));
        if (_input != null && _input.IsConnected("gui_input", new Callable(this, nameof(OnInputGuiInput))))
            _input.Disconnect("gui_input", new Callable(this, nameof(OnInputGuiInput)));
        if (_sendBtn != null && _sendBtn.IsConnected("pressed", new Callable(this, nameof(OnSendButtonPressed))))
            _sendBtn.Disconnect("pressed", new Callable(this, nameof(OnSendButtonPressed)));
        if (_sessionsBtn != null && _sessionsBtn.IsConnected("pressed", new Callable(this, nameof(OnSessionsHistoryPressed))))
            _sessionsBtn.Disconnect("pressed", new Callable(this, nameof(OnSessionsHistoryPressed)));
        if (_newChatBtn != null && _newChatBtn.IsConnected("pressed", new Callable(this, nameof(OnNewChatPressed))))
            _newChatBtn.Disconnect("pressed", new Callable(this, nameof(OnNewChatPressed)));
        if (_prevSessionBtn != null && _prevSessionBtn.IsConnected("pressed", new Callable(this, nameof(OnPrevSessionPressed))))
            _prevSessionBtn.Disconnect("pressed", new Callable(this, nameof(OnPrevSessionPressed)));
        if (_nextSessionBtn != null && _nextSessionBtn.IsConnected("pressed", new Callable(this, nameof(OnNextSessionPressed))))
            _nextSessionBtn.Disconnect("pressed", new Callable(this, nameof(OnNextSessionPressed)));
    }

    // -----------------------------------------------------------------------
    // TryInitAgent — create the ChatService and GodotTools
    // -----------------------------------------------------------------------

    private void TryInitAgent()
    {
        try
        {
            // ChatService initiates the method ChatService which opens up gemini
            _agent = new ChatService();
            _tools = new GodotTools();
            SetStatus("ready", StatusOkColor);
        }
        catch (Exception ex)
        {
            AppendMessage("error", ex.Message);
            SetStatus("error", StatusErrColor);
        }
    }

    public void ForceDisposeAgent()
    {
        if (_agent != null)
        {
            _agent.Dispose();
            _agent = null;
        }
    }

    // -----------------------------------------------------------------------
    // GetSystemPrompt — builds the system instructions including tool schemas
    // -----------------------------------------------------------------------
    private string GetSystemPrompt()
    {
        var definitions = _tools.GetToolDefinitions();
        string toolsJson = JsonSerializer.Serialize(definitions, new JsonSerializerOptions { WriteIndented = true });

        return $@"You are an AI agent built to help the user build their game inside the Godot Editor.
You have access to a set of Godot editor tools to inspect and modify the current scene tree.

### Best Practices for Node Selection & Composition
- **Compare Node Complexity**: Before creating a node, compare Godot's built-in node classes to choose the best fit for the task. Avoid lazy workarounds (e.g., scaling up a generic `Node2D` or `Sprite2D` to represent a repeating floor grid). Instead, use the correct specialized node type (e.g., `TileMap` or `TileMapLayer` for tiling floors, `CharacterBody2D` for physics characters).
- **Proactive Node Configuration**: You DO NOT have visual access to the Godot Editor (e.g., yellow warning triangles). You must proactively configure nodes that require setup. 
  - To assign a new shape to a `CollisionShape2D`, use `setnodeproperty` with `property: ""shape""` and `value: ""RectangleShape2D""` (or CircleShape2D).
  - To edit a sub-property of that shape, use Godot's indexed property path syntax with a colon, e.g., `property: ""shape:size""` and `value: ""[20, 20]""`.
- **Camera Parent Relationships**: When adding a camera designed to follow a character, instantiate the `Camera2D` as a direct child of that character node so it follows the player automatically without requiring manual position syncing.
- **Composition over Recreation**: Never recreate a character or asset's node tree from scratch in a new scene. Use the `instantiate_subscene` tool to instance existing scene files (like `Character.tscn`) inside world/level scenes.

### Core Modes of Operation
You must dynamically choose between two distinct modes of operation depending on the user's input:

1. **CONVERSATIONAL MODE**:
   - **Trigger**: The user is asking questions, seeking advice, asking about your capabilities, requesting a listing of tools, or discussing game logic.
   - **Behavior**: Respond in normal, helpful, conversational text. Explain your answers clearly. Do NOT output any [CALL] blocks.

2. **ACTION MODE**:
   - **Trigger**: The user requests an action inside the editor (e.g., ""create a node"", ""delete the Player"", ""save the scene"", ""list files"").
   - **Behavior**: Your response **MUST contain ONLY the [CALL] block and absolutely nothing else**. No introduction, no explanation, no filler text before or after it.

   STRICT RULE: When you are in Action Mode, your ENTIRE response must be exactly one [CALL] block. No introduction. No explanation. No text before or after the tags. Writing any text outside [CALL]...[/CALL] while in Action Mode is an error.

   WRONG (never do this):
     Sure! I'll create that node for you.
     [CALL]{{""tool"": ""create_node"", ""node_type"": ""Node2D""}}[/CALL]

   CORRECT:
     [CALL]{{""tool"": ""create_node"", ""node_type"": ""Node2D""}}[/CALL]

### Tool Call Format
In **Action Mode**, your ENTIRE response must be exactly this — nothing more:

[CALL]
{{""tool"": ""tool_name_here"", ""param1"": ""value1"", ""param2"": ""value2""}}
[/CALL]

Rules for the format:
- `tool` is REQUIRED and must be the exact tool name from the list below.
- All other keys are the arguments for that tool, matching its parameter names exactly.
- The JSON inside [CALL] tags must be valid. Use double quotes for all strings.
- CRITICAL: When writing scripts/code inside the ""content"" parameter of ""write_file"", you MUST escape all double quotes as \"" or use single quotes ' in your GDScript strings (e.g. Input.is_action_just_pressed('ui_accept')). Unescaped double quotes will corrupt the JSON and cause a parsing failure.
- Do NOT wrap it in markdown code fences (no ```). Do NOT add any text outside the tags.

#### Concrete Examples:

User: ""Create a Sprite2D node named Background""
Your entire response:
[CALL]
{{""tool"": ""create_2d_node"", ""node_type"": ""Sprite2D"", ""node_name"": ""Background""}}
[/CALL]

User: ""Create a new scene called Character.tscn with a Node2D root""
Your entire response:
[CALL]
{{""tool"": ""create_new_scene"", ""scene_path"": ""res://Character.tscn"", ""root_type"": ""Node2D"", ""root_name"": ""Character""}}
[/CALL]

User: ""Save the scene""
Your entire response:
[CALL]
{{""tool"": ""save_scene""}}
[/CALL]

### Handling Tool Results
After you output a [CALL] block, the system will execute it and return:
[RESULT]
result_json
[/RESULT]

- If you need multiple tool calls to complete a request, do them ONE AT A TIME. Output one [CALL] block, wait for the [RESULT], then output the next.
- Once all actions are done, write a short conversational summary of what was accomplished. No [CALL] tags in this final reply.

---
### Available Tools
{toolsJson}";
    }

    // -----------------------------------------------------------------------
    // PrimeSessionAsync — sends the system prompt to a brand new Gemini chat
    // and waits for a short acknowledgment. After this, all user messages are
    // sent as plain short text inside the same session (keepSession = true).
    // -----------------------------------------------------------------------
    private async Task<string> PrimeSessionAsync()
    {
        // keepSession = false → navigate to a fresh AI conversation
        string primeMessage = GetSystemPrompt()
            + "\n\nYou are now set up. Respond only with: READY";

        return await _agent!.SendMessageAsync(primeMessage, false, _currentModel, keepSession: false);
    }

    private async Task AutoPrimeSessionAsync()
    {
        if (_agent == null) return;

        try
        {
            SetWaiting(true);
            AppendMessage("system", "priming session with tool definitions...");
            SetStatus("priming", StatusBusyColor);

            string primeAck = await PrimeSessionAsync();
            GD.Print($"[GodotMCP] Prime ack: {primeAck}");

            _sessionPrimed = true;
            _sessionActive = true;
            AppendMessage("system", "session ready.");
            SetStatus("ready", StatusOkColor);

            // Wait for priming to finish and then load the history
            await ShowChatSessionsAsync(silent: true);
        }
        catch (Exception ex)
        {
            AppendMessage("error", "Priming failed: " + ex.Message);
            SetStatus("error", StatusErrColor);
        }
        finally
        {
            SetWaiting(false);
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

        // Spacer before sessions UI
        Control headerSpacer1 = new Control { CustomMinimumSize = new Vector2(8, 0) };
        headerInner.AddChild(headerSpacer1);

        // Sessions history button
        _sessionsBtn = new Button
        {
            Text = "history",
            Flat = true,
            CustomMinimumSize = new Vector2(0, 22),
            MouseFilter = MouseFilterEnum.Stop,
        };
        ApplyMonospaceFont(_sessionsBtn);
        _sessionsBtn.AddThemeFontSizeOverride("font_size", 11);
        _sessionsBtn.AddThemeColorOverride("font_color", FgDimColor);
        _sessionsBtn.AddThemeColorOverride("font_hover_color", AccentColor);
        _sessionsBtn.Connect("pressed", new Callable(this, nameof(OnSessionsHistoryPressed)));
        headerInner.AddChild(_sessionsBtn);

        // New Chat button
        _newChatBtn = new Button
        {
            Text = "new",
            Flat = true,
            CustomMinimumSize = new Vector2(0, 22),
            MouseFilter = MouseFilterEnum.Stop,
        };
        ApplyMonospaceFont(_newChatBtn);
        _newChatBtn.AddThemeFontSizeOverride("font_size", 11);
        _newChatBtn.AddThemeColorOverride("font_color", FgDimColor);
        _newChatBtn.AddThemeColorOverride("font_hover_color", AccentColor);
        _newChatBtn.Connect("pressed", new Callable(this, nameof(OnNewChatPressed)));
        headerInner.AddChild(_newChatBtn);

        // Prev Session button
        _prevSessionBtn = new Button
        {
            Text = "<",
            Flat = true,
            CustomMinimumSize = new Vector2(15, 22),
            MouseFilter = MouseFilterEnum.Stop,
            Disabled = true
        };
        ApplyMonospaceFont(_prevSessionBtn);
        _prevSessionBtn.AddThemeFontSizeOverride("font_size", 11);
        _prevSessionBtn.AddThemeColorOverride("font_color", FgDimColor);
        _prevSessionBtn.AddThemeColorOverride("font_hover_color", AccentColor);
        _prevSessionBtn.Connect("pressed", new Callable(this, nameof(OnPrevSessionPressed)));
        headerInner.AddChild(_prevSessionBtn);

        // Session Index Label
        _sessionIndexLbl = new Label
        {
            Text = "[-]",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        ApplyMonospaceFont(_sessionIndexLbl);
        _sessionIndexLbl.AddThemeColorOverride("font_color", FgDimColor);
        _sessionIndexLbl.AddThemeFontSizeOverride("font_size", 11);
        _sessionIndexLbl.VerticalAlignment = VerticalAlignment.Center;
        headerInner.AddChild(_sessionIndexLbl);

        // Next Session button
        _nextSessionBtn = new Button
        {
            Text = ">",
            Flat = true,
            CustomMinimumSize = new Vector2(15, 22),
            MouseFilter = MouseFilterEnum.Stop,
            Disabled = true
        };
        ApplyMonospaceFont(_nextSessionBtn);
        _nextSessionBtn.AddThemeFontSizeOverride("font_size", 11);
        _nextSessionBtn.AddThemeColorOverride("font_color", FgDimColor);
        _nextSessionBtn.AddThemeColorOverride("font_hover_color", AccentColor);
        _nextSessionBtn.Connect("pressed", new Callable(this, nameof(OnNextSessionPressed)));
        headerInner.AddChild(_nextSessionBtn);

        // Spacer before clear button
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
        _clearBtn.Connect("pressed", new Callable(this, nameof(ClearChat)));
        headerInner.AddChild(_clearBtn);

        // Copy button
        _copyBtn = new Button
        {
            Text = "copy",
            Flat = true,
            CustomMinimumSize = new Vector2(0, 22),
            MouseFilter = MouseFilterEnum.Stop,
        };
        ApplyMonospaceFont(_copyBtn);
        _copyBtn.AddThemeFontSizeOverride("font_size", 11);
        _copyBtn.AddThemeColorOverride("font_color", FgDimColor);
        _copyBtn.AddThemeColorOverride("font_hover_color", FgColor);
        _copyBtn.Connect("pressed", new Callable(this, nameof(CopyChat)));
        headerInner.AddChild(_copyBtn);

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
        _input.Connect("text_submitted", new Callable(this, nameof(OnSend)));
        _input.Connect("gui_input", new Callable(this, nameof(OnInputGuiInput)));
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
        _sendBtn.AddThemeStyleboxOverride("hover", MakeStyle(new Color(0.20f, 0.23f, 0.30f), 0, BorderColor));
        _sendBtn.AddThemeStyleboxOverride("disabled", MakeStyle(PanelBgColor, 0, BorderColor));
        _sendBtn.AddThemeStyleboxOverride("focus", MakeStyle(PanelBgColor, 0, BorderColor));
        _sendBtn.AddThemeStyleboxOverride("pressed", MakeStyle(PanelBgColor, 0, AccentColor));

        // When the send button is clicked, send the current input text
        _sendBtn.Connect("pressed", new Callable(this, nameof(OnSendButtonPressed)));
        inputRow.AddChild(_sendBtn);
    }

    private void OnSendButtonPressed()
    {
        OnSend(_input.Text);
    }

    private void OnInputGuiInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            if (keyEvent.Keycode == Key.Up)
            {
                GetViewport().SetInputAsHandled();
                OnPrevSessionPressed();
            }
            else if (keyEvent.Keycode == Key.Down)
            {
                GetViewport().SetInputAsHandled();
                OnNextSessionPressed();
            }
        }
    }

    private async void SetModel()
    {
        
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

        if (text.StartsWith("/"))
        {
            // Handle slash commands
            string command = text.Substring(1).Trim().ToLower();
            if (command == "sessions" || command == "history")
            {
                _input.Clear();
                await ShowChatSessionsAsync();
                return;
            }
            else if (command.StartsWith("session ") || command.StartsWith("select "))
            {
                _input.Clear();
                string idxStr = command.Substring(command.IndexOf(" ")).Trim();
                if (int.TryParse(idxStr, out int idx))
                {
                    await NavigateToSessionAsync(idx);
                }
                else
                {
                    AppendMessage("error", "Invalid session index. Usage: /session <index>");
                }
                return;
            }
            else if (command.StartsWith("rename "))
            {
                _input.Clear();
                string newName = text.Substring(text.IndexOf(" ") + 1).Trim();
                if (string.IsNullOrEmpty(newName))
                {
                    AppendMessage("error", "Usage: /rename <new_name>");
                }
                else
                {
                    await RenameActiveSessionAsync(newName);
                }
                return;
            }
            else if (command == "delete")
            {
                _input.Clear();
                await DeleteActiveSessionAsync();
                return;
            }
            else if (command == "clear")
            {
                ClearChat();
                return;
            }
            else if (command == "copy")
            {
                CopyChat();
                return;
            }
            else if (command == "model")
            {
                AppendMessage("system", "There are currently 3 available models platforms: 'gemini', 'Chatgpt', and 'Zai'.\nUse '/model <model_name>' to switch between them.");
                return;
            }
            else if (command == "model gemini")
            {
                AppendMessage("system", "Switching to Gemini model...");
                _currentModel = "gemini";
                _sessionPrimed = false;
                _sessionActive = false;
                _primingTask = AutoPrimeSessionAsync();
                return;
            }
            else if (command == "model chatgpt")
            {
                AppendMessage("system", "Switching to ChatGPT model...");
                _currentModel = "chatgpt";
                _sessionPrimed = false;
                _sessionActive = false;
                _primingTask = AutoPrimeSessionAsync();
                return;
            }
            else if (command == "model zai")
            {
                AppendMessage("system", "Switching to Zai model...");
                _currentModel = "zai";
                _sessionPrimed = false;
                _sessionActive = false;
                _primingTask = AutoPrimeSessionAsync();
                return;
            }
            else if (command == "help")
            {
                AppendMessage("system", "Available commands:\n" +
                    "/sessions - List past chat sessions\n" +
                    "/session <index> - Switch to a past chat session by index\n" +
                    "/rename <new_name> - Rename the current active chat session\n" +
                    "/delete - Delete the current active chat session\n" +
                    "/clear - Clear the chat output\n" +
                    "/copy - Copy the chat log to clipboard\n" +
                    "/model <model_name> - Switch between available models (gemini, Chatgpt, Zai)\n" +
                    "/help - Show this help message");
                return;
            }
            else
            {
                AppendMessage("error", $"unknown command: {command}");
                return;
            }
        }

        if (_agent == null)
        {
            AppendMessage("error", "agent not initialized.");
            return;
        }

        // Clear the input box and show the user's message
        _input.Clear();
        AppendMessage("user", text);
        SetWaiting(true);

        try
        {
            // Issue 5: Add session health re-prime check
            if (_sessionPrimed && _agent != null && !_agent.IsSessionHealthy())
            {
                AppendMessage("system", "session lost — re-priming...");
                _sessionPrimed = false;
                _sessionActive = false;
                _primingTask = AutoPrimeSessionAsync();
            }

            // --- Session priming --------------------------------------------
            if (!_sessionPrimed)
            {
                if (_primingTask != null)
                {
                    AppendMessage("system", "Waiting for session priming to finish...");
                    await _primingTask;
                }
                else
                {
                    _primingTask = AutoPrimeSessionAsync();
                    await _primingTask;
                }
            }

            // Now send the user's actual message as a clean, short turn
            // inside the already-primed session (keepSession = true)
            string currentMessage = text;
            bool keepGoing = true;
            int turnCount = 0;

            while (keepGoing && turnCount < 10)
            {
                turnCount++;

                // Send the message to ChatService and wait for a response
                // Always keepSession = true after priming so we stay in the same conversation
                string reply = await _agent!.SendMessageAsync(currentMessage, false, _currentModel, keepSession: true);

                // Debug: log the raw AI response so we can see exactly what was returned
                GD.Print($"[GodotMCP] AI raw reply (turn {turnCount}): {reply}");

                // Issue 1: Extract display text and print tool statuses
                string displayText = ToolCallRegex.Replace(reply, "").Trim();

                foreach (Match m in ToolCallRegex.Matches(reply))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(m.Groups[1].Value.Trim());
                        var toolName = doc.RootElement.GetProperty("tool").GetString();
                        AppendMessage("tool", $"⚙ {toolName}...");
                    }
                    catch
                    {
                        AppendMessage("tool", "⚙ executing...");
                    }
                }

                if (!string.IsNullOrEmpty(displayText))
                {
                    AppendMessage("assistant", displayText);
                }

                Match match = ToolCallRegex.Match(reply);
                if (match.Success)
                {
                    // Extract the JSON blob inside the [CALL]...[/CALL] tags
                    string callJson = match.Groups[1].Value.Trim();

                    GD.Print($"[GodotMCP] Extracted call JSON: {callJson}");

                    // Parse the JSON to extract the tool name and build the args object
                    Dictionary<string, JsonElement>? callObj;
                    try
                    {
                        callObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(callJson);
                    }
                    catch (Exception jsonEx)
                    {
                        string errorText = $"JSON parsing failed: {jsonEx.Message}. Raw block was: {callJson}. " +
                                           "Please output the tool call again with valid JSON. Remember to escape double quotes inside content/code blocks, or use single quotes for GDScript strings.";
                        AppendMessage("error", $"Tool call JSON was malformed. Prompting AI to self-correct...");

                        currentMessage = "TOOL RESULT (do not reply to this directly — continue your task):\n" +
                                         $"[RESULT]\n{{\"error\": {JsonSerializer.Serialize(errorText)}}}\n[/RESULT]";
                        continue; // Keep loop active, sending the error message to the AI
                    }

                    if (callObj == null || !callObj.ContainsKey("tool"))
                    {
                        string errorText = $"Tool call missing \"tool\" key. Raw block was: {callJson}. " +
                                           "Please output the tool call again with a valid \"tool\" key matching an available tool.";
                        AppendMessage("error", $"Tool call missing \"tool\" key. Prompting AI to self-correct...");

                        currentMessage = "TOOL RESULT (do not reply to this directly — continue your task):\n" +
                                         $"[RESULT]\n{{\"error\": {JsonSerializer.Serialize(errorText)}}}\n[/RESULT]";
                        continue;
                    }

                    string toolName = callObj["tool"].GetString() ?? "";

                    // Build args as a new JSON object without the "tool" key
                    callObj.Remove("tool");
                    string argsJson = JsonSerializer.Serialize(callObj);

                    AppendMessage("tool", $"→ {toolName}({Truncate(argsJson, 120)})");

                    // Execute the tool
                    string toolResult;
                    try
                    {
                        toolResult = await _tools.ExecuteAsync(toolName, argsJson);
                    }
                    catch (Exception toolEx)
                    {
                        toolResult = JsonSerializer.Serialize(new { error = toolEx.Message });
                    }

                    GD.Print($"[GodotMCP] Tool result: {toolResult}");
                    AppendMessage("tool", $"← {Truncate(toolResult, 200)}");

                    // Issue 2: Prefix the result message so Gemini knows it is tool output
                    currentMessage = "TOOL RESULT (do not reply to this directly — continue your task):\n" +
                                     $"[RESULT]\n{toolResult}\n[/RESULT]";
                }
                else
                {
                    // No tool call — done
                    keepGoing = false;
                }
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
        string header = "";
        Color headerColor = FgColor;
        string bodyText = "";
        bool useBlockIndent = false;

        if (role == "user")
        {
            header = "🧔🏻‍♂️ USER";
            headerColor = UserRoleColor;
            useBlockIndent = true;
        }
        else if (role == "ai" || role == "assistant")
        {
            header = "🤖 ASSISTANT";
            headerColor = AiRoleColor;
            useBlockIndent = true;
        }
        else if (role == "system")
        {
            header = "💻 SYSTEM";
            headerColor = SysRoleColor;
        }
        else if (role == "error")
        {
            header = "❌ ERROR";
            headerColor = ErrRoleColor;
        }
        else if (role == "tool")
        {
            header = "🔧 TOOL";
            headerColor = ToolRoleColor;
        }
        else
        {
            header = role.ToUpper();
            headerColor = FgColor;
        }

        string processedText = ConvertMarkdownToBbcode(text).Trim();
        string headerHex = ColorToHex(headerColor);

        if (useBlockIndent)
        {
            bodyText = "[color=" + ColorToHex(FgColor) + "]" + processedText + "[/color]";
            _output.AppendText(
                "[color=" + headerHex + "][b]" + header + "[/b][/color]\n" +
                "[indent]" + bodyText + "[/indent]\n\n"
            );
        }
        else
        {
            Color bodyColor = (role == "system" || role == "tool") ? FgDimColor : FgColor;
            string bodyHex = ColorToHex(bodyColor);
            bodyText = "[color=" + bodyHex + "]" + processedText + "[/color]";
            if (role == "system" || role == "tool")
            {
                bodyText = "[i]" + bodyText + "[/i]";
            }

            _output.AppendText(
                "[color=" + headerHex + "][b]" + header + ":[/b][/color] " + bodyText + "\n"
            );
        }
    }

    private string ConvertMarkdownToBbcode(string text)
    {
        // First escape all brackets so we don't conflict with BBCode tags
        string safe = text.Replace("[", "[[");

        // Convert block code fences: ```lang\n...\n```
        var codeBlockRegex = new Regex(@"```(\w+)?\r?\n([\s\S]*?)```");
        safe = codeBlockRegex.Replace(safe, m => {
            string lang = m.Groups[1].Value;
            string code = m.Groups[2].Value;
            
            // Revert bracket escape for the code content so brackets in code show correctly
            string cleanCode = code.Replace("[[", "[");
            
            string header = string.IsNullOrEmpty(lang) ? "CODE" : lang.ToUpper();
            
            return "\n[color=#478cbf][b]── " + header + " ──[/b][/color]\n" +
                   "[color=#8ccaee][code]" + cleanCode + "[/code][/color]\n" +
                   "[color=#478cbf][b]────────────────[/b][/color]\n";
        });

        // Convert inline code: `code`
        var inlineCodeRegex = new Regex(@"`([^`\n]+)`");
        safe = inlineCodeRegex.Replace(safe, m => {
            string code = m.Groups[1].Value.Replace("[[", "[");
            return "[color=#8ccaee][code]" + code + "[/code][/color]";
        });

        // Convert bold: **text**
        var boldRegex = new Regex(@"\*\*([^\*]+)\*\*");
        safe = boldRegex.Replace(safe, "[b]$1[/b]");

        // Convert italic: *text* or _text_
        var italicRegex1 = new Regex(@"\*([^\*]+)\*");
        safe = italicRegex1.Replace(safe, "[i]$1[/i]");
        var italicRegex2 = new Regex(@"_([^_]+)_");
        safe = italicRegex2.Replace(safe, "[i]$1[/i]");

        // Convert bullet points: * or - at start of line
        var bulletRegex = new Regex(@"(?m)^[ \t]*[*+-][ \t]+(.*)$");
        safe = bulletRegex.Replace(safe, "  • $1");

        return safe;
    }

    // Convert a Godot Color to a hex string like "#FF8C33"
    private string ColorToHex(Color c)
    {
        int r = (int)(c.R * 255);
        int g = (int)(c.G * 255);
        int b = (int)(c.B * 255);
        return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
    }

    // Truncate a string for display — keeps the chat readable.
    // Full data is always preserved in GD.Print() for debugging.
    private static string Truncate(string s, int maxLen)
    {
        if (s.Length <= maxLen) return s;
        return s.Substring(0, maxLen) + $"… (+{s.Length - maxLen} chars)";
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
        _sessionActive = false;
        _sessionPrimed = false;
        AppendMessage("system", "chat cleared. Starting a new session...");
        _primingTask = AutoPrimeSessionAsync();
    }

    // Copy the full chat log as plain text to the clipboard
    private void CopyChat()
    {
        // _output.Text strips BBCode and gives us clean plain text
        string plainText = _output.GetParsedText();
        DisplayServer.ClipboardSet(plainText);
        AppendMessage("system", "chat copied to clipboard.");
    }

    // =======================================================================
    // CHAT SESSIONS HISTORY & NAVIGATION
    // =======================================================================

    private void OnNewChatPressed()
    {
        ClearChat();
    }

    private async void OnSessionsHistoryPressed()
    {
        await ShowChatSessionsAsync();
    }

    private async void OnPrevSessionPressed()
    {
        if (_currentSessionIndex > 0)
        {
            _currentSessionIndex--;
            await NavigateToSessionAsync(_currentSessionIndex);
        }
    }

    private async void OnNextSessionPressed()
    {
        if (_currentSessionIndex < _totalSessionsCount - 1)
        {
            _currentSessionIndex++;
            await NavigateToSessionAsync(_currentSessionIndex);
        }
    }

    private async Task ShowChatSessionsAsync(bool silent = false)
    {
        if (_agent == null)
        {
            if (!silent) AppendMessage("error", "agent not initialized.");
            return;
        }

        if (!silent) AppendMessage("system", "Fetching recent chat sessions from " + _currentModel + "...");
        if (!silent) SetWaiting(true);

        try
        {
            string result = await _agent.CheckChatHistoryAsync(_currentModel);
            if (!silent) AppendMessage("system", result);

            // Parse session count from the output text block
            int count = 0;
            string[] lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                if (line.Trim().StartsWith("["))
                {
                    count++;
                }
            }

            _totalSessionsCount = count;
            if (count > 0 && _currentSessionIndex == -1)
            {
                _currentSessionIndex = 0;
            }
            UpdateSessionUi();
        }
        catch (Exception ex)
        {
            if (!silent) AppendMessage("error", "Failed to fetch chat history: " + ex.Message);
        }
        finally
        {
            if (!silent) SetWaiting(false);
        }
    }

    private async Task NavigateToSessionAsync(int index)
    {
        if (_agent == null)
        {
            AppendMessage("error", "agent not initialized.");
            return;
        }

        AppendMessage("system", $"Navigating to session [{index}]...");
        SetWaiting(true);

        try
        {
            // 1. Get the session count and click it in the browser
            string result = await _agent.GetChatHistoryCountAsync(_currentModel, index);
            if (result.StartsWith("Error"))
            {
                AppendMessage("error", result);
                UpdateSessionUi();
                return;
            }

            // 2. Scrape the messages of this conversation first (before clearing the UI)
            string messagesStr = await _agent.GetChatHistoryMessagesAsync(_currentModel);
            
            // 3. Validate the system prompt signature anywhere in the scraped conversation
            bool isValid = messagesStr.Contains("Godot Editor") || 
                           messagesStr.Contains("GodotMCP") || 
                           messagesStr.Contains("[CALL]") ||
                           messagesStr.Contains("Respond only with: READY");

            if (!isValid)
            {
                AppendMessage("system", "This isn't a conversation made by the editor, so it can't be continued.");
                UpdateSessionUi();
                return;
            }

            // 5. If valid, set indices and clear the chat UI
            _currentSessionIndex = index;
            _sessionActive = true;
            _sessionPrimed = true;
            _output.Clear();

            // 6. Replace UI output with the entire conversation history
            string[] msgLines = messagesStr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in msgLines)
            {
                if (line.StartsWith("[ROLE:USER]"))
                {
                    string userText = line.Substring("[ROLE:USER]".Length).Replace("\\n", "\n").Trim();
                    // Is it JSON (tool response)?
                    if (IsJsonString(userText))
                    {
                        AppendMessage("tool", $"← {userText}");
                    }
                    else
                    {
                        AppendMessage("user", userText);
                    }
                }
                else if (line.StartsWith("[ROLE:AI]"))
                {
                    string aiText = line.Substring("[ROLE:AI]".Length).Replace("\\n", "\n").Trim();
                    // Is it JSON (tool call)?
                    if (IsJsonString(aiText))
                    {
                        AppendMessage("tool", $"→ {aiText}");
                    }
                    else
                    {
                        AppendMessage("gemini", aiText);
                    }
                }
            }
            UpdateSessionUi();
        }
        catch (Exception ex)
        {
            AppendMessage("error", "Failed to navigate session: " + ex.Message);
        }
        finally
        {
            SetWaiting(false);
        }
    }

    private bool IsJsonString(string text)
    {
        text = text.Trim();
        if ((text.StartsWith("{") && text.EndsWith("}")) || (text.StartsWith("[") && text.EndsWith("]")))
        {
            try
            {
                System.Text.Json.JsonDocument.Parse(text);
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private async Task RenameActiveSessionAsync(string newName)
    {
        if (_agent == null) return;
        if (_currentSessionIndex == -1)
        {
            AppendMessage("error", "No active session loaded to rename.");
            return;
        }
        AppendMessage("system", $"Renaming active session to '{newName}'...");
        SetWaiting(true);
        try
        {
            string res = await _agent.RenameChatSessionAsync(_currentModel, _currentSessionIndex, newName);
            AppendMessage("system", res);
            // Reload history silently to update names
            await ShowChatSessionsAsync(silent: true);
        }
        catch (Exception ex)
        {
            AppendMessage("error", "Failed to rename session: " + ex.Message);
        }
        finally
        {
            SetWaiting(false);
        }
    }

    private async Task DeleteActiveSessionAsync()
    {
        if (_agent == null) return;
        if (_currentSessionIndex == -1)
        {
            AppendMessage("error", "No active session loaded to delete.");
            return;
        }
        AppendMessage("system", $"Deleting active session [{_currentSessionIndex}]...");
        SetWaiting(true);
        try
        {
            string res = await _agent.DeleteChatSessionAsync(_currentModel, _currentSessionIndex);
            AppendMessage("system", res);
            // Clear output, reset index, and reload history
            _output.Clear();
            _currentSessionIndex = -1;
            _totalSessionsCount = 0;
            await ShowChatSessionsAsync(silent: true);
        }
        catch (Exception ex)
        {
            AppendMessage("error", "Failed to delete session: " + ex.Message);
        }
        finally
        {
            SetWaiting(false);
        }
    }

    private void UpdateSessionUi()
    {
        if (_currentSessionIndex == -1 || _totalSessionsCount == 0)
        {
            _sessionIndexLbl.Text = "[-]";
            _prevSessionBtn.Disabled = true;
            _nextSessionBtn.Disabled = true;
        }
        else
        {
            _sessionIndexLbl.Text = $"[{_currentSessionIndex}/{_totalSessionsCount - 1}]";
            _prevSessionBtn.Disabled = (_currentSessionIndex <= 0);
            _nextSessionBtn.Disabled = (_currentSessionIndex >= _totalSessionsCount - 1);
        }
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