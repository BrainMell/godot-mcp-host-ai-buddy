#if TOOLS
using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Playwright;
using System.Runtime.Loader;
using System.Collections.Generic;
using Godot;

namespace GodotMCP;

// ---------------------------------------------------------------------------
// ChatService — the single entry point for all AI browser automation
//
// Responsibilities:
//   1. Launch and manage the Chromium browser (one shared instance)
//   2. Check login state, redirect to Google sign-in if needed
//   3. Route messages to the correct AI based on the "model" parameter
//   4. Return the AI's response as a plain string
//
// Usage:
//   var service = new ChatService();
//   string reply = await service.SendMessageAsync("hello", false, "gemini");
// ---------------------------------------------------------------------------
public class ChatService : IDisposable
{
    private static readonly List<ChatService> _activeInstances = new List<ChatService>();

    static ChatService()
    {
        var alc = AssemblyLoadContext.GetLoadContext(typeof(ChatService).Assembly);
        if (alc != null)
        {
            alc.Unloading += OnAssemblyUnloading;
        }
    }

    private static void OnAssemblyUnloading(AssemblyLoadContext context)
    {
        GD.Print("[GodotMCP] Assembly unloading. Disposing active Playwright browser sessions...");
        List<ChatService> toDispose;
        lock (_activeInstances)
        {
            toDispose = new List<ChatService>(_activeInstances);
            _activeInstances.Clear();
        }
        foreach (var instance in toDispose)
        {
            try { instance.Dispose(); } catch { }
        }
        // CRITICAL: Playwright's Dispose() kills the Node process, but the stdout/stderr
        // reader threads take a moment to unwind. If we return from this event while those
        // threads are still alive, Godot will abort the assembly unload. Give them time.
        System.Threading.Thread.Sleep(500);
    }

    public ChatService()
    {
        lock (_activeInstances)
        {
            _activeInstances.Add(this);
        }
    }

    // -- Playwright state (one browser shared across all AI sessions) --------
    private IPlaywright _playwright = null!;  // set in InitializePlaywrightAsync
    private IBrowserContext _context = null!; // set in InitializePlaywrightAsync
    private IPage _page = null!;              // set in InitializePlaywrightAsync

    // CancellationTokenSource — cancelled on Dispose so all in-flight awaits
    // unblock immediately. This is what actually lets Godot unload the assembly.
    private CancellationTokenSource _cts = new CancellationTokenSource();

    // The folder on disk where the browser saves cookies and login state.
    // Because this is a persistent context, the user only logs in once.
    private string _profilePath = "PlaywrightProfile";

    // Tracks whether the browser is currently headless or not.
    // If the caller switches modes, we tear down and restart the browser.
    private bool? _stateOfBrowser = null;

    // -- AI URLs -------------------------------------------------------------
    private string _ChatGPT = "https://chat.openai.com/chat";
    private string _Gemini  = "https://gemini.google.com/app";
    private string _Zai     = "https://chat.z.ai/c/";

