// <copyright file="HttpJsonPostTransport.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using OpenTelemetry.Internal;

namespace OpenTelemetry.Exporter.OneCollector;

internal sealed class HttpJsonPostTransport : ITransport, IDisposable
{
    private static readonly string SdkVersion = $"OTel-{Environment.OSVersion.Platform}-.net-{typeof(OneCollectorExporter<>).Assembly.GetName()?.Version?.ToString() ?? "0.0.0"}";
    private static readonly string UserAgent = $".NET/{Environment.Version} HttpClient";

    private readonly CallbackManager<OneCollectorExporterPayloadTransmittedCallbackAction> payloadTransmittedCallbacks = new();
    private readonly Uri endpoint;
    private readonly string instrumentationKey;
    private readonly OneCollectorExporterHttpTransportCompressionType compressionType;
    private readonly HttpClient httpClient;
    private MemoryStream? buffer;

    public HttpJsonPostTransport(
        string instrumentationKey,
        Uri endpoint,
        OneCollectorExporterHttpTransportCompressionType compressionType,
        HttpClient httpClient)
    {
        Debug.Assert(!string.IsNullOrWhiteSpace(instrumentationKey), "instrumentationKey was null or whitespace");
        Debug.Assert(endpoint != null, "endpoint was null");
        Debug.Assert(httpClient != null, "httpClient was null");

        this.instrumentationKey = instrumentationKey!;
        this.endpoint = endpoint!;
        this.compressionType = compressionType;
        this.httpClient = httpClient!;

        this.Description = $"http.jsonpost@{endpoint}";
    }

    public string Description { get; }

    public void Dispose()
    {
        this.payloadTransmittedCallbacks.Dispose();
        this.buffer?.Dispose();
    }

    public IDisposable RegisterPayloadTransmittedCallback(OneCollectorExporterPayloadTransmittedCallbackAction callback)
    {
        Guard.ThrowIfNull(callback);

        return this.payloadTransmittedCallbacks.Add(callback);
    }

    public bool Send(in TransportSendRequest sendRequest)
    {
        Debug.Assert(sendRequest.ItemStream.CanSeek, "ItemStream was not seekable.");
        Debug.Assert(
            sendRequest.ItemSerializationFormat == OneCollectorExporterSerializationFormatType.CommonSchemaV4JsonStream,
            "sendRequest.ItemSerializationFormat was not CommonSchemaV4JsonStream");

        var streamStartingPosition = sendRequest.ItemStream.Position;

        // Prevent OneCollector's HTTP operations from being instrumented.
        using var scope = SuppressInstrumentationScope.Begin();

        try
        {
            var content = this.BuildRequestContent(sendRequest.ItemStream);

            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-json-stream")
            {
                CharSet = "utf-8",
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, this.endpoint)
            {
                Content = content,
            };

            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("sdk-version", SdkVersion);
            request.Headers.TryAddWithoutValidation("x-apikey", this.instrumentationKey);

            bool logResponseDetails = OneCollectorExporterEventSource.Log.IsInformationalLoggingEnabled();

            if (!logResponseDetails)
            {
                request.Headers.TryAddWithoutValidation("NoResponseBody", "true");
            }

#if NET6_0_OR_GREATER
            using var response = this.httpClient.Send(request, CancellationToken.None);
#else
            using var response = this.httpClient.SendAsync(request, CancellationToken.None).GetAwaiter().GetResult();
#endif

            try
            {
                response.EnsureSuccessStatusCode();

                OneCollectorExporterEventSource.Log.WriteTransportDataSentEventIfEnabled(sendRequest.ItemType, sendRequest.NumberOfItems, this.Description);

                var root = this.payloadTransmittedCallbacks.Root;
                if (root != null)
                {
                    this.InvokePayloadTransmittedCallbacks(
                        root,
                        streamStartingPosition,
                        in sendRequest);
                }

                return true;
            }
            catch
            {
                response.Headers.TryGetValues("Collector-Error", out var collectorErrors);

                var errorDetails = logResponseDetails ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult() : null;

                OneCollectorExporterEventSource.Log.WriteHttpTransportErrorResponseReceivedEventIfEnabled(
                    this.Description,
                    (int)response.StatusCode,
                    collectorErrors,
                    errorDetails);

                return false;
            }
        }
        catch (Exception ex)
        {
            OneCollectorExporterEventSource.Log.WriteTransportExceptionThrownEventIfEnabled(this.Description, ex);

            return false;
        }
    }

    private HttpContent BuildRequestContent(Stream stream)
    {
        switch (this.compressionType)
        {
            case OneCollectorExporterHttpTransportCompressionType.None:
                return new NonDisposingStreamContent(stream);

            case OneCollectorExporterHttpTransportCompressionType.Deflate:
                var buffer = this.buffer;
                if (buffer == null)
                {
                    buffer = this.buffer = new MemoryStream(8192);
                }
                else
                {
                    buffer.SetLength(0);
                }

                using (var compressionStream = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    stream.CopyTo(compressionStream);
                }

                buffer.Position = 0;

                var content = new NonDisposingStreamContent(buffer);

                content.Headers.TryAddWithoutValidation("Content-Encoding", "deflate");

                return content;

            default:
                throw new NotSupportedException($"Compression type '{this.compressionType}' is not supported.");
        }
    }

    private void InvokePayloadTransmittedCallbacks(
        OneCollectorExporterPayloadTransmittedCallbackAction callback,
        long streamStartingPosition,
        in TransportSendRequest sendRequest)
    {
        var stream = sendRequest.ItemStream;

        var currentPosition = stream.Position;

        try
        {
            stream.Position = streamStartingPosition;

            callback(
                new OneCollectorExporterPayloadTransmittedCallbackArguments(
                    sendRequest.ItemSerializationFormat,
                    stream,
                    OneCollectorExporterTransportProtocolType.HttpJsonPost,
                    this.endpoint));
        }
        catch (Exception ex)
        {
            OneCollectorExporterEventSource.Log.WriteExceptionThrownFromUserCodeEventIfEnabled("PayloadTransmittedCallback", ex);
        }
        finally
        {
            stream.Position = currentPosition;
        }
    }

    private sealed class NonDisposingStreamContent : HttpContent
    {
#pragma warning disable CA2213 // Disposable fields should be disposed
        private readonly Stream stream;
#pragma warning restore CA2213 // Disposable fields should be disposed

        public NonDisposingStreamContent(Stream stream)
        {
            Debug.Assert(stream != null, "stream was null");

            this.stream = stream!;
        }

        protected override bool TryComputeLength(out long length)
        {
            var stream = this.stream;
            length = stream.Length - stream.Position;
            return true;
        }

#if NET6_0_OR_GREATER
        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            this.stream.CopyTo(stream);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            return this.stream.CopyToAsync(stream, cancellationToken);
        }
#endif

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return this.stream.CopyToAsync(stream);
        }
    }
}
