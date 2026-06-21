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

// ---------------------------------------------------------------------------
// ChatResult — what gets returned to the chat dock after a conversation turn
// ---------------------------------------------------------------------------
public class ChatResult
{
    public string Reply { get; set; } = "";
    public List<string> ToolsUsed { get; set; } = new List<string>();
}

// ---------------------------------------------------------------------------
// GroqAgent — the orchestrator that talks to the Groq API
//
// Responsibilities:
//   1. Keep a conversation history (user messages, assistant replies, tool results)
//   2. Send messages + tool definitions to the Groq API
//   3. If the API responds with tool_calls, execute them via GodotTools
//   4. Send tool results back to the API and loop until a final text reply
// ---------------------------------------------------------------------------
public class GroqAgent
{
    // -- Provider config (change these 3 lines to use a different LLM) -------
    private const string BaseUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string Model = "llama-3.3-70b-versatile";
    private const string AuthHeader = "Authorization"; // Use "x-api-key" for Anthropic
    // -----------------------------------------------------------------------

    // The HTTP client used to talk to the Groq API
    private HttpClient _http;

    // GodotTools knows the tool schemas and how to execute them
    private GodotTools _tools;

    // Every message (user, assistant, tool result) gets stored here so the
    // API has full conversation context on every call
    private List<ApiMessage> _history;

    // This is the system prompt — it tells the AI how to behave.
    // It gets inserted as the very first message in the history.
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

    // -----------------------------------------------------------------------
    // Constructor — runs once when the plugin loads
    // -----------------------------------------------------------------------
    public GroqAgent(GodotTools tools)
    {
        _tools = tools;
        _history = new List<ApiMessage>();

        // Try to find the API key from the environment
        string apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (apiKey == null)
        {
            apiKey = "";
        }

        // If not found in env, try loading a .env file from the project root
        if (apiKey == "")
        {
            DotNetEnv.Env.TraversePath().Load();
            apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
            if (apiKey == null)
            {
                apiKey = "";
            }
        }

        // If still empty, we can't do anything — throw an error
        if (apiKey == "")
        {
            throw new InvalidOperationException(
                "GROQ_API_KEY not set.\n" +
                "Create a file called .env in your Godot project root with:\n" +
                "GROQ_API_KEY=your_key_here\n" +
                "Get a free key at https://console.groq.com/keys");
        }

        // Set up the HTTP client with the API key in the header
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.DefaultRequestHeaders.Add(AuthHeader, "Bearer " + apiKey);

        // Seed the history with the system prompt so the AI knows its role
        ApiMessage systemMessage = new ApiMessage();
        systemMessage.Role = "system";
        systemMessage.Content = SystemPrompt;
        _history.Add(systemMessage);
    }

    // -----------------------------------------------------------------------
    // ChatAsync — the main entry point, called when the user sends a message
    //
    // This is an "async" method, meaning it returns a Task (a promise that
    // will eventually have a ChatResult). The "await" keyword is used to wait
    // for the API response without freezing the editor.
    // -----------------------------------------------------------------------
    public async Task<ChatResult> ChatAsync(string userMessage)
    {
        // Add the user's message to the history
        ApiMessage userMsg = new ApiMessage();
        userMsg.Role = "user";
        userMsg.Content = userMessage;
        _history.Add(userMsg);

        // Track which tools were used (for display in the chat dock)
        List<string> toolsUsed = new List<string>();

        // The max number of tool-call loops before we give up
        // (prevents infinite loops if the AI keeps calling tools)
        int maxLoops = 6;

        for (int i = 0; i < maxLoops; i++)
        {
            // Send everything to the Groq API and get a response
            ApiResponse response = await CallApi();

            // The API returns an array of "choices" — we just take the first one
            ApiChoice choice = response.Choices[0];
            ApiMessage msg = choice.Message;

            // Check if the response has any tool calls
            bool hasToolCalls = msg.ToolCalls != null && msg.ToolCalls.Count > 0;

            // If no tool calls, the AI gave us a final text answer — we're done
            if (!hasToolCalls)
            {
                string reply = msg.Content;
                if (reply == null)
                {
                    reply = "";
                }

                // Add the final reply to history
                ApiMessage assistantMsg = new ApiMessage();
                assistantMsg.Role = "assistant";
                assistantMsg.Content = reply;
                _history.Add(assistantMsg);

                // Return the result to the chat dock
                ChatResult result = new ChatResult();
                result.Reply = reply;
                result.ToolsUsed = toolsUsed;
                return result;
            }

            // -- The AI wants to call tools. Add the assistant message (with
            //    tool calls) to history so the API remembers what it asked for.
            ApiMessage assistantWithTools = new ApiMessage();
            assistantWithTools.Role = "assistant";
            assistantWithTools.Content = msg.Content;
            assistantWithTools.ToolCalls = msg.ToolCalls;
            _history.Add(assistantWithTools);

            // -- Execute each tool call one by one
            for (int t = 0; t < msg.ToolCalls.Count; t++)
            {
                ApiToolCall tc = msg.ToolCalls[t];

                // Record which tool was called
                toolsUsed.Add(tc.Function.Name);

                string argsPreview = tc.Function.Arguments;
                if (argsPreview.Length > 150)
                {
                    argsPreview = argsPreview.Substring(0, 150);
                }
                GD.Print("[GodotMCP] Tool call: " + tc.Function.Name + "(" + argsPreview + ")");

                // Actually execute the tool (this calls the HTTP server in Godot)
                string toolResult = await _tools.ExecuteAsync(tc.Function.Name, tc.Function.Arguments);

                string resultPreview = toolResult;
                if (resultPreview.Length > 150)
                {
                    resultPreview = resultPreview.Substring(0, 150);
                }
                GD.Print("[GodotMCP] Tool result: " + resultPreview);

                // Add the tool result to history so the AI can see what happened
                ApiMessage toolMsg = new ApiMessage();
                toolMsg.Role = "tool";
                toolMsg.Content = toolResult;
                toolMsg.ToolCallId = tc.Id;
                _history.Add(toolMsg);
            }

            // Loop back to the top — send the tool results to the API so the
            // AI can decide what to do next (call more tools, or give a final answer)
        }

        // If we hit the loop limit, return an error message
        ChatResult limitResult = new ChatResult();
        limitResult.Reply = "Hit the tool call limit. Try a simpler request.";
        limitResult.ToolsUsed = toolsUsed;
        return limitResult;
    }

