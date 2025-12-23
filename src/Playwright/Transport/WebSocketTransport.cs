/*
 * MIT License
 *
 * Copyright (c) Microsoft Corporation.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
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
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Playwright.Transport;

internal sealed class WebSocketTransport : IAsyncDisposable
{
    private readonly ClientWebSocket _webSocket;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _readTask;
    private bool _isClosed;

    public WebSocketTransport(ClientWebSocket webSocket)
    {
        _webSocket = webSocket;
        _readTask = Task.Run(() => ReadAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
    }

    public event EventHandler<byte[]>? MessageReceived;

    public event EventHandler<Exception>? TransportClosed;

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                var messageBytes = new MemoryStream();

                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Close(new WebSocketException("WebSocket closed by the server."));
                        return;
                    }

#pragma warning disable VSTHRD103 // Call async methods when in an async method. This is a memory stream.
                    messageBytes.Write(buffer, 0, result.Count);
#pragma warning restore VSTHRD103 // Call async methods when in an async method
                }
                while (!result.EndOfMessage);

                MessageReceived?.Invoke(this, messageBytes.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Close(ex);
            return;
        }
    }

    public void Close(Exception closeReason)
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _cancellationTokenSource.Cancel();
        _webSocket.Abort();
        TransportClosed?.Invoke(this, closeReason);
    }

    public Task SendAsync(byte[] message)
    {
        return _webSocket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _readTask.ConfigureAwait(false);
        _cancellationTokenSource.Dispose();
        _readTask.Dispose();
        _webSocket.Dispose();
    }
}
