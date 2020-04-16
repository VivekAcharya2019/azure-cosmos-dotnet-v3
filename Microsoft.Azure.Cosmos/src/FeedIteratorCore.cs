//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.Json;
    using Microsoft.Azure.Cosmos.Json.Interop;
    using Microsoft.Azure.Cosmos.Query.Core;
    using Microsoft.Azure.Cosmos.Query.Core.Monads;
    using Microsoft.Azure.Cosmos.Resource.CosmosExceptions;
    using Microsoft.Azure.Documents;
    using static Microsoft.Azure.Documents.RuntimeConstants;

    /// <summary>
    /// Cosmos feed stream iterator. This is used to get the query responses with a Stream content
    /// </summary>
    internal sealed class FeedIteratorCore : FeedIteratorInternal
    {
        private readonly ContainerCore containerCore;
        private readonly CosmosClientContext clientContext;
        private readonly Uri resourceLink;
        private readonly ResourceType resourceType;
        private readonly SqlQuerySpec querySpec;
        private bool hasMoreResultsInternal;
        private FeedTokenInternal feedTokenInternal;

        internal static FeedIteratorCore CreateForNonPartitionedResource(
            CosmosClientContext clientContext,
            Uri resourceLink,
            ResourceType resourceType,
            QueryDefinition queryDefinition,
            string continuationToken,
            QueryRequestOptions options)
        {
            return new FeedIteratorCore(
                clientContext: clientContext,
                containerCore: null,
                resourceLink: resourceLink,
                resourceType: resourceType,
                queryDefinition: queryDefinition,
                continuationToken: continuationToken,
                feedTokenInternal: null,
                options: options);
        }

        internal static FeedIteratorCore CreateForPartitionedResource(
            ContainerCore containerCore,
            Uri resourceLink,
            ResourceType resourceType,
            QueryDefinition queryDefinition,
            string continuationToken,
            FeedTokenInternal feedTokenInternal,
            QueryRequestOptions options)
        {
            if (containerCore == null)
            {
                throw new ArgumentNullException(nameof(containerCore));
            }

            return new FeedIteratorCore(
                containerCore: containerCore,
                clientContext: containerCore.ClientContext,
                resourceLink: resourceLink,
                resourceType: resourceType,
                queryDefinition: queryDefinition,
                continuationToken: continuationToken,
                feedTokenInternal: feedTokenInternal,
                options: options);
        }

        private FeedIteratorCore(
            ContainerCore containerCore,
            CosmosClientContext clientContext,
            Uri resourceLink,
            ResourceType resourceType,
            QueryDefinition queryDefinition,
            string continuationToken,
            FeedTokenInternal feedTokenInternal,
            QueryRequestOptions options)
        {
            this.resourceLink = resourceLink;
            this.containerCore = containerCore;
            this.clientContext = clientContext;
            this.resourceType = resourceType;
            this.querySpec = queryDefinition?.ToSqlQuerySpec();
            this.feedTokenInternal = feedTokenInternal;
            this.ContinuationToken = continuationToken ?? this.feedTokenInternal?.GetContinuation();
            this.requestOptions = options;
            this.hasMoreResultsInternal = true;
        }

        public override bool HasMoreResults => this.hasMoreResultsInternal;

#if PREVIEW
        public override
#else
        internal
#endif
        FeedToken FeedToken => this.feedTokenInternal;

        /// <summary>
        /// The query options for the result set
        /// </summary>
        public QueryRequestOptions requestOptions { get; }

        /// <summary>
        /// The Continuation Token
        /// </summary>
        public string ContinuationToken { get; set; }

        /// <summary>
        /// Get the next set of results from the cosmos service
        /// </summary>
        /// <param name="cancellationToken">(Optional) <see cref="CancellationToken"/> representing request cancellation.</param>
        /// <returns>A query response from cosmos service</returns>
        public override async Task<ResponseMessage> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            CosmosDiagnosticsContext diagnostics = CosmosDiagnosticsContext.Create(this.requestOptions);
            using (diagnostics.GetOverallScope())
            {
                return await this.ReadNextInternalAsync(diagnostics, cancellationToken);
            }
        }

        private async Task<ResponseMessage> ReadNextInternalAsync(
            CosmosDiagnosticsContext diagnostics,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Stream stream = null;
            OperationType operation = OperationType.ReadFeed;
            if (this.querySpec != null)
            {
                stream = this.clientContext.SerializerCore.ToStreamSqlQuerySpec(this.querySpec, this.resourceType);
                operation = OperationType.Query;
            }

            if (this.feedTokenInternal == null)
            {
                TryCatch<FeedTokenInternal> tryCatchFeedTokeninternal = await this.TryInitializeFeedTokenAsync(cancellationToken);
                if (!tryCatchFeedTokeninternal.Succeeded)
                {
                    if (tryCatchFeedTokeninternal.Exception.InnerException is CosmosException cosmosException)
                    {
                        return cosmosException.ToCosmosResponseMessage(new RequestMessage(method: null, requestUri: null, diagnosticsContext: diagnostics));
                    }

                    return CosmosExceptionFactory.CreateInternalServerErrorException(
                        message: tryCatchFeedTokeninternal.Exception.InnerException.Message,
                        innerException: tryCatchFeedTokeninternal.Exception.InnerException,
                        diagnosticsContext: diagnostics).ToCosmosResponseMessage(new RequestMessage(method: null, requestUri: null, diagnosticsContext: diagnostics));
                }

                this.feedTokenInternal = tryCatchFeedTokeninternal.Result;
            }

            ResponseMessage responseMessage = await this.clientContext.ProcessResourceOperationStreamAsync(
               resourceUri: this.resourceLink,
               resourceType: this.resourceType,
               operationType: operation,
               requestOptions: this.requestOptions,
               cosmosContainerCore: null,
               partitionKey: this.requestOptions?.PartitionKey,
               streamPayload: stream,
               requestEnricher: request =>
               {
                   QueryRequestOptions.FillContinuationToken(request, this.ContinuationToken);
                   if (this.querySpec != null)
                   {
                       request.Headers.Add(HttpConstants.HttpHeaders.ContentType, MediaTypes.QueryJson);
                       request.Headers.Add(HttpConstants.HttpHeaders.IsQuery, bool.TrueString);
                   }

                   this.feedTokenInternal?.EnrichRequest(request);
               },
               diagnosticsContext: diagnostics,
               cancellationToken: cancellationToken);

            // Retry in case of splits or other scenarios only on partitioned resources
            if (this.containerCore != null
                && await this.feedTokenInternal.ShouldRetryAsync(this.containerCore, responseMessage, cancellationToken))
            {
                return await this.ReadNextInternalAsync(diagnostics, cancellationToken);
            }

            if (responseMessage.IsSuccessStatusCode)
            {
                this.feedTokenInternal.UpdateContinuation(responseMessage.Headers.ContinuationToken);
                this.ContinuationToken = this.feedTokenInternal.GetContinuation();
                this.hasMoreResultsInternal = !this.feedTokenInternal.IsDone;
            }
            else
            {
                this.hasMoreResultsInternal = false;
            }

            if ((responseMessage.Content == null) || (responseMessage.Content.Length == 0))
            {
                return responseMessage;
            }

            // Rewrite the payload to be in the specified format.
            // If it's already in the correct format, then the following will be a memcpy.
            MemoryStream memoryStream;
            if (responseMessage.Content is MemoryStream responseContentAsMemoryStream)
            {
                memoryStream = responseContentAsMemoryStream;
            }
            else
            {
                memoryStream = new MemoryStream();
                await responseMessage.Content.CopyToAsync(memoryStream);
            }

            ReadOnlyMemory<byte> buffer;
            if (memoryStream.TryGetBuffer(out ArraySegment<byte> segment))
            {
                buffer = segment.Array.AsMemory().Slice(start: segment.Offset, length: segment.Count);
            }
            else
            {
                buffer = memoryStream.ToArray();
            }

            IJsonReader jsonReader = JsonReader.Create(buffer);
            IJsonWriter jsonWriter;
            if (this.requestOptions?.CosmosSerializationFormatOptions != null)
            {
                jsonWriter = this.requestOptions.CosmosSerializationFormatOptions.CreateCustomWriterCallback();
            }
            else
            {
                jsonWriter = NewtonsoftToCosmosDBWriter.CreateTextWriter();
            }

            jsonWriter.WriteAll(jsonReader);

            ReadOnlyMemory<byte> result = jsonWriter.GetResult();
            MemoryStream rewrittenMemoryStream;
            if (MemoryMarshal.TryGetArray(result, out ArraySegment<byte> rewrittenSegment))
            {
                rewrittenMemoryStream = new MemoryStream(rewrittenSegment.Array, index: rewrittenSegment.Offset, count: rewrittenSegment.Count);
            }
            else
            {
                rewrittenMemoryStream = new MemoryStream(result.ToArray());
            }

            responseMessage.Content = rewrittenMemoryStream;
            return responseMessage;
        }

        private async Task<TryCatch<FeedTokenInternal>> TryInitializeFeedTokenAsync(CancellationToken cancellationToken)
        {
            string containerRId = string.Empty;
            if (this.containerCore != null)
            {
                try
                {
                    containerRId = await this.containerCore.GetRIDAsync(cancellationToken);
                }
                catch (Exception cosmosException)
                {
                    return TryCatch<FeedTokenInternal>.FromException(cosmosException);
                }
            }

            // Create FeedToken for the full Range
            FeedTokenEPKRange feedTokenInternal = new FeedTokenEPKRange(
                containerRId,
                new PartitionKeyRange()
                {
                    MinInclusive = Documents.Routing.PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey,
                    MaxExclusive = Documents.Routing.PartitionKeyInternal.MaximumExclusiveEffectivePartitionKey
                });
            // Initialize with the ContinuationToken that the user passed, if any
            if (this.ContinuationToken != null)
            {
                feedTokenInternal.UpdateContinuation(this.ContinuationToken);
            }

            return TryCatch<FeedTokenInternal>.FromResult(feedTokenInternal);
        }

        public override CosmosElement GetCosmosElementContinuationToken()
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Cosmos feed iterator that keeps track of the continuation token when retrieving results form a query.
    /// </summary>
    /// <typeparam name="T">The response object type that can be deserialized</typeparam>
    internal sealed class FeedIteratorCore<T> : FeedIteratorInternal<T>
    {
        private readonly FeedIteratorInternal feedIterator;
        private readonly Func<ResponseMessage, FeedResponse<T>> responseCreator;

        internal FeedIteratorCore(
            FeedIteratorInternal feedIterator,
            Func<ResponseMessage, FeedResponse<T>> responseCreator)
        {
            this.responseCreator = responseCreator;
            this.feedIterator = feedIterator;
        }

        public override bool HasMoreResults => this.feedIterator.HasMoreResults;

        public override CosmosElement GetCosmosElementContinuationToken()
        {
            return this.feedIterator.GetCosmosElementContinuationToken();
        }

#if PREVIEW
        public override FeedToken FeedToken => this.feedIterator.FeedToken;
#endif

        /// <summary>
        /// Get the next set of results from the cosmos service
        /// </summary>
        /// <param name="cancellationToken">(Optional) <see cref="CancellationToken"/> representing request cancellation.</param>
        /// <returns>A query response from cosmos service</returns>
        public override async Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ResponseMessage response = await this.feedIterator.ReadNextAsync(cancellationToken);
            return this.responseCreator(response);
        }
    }
}
