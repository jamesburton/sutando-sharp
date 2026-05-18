namespace Sutando.Browser;

/// <summary>
/// Thin CLI shim that mirrors the <c>node src/browser.mjs &lt;url&gt; [action]...</c> invocation
/// shape from upstream. The future Sutando.Cli verb wires its arguments here; we intentionally
/// keep this surface tiny so the CLI integration stays a one-liner.
/// </summary>
public static class BrowserCommand
{
    /// <summary>
    /// Run the upstream-compatible CLI flow: navigate to <c>args[0]</c>, then either dump the
    /// page text (when no further args are supplied) or execute each colon-delimited action in
    /// turn, printing per-action results to <see cref="Console.Out"/>.
    /// </summary>
    /// <param name="args">First element is the URL; subsequent elements are <see cref="BrowserAction"/>-grammar strings.</param>
    /// <param name="options">Optional launch options; defaults are used when <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token observed across the run.</param>
    /// <returns>Process exit code — <c>0</c> on success, <c>1</c> on user error or browser failure.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        BrowserOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            await Console.Error.WriteLineAsync("Usage: sutando browser <url> [action]...").ConfigureAwait(false);
            return 1;
        }

        var url = args[0];
        var actionArgs = args.AsSpan(1).ToArray();
        var launchOptions = options ?? new BrowserOptions();

        await using var session = await BrowserSession.LaunchAsync(launchOptions, ct).ConfigureAwait(false);

        try
        {
            await session.ExecuteAsync(new BrowserAction.Navigate(url), ct).ConfigureAwait(false);

            if (actionArgs.Length == 0)
            {
                // Default behaviour mirrors upstream: dump truncated body text.
                var result = await session.ExecuteAsync(new BrowserAction.Text(), ct).ConfigureAwait(false);
                Console.WriteLine(Truncate(result.Text ?? string.Empty, 10_000));
                return 0;
            }

            foreach (var raw in actionArgs)
            {
                BrowserAction action;
                try
                {
                    action = BrowserAction.Parse(raw);
                }
                catch (FormatException ex)
                {
                    await Console.Error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
                    return 1;
                }

                var result = await session.ExecuteAsync(action, ct).ConfigureAwait(false);
                PrintResult(action, result);
            }

            return 0;
        }
        catch (Exception ex) when (ex is Microsoft.Playwright.PlaywrightException or TimeoutException or IOException)
        {
            await Console.Error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static void PrintResult(BrowserAction action, BrowserActionResult result)
    {
        switch (action)
        {
            case BrowserAction.Text:
                Console.WriteLine(Truncate(result.Text ?? string.Empty, 10_000));
                break;
            case BrowserAction.Html:
                Console.WriteLine(Truncate(result.Html ?? string.Empty, 20_000));
                break;
            case BrowserAction.Screenshot or BrowserAction.Pdf:
                Console.WriteLine(result.OutputPath);
                break;
            case BrowserAction.Click click:
                Console.WriteLine($"Clicked: {click.Selector}");
                break;
            case BrowserAction.Fill fill:
                Console.WriteLine($"Filled: {fill.Selector} = {fill.Value}");
                break;
            case BrowserAction.Select sel:
                Console.WriteLine($"Selected: {sel.Selector} = {sel.Value}");
                break;
            case BrowserAction.Wait wait:
                Console.WriteLine($"Waited: {(int)wait.Duration.TotalMilliseconds}ms");
                break;
            default:
                // Navigate has no console output in upstream; ignore.
                break;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
