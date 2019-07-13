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

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PackageServer
{
    internal sealed class TTFSHTTPRequestParser
    {
        private static readonly string ExpectedRequestType = "GET";
        private static readonly string ExpectedHTTPVersionPrefix = "HTTP/";
        private static readonly string ExpectedBuildProductName = "TT";
        private static readonly string ExpectedConnection = "KEEP-ALIVE";

        public TTFSHTTPRequestParser(int bufferSize)
        {
            Buffer = new byte[bufferSize];
            BufferOffset = 0;
        }

        public async Task<TTFSHTTPReadResult> ReadAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            else if (IsBufferFull)
            {
                throw new InternalBufferOverflowException();
            }

            int bytesRead = await stream.ReadAsync(
                Buffer,
                BufferOffset,
                Buffer.Length - BufferOffset,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return TTFSHTTPReadResult.Done;
            }
            else if (bytesRead <= 0)
            {
                return TTFSHTTPReadResult.PeerSocketClosed;
            }

            BufferOffset += bytesRead;

            string rawRequest;
            try
            {
                rawRequest = Encoding.UTF8.GetString(Buffer, 0, BufferOffset);
            }
            catch (ArgumentException)
            {
                // Invalid chars found, either client is doing something fucky, or we're still missing some data
                // We're going to assume that we are missing some data
                return TTFSHTTPReadResult.IncompleteData;
            }

            return ParseRawRequest(rawRequest);
        }

        private TTFSHTTPReadResult ParseRawRequest(string rawRequest)
        {
            ResetRequest();

            string[] headers = rawRequest.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (headers.Length <= 0)
            {
                return TTFSHTTPReadResult.IncompleteData; // Nothing to do here
            }

            string rawRequestType = null;

            int currentHeaderIndex;
            for (
                string currentRawHeader = GetNextNonWhitespaceString(headers, 0, out currentHeaderIndex);
                currentRawHeader!= null && !ReceivedFullRequest;
                currentRawHeader = GetNextNonWhitespaceString(headers, currentHeaderIndex + 1, out currentHeaderIndex))
            {
                TTFSHTTPReadResult currentParseResult;

                // First header should be the request type
                if (rawRequestType == null)
                {
                    rawRequestType = currentRawHeader;
                    currentParseResult = ParseRequestType(rawRequestType);
                }
                else
                {
                    currentParseResult = ParseHeader(currentRawHeader);
                }

                if (currentParseResult != TTFSHTTPReadResult.Done) // Check if current header is valid
                {
                    // We need to check if we still have remaining headers if it returned incomplete data
                    // Because that means that it is invalid data
                    if (currentParseResult == TTFSHTTPReadResult.IncompleteData)
                    {
                        if (GetNextNonWhitespaceString(headers, currentHeaderIndex + 1, out currentHeaderIndex) != null)
                        {
                            return TTFSHTTPReadResult.InvalidData;
                        }
                    }

                    // Otherwise return whatever result we got
                    return currentParseResult;
                }
            }

            return (ReceivedFullRequest ? TTFSHTTPReadResult.Done : TTFSHTTPReadResult.IncompleteData);
        }

        private TTFSHTTPReadResult ParseRequestType(string rawRequestType)
        {
            string[] splitRawRequestType = rawRequestType.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (splitRawRequestType.Length <= 0)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            // First string should be request type
            string requestType = GetNextNonWhitespaceString(splitRawRequestType, 0, out int currentStringIndex);
            if (requestType == null)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            // We expect the type to be GET
            if (requestType.Length != ExpectedRequestType.Length)
            {
                // This request is invalid if:
                // - The request was longer than the expected request type
                // - The request was shorter than the expected request type and we still have more stuff from the raw request
                if (requestType.Length > ExpectedRequestType.Length || ((currentStringIndex + 1) < splitRawRequestType.Length))
                {
                    return TTFSHTTPReadResult.InvalidData;
                }
                else
                {
                    return TTFSHTTPReadResult.IncompleteData; // Must be missing data
                }
            }

            // Obviously the requestType should be exactly the same as the expected request type
            if (!requestType.Equals(ExpectedRequestType, StringComparison.InvariantCulture))
            {
                return TTFSHTTPReadResult.InvalidData;
            }

            // Second string should be the target path
            RequestTargetPath = GetNextNonWhitespaceString(splitRawRequestType, currentStringIndex + 1, out currentStringIndex);
            if (RequestTargetPath == null)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            // Make sure we unescape all special characters
            RequestTargetPath = Uri.UnescapeDataString(RequestTargetPath);

            // We have to remove any leading slashes
            RequestTargetPath = RequestTargetPath.TrimStart('/', '\\');
            if (RequestTargetPath.Equals(string.Empty, StringComparison.InvariantCulture))
            {
                if (((currentStringIndex + 1) < splitRawRequestType.Length))
                {
                    return TTFSHTTPReadResult.InvalidData;
                }
                else
                {
                    return TTFSHTTPReadResult.IncompleteData; // Must be missing data
                }
            }

            // Third string should be the http version
            string requestHTTPVersionString = GetNextNonWhitespaceString(splitRawRequestType, currentStringIndex + 1, out currentStringIndex);
            if (requestHTTPVersionString == null)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            // We expect the http version to start with HTTP/
            if (requestHTTPVersionString.Length <= ExpectedHTTPVersionPrefix.Length)
            {
                // This request is invalid if:
                // - The request was shorter than the expected request type and we still have more stuff from the raw request
                if (((currentStringIndex + 1) < splitRawRequestType.Length))
                {
                    return TTFSHTTPReadResult.InvalidData;
                }
                else
                {
                    return TTFSHTTPReadResult.IncompleteData; // Must be missing data
                }
            }

            // The http version should start with the expected http version
            if (!requestHTTPVersionString.StartsWith(ExpectedHTTPVersionPrefix, StringComparison.InvariantCulture))
            {
                return TTFSHTTPReadResult.InvalidData;
            }

            string httpVersionString = requestHTTPVersionString.Substring(ExpectedHTTPVersionPrefix.Length);

            if (!Version.TryParse(httpVersionString, out Version requestHTTPVersion))
            {
                // If we still have data, that must mean that the version is invalid
                if (((currentStringIndex + 1) < splitRawRequestType.Length))
                {
                    return TTFSHTTPReadResult.InvalidData;
                }
                else
                {
                    return TTFSHTTPReadResult.IncompleteData; // Could be missing data, but we can't know for sure in the current context
                }
            }

            RequestHTTPVersion = requestHTTPVersion;

            // We don't expect any other extra data at the end
            if (GetNextNonWhitespaceString(splitRawRequestType, currentStringIndex + 1, out _) == null)
            {
                return TTFSHTTPReadResult.Done;
            }
            else
            {
                return TTFSHTTPReadResult.InvalidData; // Unexpected more data
            }
        }

        private TTFSHTTPReadResult ParseHeader(string rawHeader)
        {
            string[] splitRawHeader = rawHeader.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (splitRawHeader.Length <= 0)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            // First string should be the header type
            string rawHeaderType = GetNextNonWhitespaceString(splitRawHeader, 0, out int currentStringIndex);
            if (rawHeaderType == null)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            // Last character must be a colon
            if (rawHeaderType.Length <= 1 || rawHeaderType[rawHeaderType.Length - 1] != ':')
            {
                return TTFSHTTPReadResult.InvalidData;
            }

            string headerType = rawHeaderType.Substring(0, rawHeaderType.Length - 1);
            if (headerType.Equals("USER-AGENT", StringComparison.InvariantCultureIgnoreCase))
            {
                return ParseUserAgent(GetNextNonWhitespaceString(splitRawHeader, currentStringIndex + 1, out _));
            }
            else if (headerType.Equals("HOST", StringComparison.InvariantCultureIgnoreCase))
            {
                return ParseHost(GetNextNonWhitespaceString(splitRawHeader, currentStringIndex + 1, out _));
            }
            else if (headerType.Equals("CONNECTION", StringComparison.InvariantCultureIgnoreCase))
            {
                return ParseConnection(GetNextNonWhitespaceString(splitRawHeader, currentStringIndex + 1, out _));
            }
            else
            {
                // Unknown header, assume it is valid
                return TTFSHTTPReadResult.Done;
            }
        }

        private TTFSHTTPReadResult ParseUserAgent(string rawUserAgent)
        {
            if (rawUserAgent == null)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            // User agent is split into 'Build_Product_Name/Build_TT_Version_String'
            string[] userAgentSplit = rawUserAgent.Split(new[] { '/' }, StringSplitOptions.None);
            if (userAgentSplit.Length != 2)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            BuildProductName = userAgentSplit[0];
            BuildTTVersionString = userAgentSplit[1];

            // Product name cannot be emtpy at this point
            // Also must match exactly 'TT'
            if (
                BuildProductName.Equals(string.Empty, StringComparison.InvariantCulture) ||
                !BuildProductName.Equals(ExpectedBuildProductName, StringComparison.InvariantCulture))
            {
                return TTFSHTTPReadResult.InvalidData;
            }

            // TT version string may be empty
            if (BuildTTVersionString.Equals(string.Empty, StringComparison.InvariantCulture))
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            return TTFSHTTPReadResult.Done;
        }

        private TTFSHTTPReadResult ParseHost(string rawHost)
        {
            if (rawHost == null)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }


            // Just try and pare the host endpoint
            if (!Uri.TryCreate($"http://{rawHost}", UriKind.Absolute, out Uri uri) || !IPAddress.TryParse(uri.Host, out IPAddress ip))
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            HostEndPoint = new IPEndPoint(ip, uri.Port);

            return TTFSHTTPReadResult.Done;
        }

        private TTFSHTTPReadResult ParseConnection(string rawConnection)
        {
            if (rawConnection == null)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }

            // We expect exactly KEEP-ALIVE
            if (rawConnection.Length < ExpectedConnection.Length)
            {
                return TTFSHTTPReadResult.IncompleteData;
            }
            else if (rawConnection.Length > ExpectedConnection.Length)
            {
                return TTFSHTTPReadResult.InvalidData;
            }

            if (rawConnection.Equals(ExpectedConnection, StringComparison.InvariantCultureIgnoreCase))
            {
                KeepConnectionAlive = true;

                return TTFSHTTPReadResult.Done;
            }
            else
            {
                return TTFSHTTPReadResult.InvalidData;
            }
        }

        private string GetNextNonWhitespaceString(string[] items, int startIndex, out int resultIndex)
        {
            for (int x = startIndex; x < items.Length; x++)
            {
                string currentItem = items[x].Trim();
                if (currentItem.Equals(string.Empty, StringComparison.InvariantCulture))
                {
                    continue;
                }

                resultIndex = x;

                return currentItem;
            }

            resultIndex = -1;

            return null;
        }

        private void ResetRequest()
        {
            RequestTargetPath = null;
            RequestHTTPVersion = null;
            BuildProductName = null;
            BuildTTVersionString = null;
            HostEndPoint = null;
            KeepConnectionAlive = null;
        }

        public byte[] Buffer
        {
            get;
            private set;
        }

        public int BufferOffset
        {
            get;
            private set;
        }

        public bool IsBufferFull
        {
            get
            {
                return (BufferOffset >= Buffer.Length);
            }
        }

        public bool ReceivedFullRequest
        {
            get
            {
                return (
                    RequestTargetPath != null &&
                    RequestHTTPVersion != null &&
                    BuildProductName != null &&
                    BuildTTVersionString != null &&
                    HostEndPoint != null &&
                    KeepConnectionAlive.HasValue);
            }
        }

        public string RequestTargetPath
        {
            get;
            private set;
        }

        public Version RequestHTTPVersion
        {
            get;
            private set;
        }

        public string BuildProductName
        {
            get;
            private set;
        }

        public string BuildTTVersionString
        {
            get;
            private set;
        }

        public IPEndPoint HostEndPoint
        {
            get;
            private set;
        }

        public bool? KeepConnectionAlive
        {
            get;
            private set;
        }
    }

}
