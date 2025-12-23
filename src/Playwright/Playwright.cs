/*
 * MIT License
 *
 * Copyright (c) Microsoft Corporation.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright.Core;
using Microsoft.Playwright.Helpers;
using Microsoft.Playwright.Transport;

namespace Microsoft.Playwright;

[SuppressMessage("Microsoft.Design", "CA1724", Justification = "Playwright is the entrypoint for all languages.")]
public static class Playwright
{
    /// <summary>
    /// Launches Playwright.
    /// </summary>
    /// <returns>A <see cref="Task"/> that completes when the playwright driver is ready to be used.</returns>
    public static async Task<IPlaywright> CreateAsync()
    {
        var transport = new StdIOTransport();
        var connection = new Connection();
        transport.MessageReceived += (_, message) =>
        {
            Connection.TraceMessage("pw:channel:recv", message);
            connection.Dispatch(JsonSerializer.Deserialize<PlaywrightServerMessage>(message, JsonExtensions.DefaultJsonSerializerOptions)!);
        };
        transport.LogReceived += (_, log) =>
        {
            // workaround for https://github.com/nunit/nunit/issues/4144
            var writer = Environment.GetEnvironmentVariable("PWAPI_TO_STDOUT") != null ? Console.Out : Console.Error;
            writer.WriteLine(log);
        };
        transport.TransportClosed += (_, reason) => connection.DoClose(reason);
        connection.OnMessage = (message, keepNulls) =>
        {
            var rawMessage = JsonSerializer.SerializeToUtf8Bytes(message, keepNulls ? connection.DefaultJsonSerializerOptionsKeepNulls : connection.DefaultJsonSerializerOptions);
            Connection.TraceMessage("pw:channel:send", rawMessage);
            return transport.SendAsync(rawMessage);
        };
        connection.Close += (_, reason) => transport.Close(reason);
        return await connection.InitializePlaywrightAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to a browser on a remote Playwright instance.
    /// In contrast to <see cref="Core.BrowserType.ConnectAsync"/>, this doesn't require a node process and uses a managed WebSocket implementation instead.
    /// </summary>
    /// <param name="wsEndpoint">The WebSocket endpoint to connect to.</param>
    /// <param name="browserName">The name of the browser.</param>
    /// <param name="options">Optional connection options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The browser.</returns>
    public static async Task<IBrowser> ConnectAsync(string wsEndpoint, string browserName, BrowserTypeConnectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ClientWebSocket webSocket = new();
        webSocket.Options.SetRequestHeader("x-playwright-browser", browserName);

        // Needed for server-side version compatibility check. The server will return HTTP 428 on mismatch.
        Version playwrightVersion = Assembly.GetExecutingAssembly().GetName().Version;
        webSocket.Options.SetRequestHeader("user-agent", $"Playwright/{playwrightVersion.Major}.{playwrightVersion.Minor}.{playwrightVersion.Build}");

        if (options?.Headers != null)
        {
            foreach (var header in options.Headers)
            {
                webSocket.Options.SetRequestHeader(header.Key, header.Value);
            }
        }

        if (options != null)
        {
            webSocket.Options.SetRequestHeader("x-playwright-launch-options", JsonSerializer.Serialize(options, JsonExtensions.DefaultJsonSerializerOptions));
        }

        try
        {
            await webSocket.ConnectAsync(new Uri(wsEndpoint), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            webSocket.Dispose();
            throw;
        }

        var transport = new WebSocketTransport(webSocket);

        var connection = new Connection();
        connection.MarkAsRemote();

        transport.MessageReceived += (_, message) =>
        {
            Connection.TraceMessage("pw:channel:recv", message);
            connection.Dispatch(JsonSerializer.Deserialize<PlaywrightServerMessage>(message, JsonExtensions.DefaultJsonSerializerOptions)!);
        };

        Browser? browser = null;

        transport.TransportClosed += (_, reason) =>
        {
            // Emulate all pages, contexts and the browser closing upon disconnect.
            if (browser?._contexts != null)
            {
                foreach (BrowserContext context in browser._contexts.ToArray())
                {
                    foreach (Page page in context._pages.ToArray())
                    {
                        page.OnClose();
                    }
                    context.OnClose();
                }
            }
            browser?.DidClose();
            connection.DoClose(reason);
        };

        connection.OnMessage = (message, keepNulls) =>
        {
            var rawMessage = JsonSerializer.SerializeToUtf8Bytes(message, keepNulls ? connection.DefaultJsonSerializerOptionsKeepNulls : connection.DefaultJsonSerializerOptions);
            Connection.TraceMessage("pw:channel:send", rawMessage);
            return transport.SendAsync(rawMessage);
        };

        connection.Close += (_, reason) => transport.Close(reason);

        PlaywrightImpl playwright = await connection.InitializePlaywrightAsync().ConfigureAwait(false);
        browser = playwright.PreLaunchedBrowser;

        browser.ShouldCloseConnectionOnClose = true;
        browser.Disconnected += (_, _) => _ = transport.DisposeAsync();
        browser.ConnectToBrowserType((Core.BrowserType)playwright[browserName], null);

        return browser;
    }
}
