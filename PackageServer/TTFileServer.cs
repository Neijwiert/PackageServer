/*
Copyright 2019 Neijwiert

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using RenSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PackageServer
{
    // The reasoning behind using a TcpListener as opposed to a HttpListener is that it doesn't require administrator elevation
    // Or an exception set to the IP address it binds to
    internal sealed class TTFileServer : IDisposable
    {
        // 1024 bytes ought to be enough to get the entire request. 
        // If the client sent more than that it is probably an invalid request anyway
        private readonly int ClientGetRequestBufferSize = 1024;
        private readonly int ClientUploadFileBufferSize = 64000;
        private readonly int MaxDispatcherQueueSize = 4096; // This is a very generous dispatcher queue size.. some client is doing something very weird if this is exceeded

        private bool disposedResources;
        private readonly string ttfsPath;
        private readonly IPAddress hostAddress;
        private readonly int maxConnectionCount;
        private readonly TimeSpan clientTimeout;
        private readonly int localPort;
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly IDictionary<EndPoint, int> connectionCounts;
        private readonly Thread internalThread;

        public TTFileServer(string ttfsPath, IPAddress hostAddress, int maxConnectionCount, TimeSpan clientTimeout, IPAddress localAddress, int localPort)
        {
            if (ttfsPath == null)
            {
                throw new ArgumentNullException(nameof(ttfsPath));
            }
            else if (hostAddress == null)
            {
                throw new ArgumentNullException(nameof(hostAddress));
            }
            else if (localAddress == null)
            {
                throw new ArgumentNullException(nameof(localAddress));
            }
            else if (localPort < IPEndPoint.MinPort || localPort > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException(nameof(localPort));
            }

            this.disposedResources = false;
            this.ttfsPath = ttfsPath;
            this.hostAddress = hostAddress;
            this.maxConnectionCount = maxConnectionCount;
            this.clientTimeout = clientTimeout;
            this.localPort = localPort;
            this.listener = new TcpListener(localAddress, localPort);
            this.cancellationTokenSource = new CancellationTokenSource();
            this.connectionCounts = new Dictionary<EndPoint, int>();
            this.internalThread = new Thread(new ThreadStart(ServerLoop))
            {
                IsBackground = true
            };
        }

        public void Dispose()
        {
            if (disposedResources)
            {
                return;
            }

            Stop();

            cancellationTokenSource.Dispose();

            disposedResources = true;
        }

        public void Start()
        {
            if (!internalThread.ThreadState.HasFlag(ThreadState.Unstarted))
            {
                throw new InvalidOperationException();
            }

            listener.Start();
            internalThread.Start();
        }

        public void Stop()
        {
            cancellationTokenSource.Cancel();
            listener.Stop();
        }

        public void Join()
        {
            internalThread.Join();
        }

        public void Join(TimeSpan timeout)
        {
            internalThread.Join(timeout);
        }

        private void ServerLoop()
        {
            RenegadeDispatcher dispatcher = Engine.Dispatcher;
            dispatcher.MaxQueueSize = MaxDispatcherQueueSize; // Prevent clients from flooding the dispatcher queue

            try
            {
                // Keep going until a cancellation has been requested
                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    // Try get a client, this may return null when a client has a lot of connections open
                    var acceptedClient = AcceptClient(dispatcher);
                    if (!acceptedClient.HasValue)
                    {
                        continue;
                    }

                    // Make sure a client can timeout, or else a client can keep a connection open for a very long time (by only sending 1 byte or something like that)
                    CancellationTokenSource timeoutCancellationTokenSource = null;
                    CancellationTokenSource linkedCancellationTokenSource = null;
                    if (clientTimeout.TotalMinutes > 0)
                    {
                        timeoutCancellationTokenSource = new CancellationTokenSource(clientTimeout);
                        linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationTokenSource.Token,
                            timeoutCancellationTokenSource.Token);
                    }

                    // First task can timeout
                    Task.Run(async () =>
                    {
                        await HandleClientAsync(
                            acceptedClient.Value.Client,
                            acceptedClient.Value.RemoteEndPoint,
                            dispatcher,
                            timeoutCancellationTokenSource == null ? cancellationTokenSource.Token : timeoutCancellationTokenSource.Token);
                    }, timeoutCancellationTokenSource == null ? cancellationTokenSource.Token : timeoutCancellationTokenSource.Token)
                    .ContinueWith(delegate // Second task cannot timeout or be canceled, since we need to do some cleanup stuff
                    {
                        try
                        {
                            // Check if we timed out
                            if (timeoutCancellationTokenSource != null && timeoutCancellationTokenSource.IsCancellationRequested)
                            {
                                dispatcher.InvokeAsync(() =>
                                {
                                    Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Client {acceptedClient.Value.RemoteEndPoint} timed out after {clientTimeout.TotalMinutes} minute(s)\n");
                                });
                            }

                            // Clean up stuff
                            timeoutCancellationTokenSource?.Dispose();
                            linkedCancellationTokenSource?.Dispose();

                            acceptedClient.Value.Client.Dispose();
                        }
                        finally
                        {
                            // Make sure we always decrement this, so that the thread can exit when waiting for connections to get closed
                            DecreaseConnectionCount(acceptedClient.Value.RemoteEndPoint);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Accept may throw this, can still shutdown normally
            }
            catch (ObjectDisposedException) when (cancellationTokenSource.IsCancellationRequested)
            {
                // Can happen, should not prevent for normal shutdown
            }
            catch (SocketException ex) when (
                    ex.SocketErrorCode == SocketError.Interrupted ||
                    ex.SocketErrorCode == SocketError.ConnectionAborted ||
                    ex.SocketErrorCode == SocketError.ConnectionRefused ||
                    ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // When we stop the listener and are in the middle of a blocking accept, this exception may be thrown
            }
            catch (Exception ex)
            {
                dispatcher.InvokeAsync(() =>
                {
                    Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Fatal exception in file server thread '{ex.Message}'\n");
                });

                throw;
            }

            // Wait until all client connections are closed
            lock (connectionCounts)
            {
                while (connectionCounts.Count > 0)
                {
                    Monitor.Wait(connectionCounts);
                }
            }
        }

        private (TcpClient Client, EndPoint RemoteEndPoint)? AcceptClient(RenegadeDispatcher dispatcher)
        {
            try
            {
                TcpClient client = listener.AcceptTcpClient();
                try
                {
                    // Save the endpoint, as this may throw an exception
                    EndPoint remoteEndPoint = client.Client.RemoteEndPoint;

                    lock (connectionCounts)
                    {
                        // Only check for exceeding max connection count if we have specified a max count
                        if (maxConnectionCount > 0)
                        {
                            int currentConnectionCount = GetCounnectionCount(remoteEndPoint);
                            if (currentConnectionCount >= maxConnectionCount)
                            {
                                client.Dispose();

                                return null;
                            }
                        }

                        IncreaseConnectionCount(remoteEndPoint);

                        return (client, remoteEndPoint);
                    }
                }
                catch (Exception) // Odd, but I guess this can happen
                {
                    client.Dispose();

                    throw;
                }
            }
            catch (SocketException ex)
            {
                if (
                    ex.SocketErrorCode == SocketError.Interrupted ||
                    ex.SocketErrorCode == SocketError.ConnectionAborted ||
                    ex.SocketErrorCode == SocketError.ConnectionRefused ||
                    ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    throw new OperationCanceledException();
                }
                else
                {
                    dispatcher.InvokeAsync(() =>
                    {
                        Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Client error '{ex.Message}'\n");
                    });

                    return null; // Some communication error could've happen, should not stop everything
                }
            }
            catch (ObjectDisposedException) when (cancellationTokenSource.IsCancellationRequested)
            {
                throw new OperationCanceledException(); // May be thrown when cancelled
            }
        }

        private async Task HandleClientAsync(TcpClient client, EndPoint remoteEndPoint, RenegadeDispatcher dispatcher, CancellationToken cancellationToken)
        {
            try
            {
                TTFSHTTPRequestParser requestParser = new TTFSHTTPRequestParser(ClientGetRequestBufferSize);

                NetworkStream clientStream = client.GetStream();

                while (!requestParser.IsBufferFull && !cancellationToken.IsCancellationRequested)
                {
                    TTFSHTTPReadResult readResult = await requestParser.ReadAsync(clientStream, cancellationToken);
                    if (readResult == TTFSHTTPReadResult.PeerSocketClosed)
                    {
                        // We're done here, client closed connection
                        return;
                    }

                    // We've succesfully parsed the entire request, or it was invalid
                    if (readResult == TTFSHTTPReadResult.Done || readResult == TTFSHTTPReadResult.InvalidData)
                    {
                        break;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    // We're done here

                    return;
                }

                if (ValidateClientRequest(requestParser))
                {
                    await SendFileToClient(client, remoteEndPoint, requestParser.RequestTargetPath, dispatcher, cancellationToken);
                }
                else
                {
                    // Client did something wrong
                    await SendBadRequestResponse(client, remoteEndPoint, dispatcher, cancellationToken);
                }
            }
            catch (SocketException ex)
            {
                if (
                    ex.SocketErrorCode == SocketError.Interrupted ||
                    ex.SocketErrorCode == SocketError.ConnectionAborted ||
                    ex.SocketErrorCode == SocketError.ConnectionRefused ||
                    ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    throw new OperationCanceledException();
                }
                else
                {
                    dispatcher.InvokeAsync(() =>
                    {
                        Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Client {remoteEndPoint} triggered internal server error '{ex.Message}'\n");
                    });
                }
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
        }

        private bool ValidateClientRequest(TTFSHTTPRequestParser request)
        {
            // Parser checked a lot of stuff already, but we need to make sure it is fully parsed
            if (!request.ReceivedFullRequest)
            {
                return false;
            }

            // Client should not be allowed to specify a path outside the ttfspath
            string fullPath = Path.GetFullPath(Path.Combine(ttfsPath, request.RequestTargetPath));
            if (!fullPath.StartsWith(ttfsPath, StringComparison.Ordinal))
            {
                return false;
            }

            // Hosts should match
            if (!hostAddress.Equals(request.HostEndPoint.Address) || !localPort.Equals(request.HostEndPoint.Port))
            {
                return false;
            }

            // Seems to be an ok request
            return true;
        }

        private async Task SendFileToClient(TcpClient client, EndPoint remoteEndPoint, string requestTargetPath, RenegadeDispatcher dispatcher, CancellationToken cancellationToken)
        {
            string fullPath = Path.GetFullPath(Path.Combine(ttfsPath, requestTargetPath));
            try
            {
                using (FileStream fileStream = File.OpenRead(fullPath))
                {
                    NetworkStream clientStream = client.GetStream();

                    StringBuilder responseHeaderBuilder = new StringBuilder();
                    responseHeaderBuilder.Append("HTTP/1.0 200 OK");
                    responseHeaderBuilder.Append(Environment.NewLine);
                    responseHeaderBuilder.Append("DATE: ").Append(DateTime.UtcNow);
                    responseHeaderBuilder.Append(Environment.NewLine);
                    responseHeaderBuilder.Append("CONTENT-LENGTH: ").Append(fileStream.Length);
                    responseHeaderBuilder.Append(Environment.NewLine);
                    responseHeaderBuilder.Append("CONTENT-TYPE: application/octet-stream");
                    responseHeaderBuilder.Append(Environment.NewLine);
                    responseHeaderBuilder.Append("ACCEPT-RANGES: bytes");
                    responseHeaderBuilder.Append(Environment.NewLine);
                    responseHeaderBuilder.Append("CONNECTION: keep-alive");
                    responseHeaderBuilder.Append(Environment.NewLine);
                    responseHeaderBuilder.Append(Environment.NewLine);

                    byte[] responseHeader = Encoding.UTF8.GetBytes(responseHeaderBuilder.ToString());
                    await clientStream.WriteAsync(responseHeader, 0, responseHeader.Length, cancellationToken);
                    await fileStream.CopyToAsync(clientStream, ClientUploadFileBufferSize, cancellationToken);
                }
            }
            catch (FileNotFoundException)
            {
                // Client did something wrong
                await SendNotFoundResponse(client, remoteEndPoint, fullPath, dispatcher, cancellationToken);
            }
            catch (IOException ex)
            {
                if (ex.InnerException is SocketException socketException)
                {
                    if (
                        socketException.SocketErrorCode == SocketError.Interrupted ||
                        socketException.SocketErrorCode == SocketError.ConnectionAborted ||
                        socketException.SocketErrorCode == SocketError.ConnectionRefused ||
                        socketException.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        throw new OperationCanceledException(); // Connection closed
                    }
                }

                // We did something wrong
                await SendInternalServerErrorResponse(client, remoteEndPoint, ex, dispatcher, cancellationToken);
            }
        }

        private async Task SendBadRequestResponse(TcpClient client, EndPoint remoteEndPoint, RenegadeDispatcher dispatcher, CancellationToken cancellationToken)
        {
            dispatcher.InvokeAsync(() =>
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Client {remoteEndPoint} sent a bad request\n");
            });

            NetworkStream clientStream = client.GetStream();

            StringBuilder responseHeaderBuilder = new StringBuilder();
            responseHeaderBuilder.Append("HTTP/1.0 400 Bad Request");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("DATE: ").Append(DateTime.UtcNow);
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("CONTENT-LENGTH: 0");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("CONTENT-TYPE: application/octet-stream");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("ACCEPT-RANGES: bytes");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("CONNECTION: close");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append(Environment.NewLine);

            byte[] responseHeader = Encoding.UTF8.GetBytes(responseHeaderBuilder.ToString());
            await clientStream.WriteAsync(responseHeader, 0, responseHeader.Length, cancellationToken);
        }

        private async Task SendNotFoundResponse(TcpClient client, EndPoint remoteEndPoint, string filePath, RenegadeDispatcher dispatcher, CancellationToken cancellationToken)
        {
            dispatcher.InvokeAsync(() =>
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Client {remoteEndPoint} tried to request file '{filePath}' that does not exist\n");
            });

            NetworkStream clientStream = client.GetStream();

            StringBuilder responseHeaderBuilder = new StringBuilder();
            responseHeaderBuilder.Append("HTTP/1.0 404 Not Found");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("DATE: ").Append(DateTime.UtcNow);
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("CONTENT-LENGTH: 0");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("CONTENT-TYPE: application/octet-stream");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("ACCEPT-RANGES: bytes");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("CONNECTION: close");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append(Environment.NewLine);

            byte[] responseHeader = Encoding.UTF8.GetBytes(responseHeaderBuilder.ToString());
            await clientStream.WriteAsync(responseHeader, 0, responseHeader.Length, cancellationToken);
        }

        private async Task SendInternalServerErrorResponse(TcpClient client, EndPoint remoteEndPoint, Exception ex, RenegadeDispatcher dispatcher, CancellationToken cancellationToken)
        {
            dispatcher.InvokeAsync(() =>
            {
                Engine.ConsoleOutput($"[{nameof(PackageServer)}]: Client {remoteEndPoint} triggered internal server error '{ex.Message}'\n");
            });

            NetworkStream clientStream = client.GetStream();

            StringBuilder responseHeaderBuilder = new StringBuilder();
            responseHeaderBuilder.Append("HTTP/1.0 500 Internal Server Error");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("DATE: ").Append(DateTime.UtcNow);
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("CONTENT-LENGTH: 0");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("CONTENT-TYPE: application/octet-stream");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("ACCEPT-RANGES: bytes");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append("CONNECTION: close");
            responseHeaderBuilder.Append(Environment.NewLine);
            responseHeaderBuilder.Append(Environment.NewLine);

            byte[] responseHeader = Encoding.UTF8.GetBytes(responseHeaderBuilder.ToString());
            await clientStream.WriteAsync(responseHeader, 0, responseHeader.Length, cancellationToken);
        }

        private void DecreaseConnectionCount(EndPoint remoteEndPoint)
        {
            lock (connectionCounts)
            {
                if (connectionCounts.TryGetValue(remoteEndPoint, out int connectionCount))
                {
                    connectionCount--;
                    if (connectionCount <= 0)
                    {
                        connectionCounts.Remove(remoteEndPoint);

                        Monitor.PulseAll(connectionCounts);
                    }
                    else
                    {
                        connectionCounts[remoteEndPoint] = connectionCount;
                    }
                }
            }
        }

        private void IncreaseConnectionCount(EndPoint remoteEndPoint)
        {
            lock (connectionCounts)
            {
                if (connectionCounts.TryGetValue(remoteEndPoint, out int connectionCount))
                {
                    connectionCount++;
                    connectionCounts[remoteEndPoint] = connectionCount;
                }
                else
                {
                    connectionCounts.Add(remoteEndPoint, 1);
                }
            }
        }

        private int GetCounnectionCount(EndPoint remoteEndPoint)
        {
            lock (connectionCounts)
            {
                if (connectionCounts.TryGetValue(remoteEndPoint, out int connectionCount))
                {
                    return connectionCount;
                }

                return 0;
            }
        }
    }
}
