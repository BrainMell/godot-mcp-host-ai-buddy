#if TOOLS
using System;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Playwright;

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
public class ChatService
{
    // -- Playwright state (one browser shared across all AI sessions) --------
    private IPlaywright _playwright = null!;  // set in InitializePlaywrightAsync
    private IBrowserContext _context = null!; // set in InitializePlaywrightAsync
    private IPage _page = null!;              // set in InitializePlaywrightAsync

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

            _context = await _playwright.Chromium.LaunchPersistentContextAsync(
                _profilePath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = requestHeadless,
                    // Hide the automation banner so sites don't block us
                    IgnoreDefaultArgs = new[] { "--enable-automation" },
                    Args = new[]
                    {
                        "--disable-blink-features=AutomationControlled",
                        "--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
                    },
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

    // -----------------------------------------------------------------------
    // Private automation providers — each one handles a specific AI site.
    // They all return a string: either the AI's response or an error message.
    // -----------------------------------------------------------------------

    private async Task<string> SendMessageToChatGPTAsync(string message)
    {
        await _page.GotoAsync(_ChatGPT);

        // TODO: add ChatGPT selectors here (textbox, send button, response scraping)

        return "Success: Transmitted to ChatGPT.";
    }

    private async Task<string> SendMessageToGeminiAsync(string message)
    {
        await _page.GotoAsync(_Gemini);

        // Wait for page elements to load
        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

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

        // Click into the text area (Gemini requires this before typing)
        await _page.Locator("[data-test-id=\"textarea-inner\"]")
                   .GetByRole(AriaRole.Paragraph)
                   .ClickAsync();

        // Type the message into the input box
        var textBox = _page.GetByRole(AriaRole.Textbox, new() { Name = "Enter a prompt for Gemini" });
        await textBox.FillAsync(message);

        // Count existing response containers BEFORE sending so we can
        // identify which one is new after the message is sent
        var responseCount = await _page.Locator(".container").CountAsync();

        // Click the send button
        var textButton = _page.GetByRole(AriaRole.Button, new() { Name = "Send message" });
        await textButton.ClickAsync();

        // Wait for the new response container to appear in the DOM
        var latestResponseLocator = _page.Locator(".container").Nth(responseCount);
        await latestResponseLocator.WaitForAsync(new() { State = WaitForSelectorState.Attached });

        // Poll until the response text stabilizes (Gemini streams its output)
        string containerText = "";
        string previousText  = "";
        int stabilityCounter = 0;

        while (true)
        {
            await Task.Delay(500);

            containerText = await latestResponseLocator.TextContentAsync() ?? "";
            containerText = containerText.Trim();

            if (!string.IsNullOrEmpty(containerText) && containerText == previousText)
            {
                stabilityCounter++;
                if (stabilityCounter >= 2) break; // stable for 1 full second — done
            }
            else
            {
                previousText     = containerText;
                stabilityCounter = 0; // still changing — keep waiting
            }
        }

        return containerText;
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
    // -----------------------------------------------------------------------
    public async Task<string> SendMessageAsync(string message, bool needsBrowser, string model)
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
        // Temporarily forced to false (headed mode) for debugging
        bool runHeadless = false;

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
            case "gemini":  return await SendMessageToGeminiAsync(message);
            case "zai":     return await SendMessageToZaiAsync(message);
            default:
                throw new ArgumentException($"Unknown model: '{model}'. Use \"gemini\", \"chatgpt\", or \"zai\".");
        }
    }
}
#endif