    // -----------------------------------------------------------------------
    // CallApi — builds the JSON request body and sends it to the Groq API
    //
    // The request body looks like:
    // {
    //   "model": "llama-3.3-70b-versatile",
    //   "messages": [ ... the full conversation history ... ],
    //   "tools": [ ... all tool definitions ... ],
    //   "tool_choice": "auto",
    //   "max_tokens": 1024,
    //   "temperature": 0.3
    // }
    // -----------------------------------------------------------------------
    private async Task<ApiResponse> CallApi()
    {
        // -- Build the messages array by converting each history entry to
        //    the format the API expects (using ToApiObject() on each)
        List<object> messages = new List<object>();
        for (int i = 0; i < _history.Count; i++)
        {
            ApiMessage m = _history[i];
            object apiObject = m.ToApiObject();
            messages.Add(apiObject);
        }

        // -- Get the tool definitions (names, descriptions, parameter schemas)
        List<object> tools = _tools.GetToolDefinitions();

        // -- Build the request body as an anonymous object
        //    (anonymous objects are convenient because they serialize to JSON
        //    with property names matching the variable names)
        var body = new
        {
            model = Model,
            messages = messages,
            tools = tools,
            tool_choice = "auto",
            max_tokens = 1024,
            temperature = 0.3
        };

        // -- Configure the JSON serializer
        //    PropertyNamingPolicy.SnakeCaseLower means:
        //      ToolCallId  →  tool_call_id  (in the JSON output)
        //    DefaultIgnoreCondition.WhenWritingNull means:
        //      skip any property that is null (sends less data)
        JsonSerializerOptions serializeOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // -- Serialize the body to a JSON string
        string json = JsonSerializer.Serialize(body, serializeOpts);

        // -- Send the HTTP POST request to the Groq API
        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage res = await _http.PostAsync(BaseUrl, content);
        string responseText = await res.Content.ReadAsStringAsync();

        // -- If the API returned an error, throw an exception
        if (!res.IsSuccessStatusCode)
        {
            throw new Exception("API error " + res.StatusCode + ": " + responseText);
        }

        GD.Print("[GodotMCP] Raw API response: " + responseText);

        // -- Deserialize the JSON response into our ApiResponse C# object
        JsonSerializerOptions deserializeOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true  // "tool_call_id" matches ToolCallId
        };

        ApiResponse apiResponse = JsonSerializer.Deserialize<ApiResponse>(responseText, deserializeOpts);
        if (apiResponse == null)
        {
            throw new Exception("Failed to deserialize API response");
        }

        return apiResponse;
    }
}

// ===========================================================================
// API data models — these represent the JSON structure of the Groq API
// ===========================================================================

// The top-level response from the API: { "choices": [ ... ] }
public class ApiResponse
{
    public List<ApiChoice> Choices { get; set; } = new List<ApiChoice>();
}

// Each choice in the response: { "message": { ... } }
public class ApiChoice
{
    public ApiMessage Message { get; set; } = new ApiMessage();
}

// A message in the conversation. Can be:
//   - "system"     — the initial system prompt
//   - "user"       — what the user typed
//   - "assistant"  — the AI's reply (may include tool_calls)
//   - "tool"       — the result of executing a tool
public class ApiMessage
{
    public string Role { get; set; } = "";
    // Content is nullable because:
    //   - Tool result messages don't always have content
    //   - The API sometimes omits it for assistant messages that only have tool_calls
    public string? Content { get; set; }

    // ToolCalls is nullable because only assistant messages have them
    public List<ApiToolCall>? ToolCalls { get; set; }

    // ToolCallId is nullable because only "tool" role messages have it
    public string? ToolCallId { get; set; }

    // Converts this message into the format the API expects.
    // Different roles need different fields in the JSON.
    public object ToApiObject()
    {
        // Tool result messages need a tool_call_id field
        if (Role == "tool")
        {
            return new
            {
                role = Role,
                content = Content,
                tool_call_id = ToolCallId
            };
        }

        // Assistant messages with tool calls need the tool_calls field
        if (ToolCalls != null && ToolCalls.Count > 0)
        {
            return new
            {
                role = Role,
                content = Content,
                tool_calls = ToolCalls
            };
        }

        // Regular messages (system, user, or plain assistant text)
        return new
        {
            role = Role,
            content = Content
        };
    }
}

// A tool call from the API response:
// {
//   "id": "call_abc123",
//   "type": "function",
//   "function": { "name": "create_node", "arguments": "{...}" }
// }
public class ApiToolCall
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "function";
    public ApiToolFunction Function { get; set; } = new ApiToolFunction();
}

// The function part of a tool call:
// { "name": "create_node", "arguments": "{\"node_type\":\"Sprite2D\"}" }
public class ApiToolFunction
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
}
#endif