    // -----------------------------------------------------------------------
    // InitializePlaywrightAsync — launches Chromium (or restarts it if the
    // headless mode changed since last call)
    //
    // requestHeadless: true = invisible browser, false = visible browser window
    // -----------------------------------------------------------------------
    public async Task InitializePlaywrightAsync(bool requestHeadless)
    {
        // If the browser is already running but headless mode changed, tear it down
        if (_playwright != null && _stateOfBrowser != requestHeadless)
        {
            await _context.CloseAsync();
            _playwright.Dispose();

            _playwright = null!;
            _context    = null!;
            _page       = null!;
        }

        // Only launch if there's no browser running yet
        if (_playwright == null)
        {
            _playwright = await Playwright.CreateAsync();

            var chromeArgs = new System.Collections.Generic.List<string>
            {
                "--disable-blink-features=AutomationControlled",
                "--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
            };

            if (requestHeadless)
            {
                chromeArgs.Add("--headless=new");
            }

            _context = await _playwright.Chromium.LaunchPersistentContextAsync(
                _profilePath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = requestHeadless,
                    // Set a standard viewport size to ensure layouts render correctly
                    ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
                    // Hide the automation banner so sites don't block us
                    IgnoreDefaultArgs = new[] { "--enable-automation" },
                    Args = chromeArgs.ToArray()
                }
            );

            await _context.GrantPermissionsAsync(new[]
            {
                "clipboard-read",
                "clipboard-write",
                "storage-access"
            });

            // Reuse the tab that the persistent context opens automatically
            _page = _context.Pages[0];
            _stateOfBrowser = requestHeadless;
        }
    }

    // -----------------------------------------------------------------------
    // CheckIfLoggedInAsync — checks whether the profile folder has a saved
    // session. If it's brand new (empty folder), we redirect to Google login
    // and return a message telling the user to sign in and restart.
    //
    // isBrandNew: true if the profile folder was empty when SendMessageAsync ran
    // -----------------------------------------------------------------------
    private async Task<string> CheckIfLoggedInAsync(bool isBrandNew)
    {
        if (isBrandNew)
        {
            // Navigate to Google login so the user can sign in manually.
            // After signing in the profile folder will save the session,
            // so this branch will never run again.
            await _page.GotoAsync("https://accounts.google.com/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 120000
            });

            return "LoginRequired: Please log into your Google account in the opened window, then restart.";
        }

        return "Authenticated";
    }
    public async Task<string> CheckChatHistoryAsync(string model)
    {
        string url = "";
        switch (model.ToLower())
        {
            case "chatgpt": url = _ChatGPT; break;
            case "gemini":  url = _Gemini; break;
            case "zai":     url = _Zai; break;
            default:        url = _Gemini; break;
        }

        string fullPath = Path.GetFullPath(_profilePath);
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }
        bool isBrandNew = Directory.GetFileSystemEntries(fullPath).Length == 0;
        bool runHeadless = !isBrandNew;
        await InitializePlaywrightAsync(runHeadless);

        string loginStatus = await CheckIfLoggedInAsync(isBrandNew);
        if (loginStatus.StartsWith("LoginRequired"))
        {
            return loginStatus;
        }

        if (!_page.Url.StartsWith(url))
        {
            await _page.GotoAsync(url);
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }

        // Ensure the sidebar is opened
        try
        {
            await _page.Locator("[data-test-id=\"all-conversations\"]").WaitForAsync(new() 
            { 
                State = WaitForSelectorState.Visible, 
                Timeout = 3000 
            });
        }
        catch (TimeoutException)
        {
            var menuBtn = _page.Locator("[data-test-id=\"side-nav-menu-button\"]");
            if (await menuBtn.CountAsync() > 0 && await menuBtn.IsVisibleAsync())
            {
                await menuBtn.ClickAsync();
                try
                {
                    await _page.Locator("[data-test-id=\"all-conversations\"]").WaitForAsync(new() 
                    { 
                        State = WaitForSelectorState.Visible, 
                        Timeout = 3000 
                    });
                }
                catch { }
            }
        }

        var chatLogs = await _page.Locator("[data-test-id='all-conversations'] a").AllAsync();
        if (chatLogs.Count == 0)
        {
            return "No recent chat sessions found.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Recent Chat Sessions:");
        for (int i = 0; i < chatLogs.Count; i++)
        {
            var log = chatLogs[i];
            var logText = await log.InnerTextAsync();
            logText = logText.Replace("\r", "").Replace("\n", " ").Trim();
            sb.AppendLine($"[{i}] {logText}");
        }

        return sb.ToString().Trim();
    }       

    // Navigates to a specific chat session by index in the history sidebar
    public async Task<string> GetChatHistoryCountAsync(string model, int sessionIndex)
    {
        string url = "";
        switch (model.ToLower())
        {
            case "chatgpt": url = _ChatGPT; break;
            case "gemini":  url = _Gemini; break;
            case "zai":     url = _Zai; break;
            default:        url = _Gemini; break;
        }

        string fullPath = Path.GetFullPath(_profilePath);
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }
        bool isBrandNew = Directory.GetFileSystemEntries(fullPath).Length == 0;
        bool runHeadless = !isBrandNew;
        await InitializePlaywrightAsync(runHeadless);

        string loginStatus = await CheckIfLoggedInAsync(isBrandNew);
        if (loginStatus.StartsWith("LoginRequired"))
        {
            return loginStatus;
        }

        if (!_page.Url.StartsWith(url))
        {
            await _page.GotoAsync(url);
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }

        // Ensure the sidebar is opened
        try
        {
            await _page.Locator("[data-test-id=\"all-conversations\"]").WaitForAsync(new() 
            { 
                State = WaitForSelectorState.Visible, 
                Timeout = 3000 
            });
        }
        catch (TimeoutException)
        {
            var menuBtn = _page.Locator("[data-test-id=\"side-nav-menu-button\"]");
            if (await menuBtn.CountAsync() > 0 && await menuBtn.IsVisibleAsync())
            {
                await menuBtn.ClickAsync();
                try
                {
                    await _page.Locator("[data-test-id=\"all-conversations\"]").WaitForAsync(new() 
                    { 
                        State = WaitForSelectorState.Visible, 
                        Timeout = 3000 
                    });
                }
                catch { }
            }
        }

        var chatLogs = await _page.Locator("[data-test-id='all-conversations'] a").AllAsync();
        if (chatLogs.Count == 0)
        {
            return "Error: No chat sessions found to navigate.";
        }

        if (sessionIndex < 0 || sessionIndex >= chatLogs.Count)
        {
            return $"Error: Session index {sessionIndex} is out of bounds (0 to {chatLogs.Count - 1}).";
        }

        var targetLog = chatLogs[sessionIndex];
        var sessionTitle = await targetLog.InnerTextAsync();
        sessionTitle = sessionTitle.Replace("\r", "").Replace("\n", " ").Trim();

        // Click the target chat session item in the sidebar
        await targetLog.ClickAsync();

        // Allow page time to load dynamic message contents for that chat
        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Task.Delay(1000); 

        return $"Switched to session [{sessionIndex}]: {sessionTitle}";
    }

    // Scrapes the visible messages (both user queries and model responses) of the currently active chat session
    public async Task<string> GetChatHistoryMessagesAsync(string model)
    {
        // Query elements matching query text content or response content
        var elements = await _page.Locator(".query-text, [class*='query-text'], query-content, [class*='query-content'], message-content, [class*='message-content']").AllAsync();
        var sb = new System.Text.StringBuilder();
        foreach (var el in elements)
        {
            string className = await el.GetAttributeAsync("class") ?? "";
            string tagName = await el.EvaluateAsync<string>("el => el.tagName") ?? "";
            string text = await el.InnerTextAsync();
            text = text.Trim();
            if (string.IsNullOrEmpty(text)) continue;

            if (className.Contains("query-") || tagName.Contains("QUERY"))
            {
                sb.AppendLine($"[ROLE:USER]{text}");
            }
            else
            {
                sb.AppendLine($"[ROLE:AI]{text}");
            }
        }
        return sb.ToString().Trim();
    }

    // Renames a specific chat session in the sidebar
    public async Task<string> RenameChatSessionAsync(string model, int sessionIndex, string newName)
    {
        var chatLogs = await _page.Locator("[data-test-id='all-conversations'] a").AllAsync();
        if (sessionIndex < 0 || sessionIndex >= chatLogs.Count)
        {
            return $"Error: Session index {sessionIndex} out of bounds.";
        }
        var targetLog = chatLogs[sessionIndex];
        
        var actionsBtn = targetLog.Locator("..").Locator("[data-test-id=\"actions-menu-button\"]");
        if (await actionsBtn.CountAsync() == 0)
        {
            actionsBtn = targetLog.Locator("..").GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^More options for") });
        }
        await actionsBtn.ClickAsync();
        
        await _page.Locator("[data-test-id=\"rename-button\"]").ClickAsync();
        await _page.Locator("[data-test-id=\"edit-title-input\"]").FillAsync(newName);
        await _page.Locator("[data-test-id=\"save-button\"]").ClickAsync();
        
        return $"Renamed session [{sessionIndex}] to: {newName}";
    }

    // Deletes a specific chat session in the sidebar
    public async Task<string> DeleteChatSessionAsync(string model, int sessionIndex)
    {
        var chatLogs = await _page.Locator("[data-test-id='all-conversations'] a").AllAsync();
        if (sessionIndex < 0 || sessionIndex >= chatLogs.Count)
        {
            return $"Error: Session index {sessionIndex} out of bounds.";
        }
        var targetLog = chatLogs[sessionIndex];
        
        var actionsBtn = targetLog.Locator("..").Locator("[data-test-id=\"actions-menu-button\"]");
        if (await actionsBtn.CountAsync() == 0)
        {
            actionsBtn = targetLog.Locator("..").GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^More options for") });
        }
        await actionsBtn.ClickAsync();
        
        await _page.Locator("[data-test-id=\"delete-button\"]").ClickAsync();
        await _page.Locator("[data-test-id=\"confirm-button\"]").ClickAsync();
        
        return $"Deleted session [{sessionIndex}].";
    }

    // -----------------------------------------------------------------------
    // Private automation providers — each one handles a specific AI site.
    // They all return a string: either the AI's response or an error message.
    // -----------------------------------------------------------------------

    private async Task<string> SendMessageToChatGPTAsync(string message)
    {
        await _page.GotoAsync(_ChatGPT);

        bool keepsession = true; // ChatGPT doesn't have a "keep session" option, so we always keep it

        if (!keepsession || !_page.Url.StartsWith("https://chat.openai.com/chat"))
        {
            await _page.GotoAsync(_ChatGPT);
            // Wait for page elements to load
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }

        bool isSignedOut = await _page.Locator("text=Log in").CountAsync() > 0;

        return "Success: Transmitted to ChatGPT.";
    }

    private async Task<string> SendMessageToGeminiAsync(string message, bool keepSession = false)
    {
        if (!keepSession || !_page.Url.StartsWith("https://gemini.google.com/app"))
        {
            await _page.GotoAsync(_Gemini);
            // Wait for page elements to load
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }

        // Check if we are signed out (if either of the Sign In buttons are present)
        bool isSignedOut = await _page.Locator("[data-test-id=\"sign-in-button\"]").CountAsync() > 0
                        || await _page.Locator("[data-test-id=\"mavatar-sign-in-icon-button\"]").CountAsync() > 0;

        if (isSignedOut)
        {
            await _context.CloseAsync();
            _playwright.Dispose();

            _playwright = null!;
            _context    = null!;
            _page       = null!;
            _stateOfBrowser = null;

            string fullPath = Path.GetFullPath(_profilePath);
            if (Directory.Exists(fullPath))
            {
                try
                {
                    foreach (string file in Directory.GetFiles(fullPath)) File.Delete(file);
                    foreach (string subDir in Directory.GetDirectories(fullPath)) Directory.Delete(subDir, true);
                }
                catch { /* ignore lock errors */ }
            }

            return "LoginRequired: Session expired or not signed in. Please log in again in the opened browser window and restart.";
        }

        if (string.IsNullOrEmpty(message))
        {
            return "Error: Message cannot be empty for Gemini.";
        }

        // Locate the prompt text box (try several semantic options to ensure stability)
        ILocator textBox = _page.GetByRole(AriaRole.Textbox, new() { Name = "Prompt", Exact = false });
        if (await textBox.CountAsync() == 0)
            textBox = _page.GetByRole(AriaRole.Textbox, new() { Name = "Message", Exact = false });
        if (await textBox.CountAsync() == 0)
            textBox = _page.GetByRole(AriaRole.Textbox, new() { Name = "Enter a prompt", Exact = false });
        if (await textBox.CountAsync() == 0)
            textBox = _page.GetByRole(AriaRole.Textbox);

        await textBox.First.ClickAsync();
        await textBox.First.FillAsync(message);

        // Count existing response containers BEFORE sending so we can
        // identify which one is new after the message is sent
        var responseLocator = _page.Locator("message-content");
        bool useMessageContent = await responseLocator.CountAsync() > 0;
        if (!useMessageContent)
        {
            responseLocator = _page.Locator(".container"); // fallback to class only if message-content tag is missing
        }
        var responseCount = await responseLocator.CountAsync();

        // Locate the send button
        ILocator textButton = _page.GetByRole(AriaRole.Button, new() { Name = "Send message", Exact = false });
        if (await textButton.CountAsync() == 0)
            textButton = _page.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = false });
        if (await textButton.CountAsync() == 0)
            textButton = _page.Locator("button[aria-label*='Send' i]");

        await textButton.First.ClickAsync();

        // Wait for the new response container to appear in the DOM
        var latestResponseLocator = responseLocator.Nth(responseCount);
        await latestResponseLocator.WaitForAsync(new() { State = WaitForSelectorState.Attached });

        // Explicitly wait for some initial text to start streaming to avoid premature empty reads
        string containerText = "";
        var startDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < startDeadline && !_cts.Token.IsCancellationRequested)
        {
            containerText = await latestResponseLocator.TextContentAsync() ?? "";
            if (!string.IsNullOrEmpty(containerText.Trim()))
            {
                break;
            }
            try
            {
                await Task.Delay(200, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Poll until the response text stabilizes (Gemini streams its output).
        // Hard timeout of 90 seconds to prevent hanging forever.
        // Also respects _cts so Dispose() unblocks this loop immediately.
        string previousText  = "";
        int stabilityCounter = 0;
        var deadline = DateTime.UtcNow.AddSeconds(90);

        while (DateTime.UtcNow < deadline && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break; // Dispose was called — exit immediately
            }

            containerText = await latestResponseLocator.TextContentAsync() ?? "";
            containerText = containerText.Trim();

            if (!string.IsNullOrEmpty(containerText) && containerText == previousText)
            {
                stabilityCounter++;
                if (stabilityCounter >= 3) break; // stable for 1.5 seconds — done
            }
            else
            {
                previousText     = containerText;
                stabilityCounter = 0; // still changing — keep waiting
            }
        }

        if (string.IsNullOrEmpty(containerText))
        {
            return "Error: Gemini did not respond within 90 seconds. Please try again.";
        }

        return containerText;
    }

    public bool IsSessionHealthy()
    {
        if (_page == null) return false;
        var url = _page.Url ?? "";
        return url.Contains("gemini.google.com") && !url.Contains("signin");
    }

    private async Task<string> SendMessageToZaiAsync(string message)
    {
        await _page.GotoAsync(_Zai);

        // TODO: add Z.ai selectors here (textbox, send button, response scraping)

        return "Success: Transmitted to Zai.";
    }

    // -----------------------------------------------------------------------
    // SendMessageAsync — the main public entry point
    //
    // This is the only method ChatDock.cs calls. It:
    //   1. Makes sure the profile folder exists
    //   2. Launches the browser (or reuses it)
    //   3. Checks login state
    //   4. Routes the message to the right AI based on `model`
    //   5. Returns the AI's response as a string
    //
    // message:      the text to send to the AI
    // needsBrowser: false = reuse existing browser | true = headless mode
    // model:        which AI to use — "gemini", "chatgpt", or "zai"
    // keepSession:  if true, tries to keep the existing session/url for Gemini
    // -----------------------------------------------------------------------
    public async Task<string> SendMessageAsync(string message, bool needsBrowser, string model, bool keepSession = false)
    {
        // Make sure the profile folder exists on disk
        string fullPath = Path.GetFullPath(_profilePath);
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }

        // A brand-new empty folder means there's no saved session yet
        bool isBrandNew = Directory.GetFileSystemEntries(fullPath).Length == 0;

        // Determine headless mode dynamically:
        // - If brand new profile, we MUST run headed (headless = false) so the user can sign in.
        // - If not brand new, we run headless (headless = true) to keep the browser invisible.
        bool runHeadless = !isBrandNew;

        // Launch (or reuse) the browser
        await InitializePlaywrightAsync(runHeadless);

        // Check if the user is logged in — bail early if not
        string loginStatus = await CheckIfLoggedInAsync(isBrandNew);
        if (loginStatus.StartsWith("LoginRequired"))
        {
            return loginStatus;
        }

        // Route to the correct AI
        switch (model.ToLower())
        {
            case "chatgpt": return await SendMessageToChatGPTAsync(message);
            case "gemini":  return await SendMessageToGeminiAsync(message, keepSession);
            case "zai":     return await SendMessageToZaiAsync(message);
            default:
                throw new ArgumentException($"Unknown model: '{model}'. Use \"gemini\", \"chatgpt\", or \"zai\".");
        }
    }

    public void Dispose()
    {
        // Step 1: Signal cancellation FIRST so all active loops wake up
        try { _cts.Cancel(); } catch { }

        // Step 2: Dispose Playwright (instantly terminates connection and kills node/browser subprocesses)
        try { _playwright?.Dispose(); } catch { }

        // Step 3: Dispose the CancellationTokenSource itself
        try { _cts.Dispose(); } catch { }

        lock (_activeInstances)
        {
            _activeInstances.Remove(this);
        }

        _playwright = null!;
        _context    = null!;
        _page       = null!;
        _stateOfBrowser = null;
    }
}
#endif
