#if TOOLS
using Godot;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace GodotMCP;

public class ChatResult
{
    public string Reply { get; set; } = "";
    public List<string> ToolsUsed { get; set; } = new();
}

public class GroqAgent
{
    // ── TO MIGRATE TO ANOTHER PROVIDER: change these 3 lines only ─────────
    private const string BaseUrl    = "https://api.groq.com/openai/v1/chat/completions";
    private const string Model      = "llama-3.3-70b-versatile";
    private const string AuthHeader = "Authorization"; // "x-api-key" for Anthropic direct
    // ───────────────────────────────────────────────────────────────────────

    private readonly System.Net.Http.HttpClient _http;
    private readonly GodotTools _tools;
    private readonly List<ApiMessage> _history = new();

    private const string SystemPrompt =
        """
        You are an AI assistant embedded inside the Godot 4 game engine editor.
        You can inspect and control the editor through tools.

        Rules:
        - When asked to do something in the editor, use tools — don't just describe how.
        - After using tools, briefly confirm what you did.
        - Be concise. The user reads your reply in a small dock panel.
        - If a tool returns an error, explain it clearly and suggest a fix.
        - You can chain multiple tool calls to complete a task.
        """;

    public GroqAgent(GodotTools tools)
    {
        _tools = tools;

        string apiKey = System.Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";

        if (string.IsNullOrEmpty(apiKey))
        {
            // Try loading from project root .env
            DotNetEnv.Env.TraversePath().Load();
            apiKey = System.Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "";
        }

        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(
                "GROQ_API_KEY not set.\n" +
                "Create a file called .env in your Godot project root with:\n" +
                "GROQ_API_KEY=your_key_here\n" +
                "Get a free key at https://console.groq.com/keys");

        _http = new System.Net.Http.HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.DefaultRequestHeaders.Add(AuthHeader, $"Bearer {apiKey}");
        _history.Add(new ApiMessage { Role = "system", Content = SystemPrompt });
    }

    public async Task<ChatResult> ChatAsync(string userMessage)
    {
        _history.Add(new ApiMessage { Role = "user", Content = userMessage });

        var toolsUsed = new List<string>();
        int maxLoops = 6; // max tool call iterations before giving up

        for (int i = 0; i < maxLoops; i++)
        {
            var response = await CallApi();
            var choice = response.Choices[0];
            var msg = choice.Message;

            // No tool calls = final answer
            if (msg.ToolCalls == null || msg.ToolCalls.Count == 0)
            {
                string reply = msg.Content ?? "";
                _history.Add(new ApiMessage { Role = "assistant", Content = reply });
                return new ChatResult { Reply = reply, ToolsUsed = toolsUsed };
            }

            // Add assistant message with tool calls to history
            _history.Add(new ApiMessage
            {
                Role = "assistant",
                Content = msg.Content,
                ToolCalls = msg.ToolCalls
            });

            // Execute each tool call and add results to history
            foreach (var tc in msg.ToolCalls)
            {
                toolsUsed.Add(tc.Function.Name);
                GD.Print($"[GodotMCP] Tool call: {tc.Function.Name}({tc.Function.Arguments})");

                string result = await _tools.ExecuteAsync(tc.Function.Name, tc.Function.Arguments);
                GD.Print($"[GodotMCP] Tool result: {result[..Math.Min(150, result.Length)]}");

                _history.Add(new ApiMessage
                {
                    Role = "tool",
                    Content = result,
                    ToolCallId = tc.Id
                });
            }
            // Loop: send results back to model for next response
        }

        return new ChatResult
        {
            Reply = "Hit the tool call limit. Try a simpler request.",
            ToolsUsed = toolsUsed
        };
    }

    private async Task<ApiResponse> CallApi()
    {
        var body = new
        {
            model = Model,
            messages = _history.Select(m => m.ToApiObject()).ToList(),
            tools = _tools.GetToolDefinitions(),
            tool_choice = "auto",
            max_tokens = 1024,
            temperature = 0.3
        };

        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(body, opts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var res = await _http.PostAsync(BaseUrl, content);
        var text = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new Exception($"API error {res.StatusCode}: {text}");

        GD.Print("[GodotMCP] Raw API response: " + text);

        return JsonSerializer.Deserialize<ApiResponse>(text,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            })
            ?? throw new Exception("Failed to deserialize API response");
    }
}

// ── API data models ────────────────────────────────────────────────────────

public class ApiResponse
{
    public List<ApiChoice> Choices { get; set; } = new();
}

public class ApiChoice
{
    public ApiMessage Message { get; set; } = new();
}

public class ApiMessage
{
    public string Role { get; set; } = "";
    public string? Content { get; set; }
    public List<ApiToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }

    public object ToApiObject()
    {
        if (Role == "tool")
            return new { role = "tool", content = Content, tool_call_id = ToolCallId };
        if (ToolCalls?.Count > 0)
            return new { role = Role, content = Content, tool_calls = ToolCalls };
        return new { role = Role, content = Content };
    }
}

public class ApiToolCall
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "function";
    public ApiToolFunction Function { get; set; } = new();
}

public class ApiToolFunction
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
}
#endif
