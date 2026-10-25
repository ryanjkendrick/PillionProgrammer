using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pillion.Common.Redaction;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway;

/// <summary>The tool transport (work item 3.23): <see cref="ChatWithToolsAsync"/> and <see cref="ResolveToolRoute"/>.</summary>
public sealed partial class ModelGateway
{
    /// <inheritdoc/>
    public ToolRoute ResolveToolRoute(ToolChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = _router.Resolve(new RoutingRequest
        {
            TaskClass = request.TaskClass,
            Profile = request.Profile,
            ModelId = request.ModelId,
            EstimatedPromptTokens = EstimateTokens(request.Messages, request.Tools),
        });
        return new ToolRoute(route.ProviderId, route.ModelId, route.IsLocal, ToolCallingOf(route), route.Reason);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ToolChatUpdate> ChatWithToolsAsync(ToolChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw new ArgumentException("A tool chat request needs at least one message.", nameof(request));
        }

        var route = _router.Resolve(new RoutingRequest
        {
            TaskClass = request.TaskClass,
            Profile = request.Profile,
            ModelId = request.ModelId,
            EstimatedPromptTokens = EstimateTokens(request.Messages, request.Tools),
        });
        LogRouted(_logger, request.RequestId, route.QualifiedId, route.Profile, route.Reason);

        // The cap is applied here, once, so every adapter sends the same text and the Outbound log records what was sent.
        var messages = CapResults(request.Messages, out var resultBytes);
        var declarations = request.Tools.OfType<AIFunctionDeclaration>().ToList();
        var toolNames = declarations.Select(t => t.Name).ToList();
        var promptTokens = EstimateTokens(messages, request.Tools);
        var redactForLocal = _registry.GetSnapshot().Settings.Settings.Privacy.RedactForLocalProviders;
        var policy = new AttemptPolicy
        {
            RequestId = request.RequestId,
            TaskClass = request.TaskClass,
            PromptTokens = promptTokens,
            MaxRetries = _services.Options.MaxRetries,
            ToolResultBytes = resultBytes,
        };

        RoutedModel? model = null;
        StreamAttempt<ChatResponseUpdate>? attempt = null;
        RedactionSession? session = null;
        Exception? lastFailure = null;
        var transport = ToolTransport.None;
        var candidates = new List<RoutedModel> { route };
        candidates.AddRange(route.Fallbacks);
        (RoutedModel Model, ToolsNotSupportedException Error)? downgraded = null;

        try
        {
            foreach (var candidate in candidates)
            {
                var provider = _registry.GetSnapshot().Find(candidate.ProviderId);
                if (provider is null)
                {
                    continue;
                }

                if (lastFailure is not null)
                {
                    _services.Progress.Report(new ModelProgress(request.RequestId, candidate.ProviderId, candidate.ModelId, ModelProgressKind.FallingBack, TimeSpan.Zero, $"Trying {candidate.QualifiedId} instead: {lastFailure.Message}"));
                }

                var sendTools = declarations.Count > 0 && ToolCallingOf(candidate) == ToolCallingSupport.Native;
                session = provider.IsLocal && !redactForLocal ? null : new RedactionSession();
                var client = provider.GetChatClient(candidate.ModelId);
                if (session is not null)
                {
                    client = new RedactingChatClient(client, session);
                }

                var options = new ChatOptions
                {
                    ModelId = candidate.ModelId,
                    MaxOutputTokens = request.MaxOutputTokens,
                    Tools = sendTools ? [.. declarations] : null,
                    ToolMode = sendTools ? request.ToolMode : null,
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [GatewayOptionKeys.Profile] = candidate.Profile,
                        [GatewayOptionKeys.RequestId] = request.RequestId,
                    },
                };
                var currentSession = session;
                attempt = new StreamAttempt<ChatResponseUpdate>(
                    _services,
                    candidate,
                    policy with
                    {
                        FirstTokenTimeout = candidate.IsLocal ? null : _services.Options.CloudFirstTokenTimeout,
                        LocalBudget = candidate.IsLocal ? _services.Options.LocalFirstTokenBudget(promptTokens) : null,
                        ToolsOffered = sendTools ? toolNames : [],
                    },
                    token => client.GetStreamingResponseAsync(messages, options, token),
                    update => !string.IsNullOrEmpty(update.Text) || update.Contents.Any(c => c is FunctionCallContent),
                    () => currentSession?.Redactions.Count ?? 0,
                    cancellationToken);

                try
                {
                    await attempt.StartAsync().ConfigureAwait(false);
                    model = candidate;
                    transport = sendTools ? ToolTransport.Native : ToolTransport.None;
                    break;
                }
                catch (ToolsNotSupportedException ex)
                {
                    // The descriptor said native and the adapter or server said no: the model is downgraded for the rest of the
                    // process and this turn ends here, so the runtime re-sends it with the text protocol instead of a silent fallback.
                    attempt.Complete(OutboundOutcome.Failed, ex);
                    _inventory.DowngradeToolCalling(candidate.ProviderId, candidate.ModelId, ex.Message);
                    LogToolsDowngraded(_logger, request.RequestId, candidate.QualifiedId, ex.Message);
                    downgraded = (candidate, ex);
                    break;
                }
                catch (ProviderException ex) when (ex.Code != PillionErrorCode.ContextOverflow)
                {
                    attempt.Complete(OutboundOutcome.Failed, ex);
                    await attempt.DisposeAsync().ConfigureAwait(false);
                    attempt = null;
                    session?.Dispose();
                    session = null;
                    lastFailure = ex;
                    LogAttemptFailed(_logger, request.RequestId, candidate.QualifiedId, ex.Message);
                }
                catch (Exception ex)
                {
                    attempt.Complete(ex is OperationCanceledException ? OutboundOutcome.Cancelled : OutboundOutcome.Failed, ex);
                    throw ToRpcException(ex);
                }
            }

            if (downgraded is { } down)
            {
                yield return new ToolChatUpdate
                {
                    RequestId = request.RequestId,
                    ProviderId = down.Model.ProviderId,
                    ModelId = down.Model.ModelId,
                    Transport = ToolTransport.Downgraded,
                    IsFinal = true,
                    PromptTokens = promptTokens,
                    Error = new PillionError
                    {
                        Code = PillionErrorCode.ProviderUnavailable,
                        Message = down.Error.Message,
                        RetryHint = "Send the turn again with the text protocol; the model is now marked as having no native tool calling.",
                    },
                    ErrorIsRetryable = true,
                };
                yield break;
            }

            if (model is null || attempt is null)
            {
                throw ToRpcException(lastFailure ?? new ProviderException("No model provider could serve the request."));
            }

            var promptReported = 0;
            var completionReported = 0;
            ChatFinishReason? finish = null;
            PillionError? streamError = null;
            var retryable = false;
            var outcome = OutboundOutcome.Succeeded;
            Exception? failure = null;
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await attempt.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = attempt.Current;
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    attempt.Complete(OutboundOutcome.Cancelled, ex);
                    throw;
                }
                catch (Exception ex) when (ex is ProviderException or PillionRpcException)
                {
                    failure = ex;
                    outcome = OutboundOutcome.Failed;
                    streamError = ex is ProviderException p ? p.ToError() : ((PillionRpcException)ex).Error;
                    retryable = ex is ProviderException { IsRetryable: true };
                    break;
                }

                finish = update.FinishReason ?? finish;
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case UsageContent usage:
                            promptReported = (int)(usage.Details.InputTokenCount ?? promptReported);
                            completionReported = (int)(usage.Details.OutputTokenCount ?? completionReported);
                            break;

                        case TextContent { Text: { Length: > 0 } text }:
                            attempt.GeneratedCharacters += text.Length;
                            yield return new ToolChatUpdate { RequestId = request.RequestId, ProviderId = model.ProviderId, ModelId = model.ModelId, Transport = transport, Text = text };
                            break;

                        case FunctionCallContent call:
                            var bytes = ToolJson.ArgumentBytes(call);
                            attempt.ToolCalls.Add(call.Name);
                            attempt.ToolArgumentBytes += bytes;
                            attempt.GeneratedCharacters += bytes;
                            yield return new ToolChatUpdate { RequestId = request.RequestId, ProviderId = model.ProviderId, ModelId = model.ModelId, Transport = transport, ToolCall = call };
                            break;
                    }
                }
            }

            attempt.ReportedCompletionTokens = completionReported;
            attempt.Complete(outcome, failure);

            yield return new ToolChatUpdate
            {
                RequestId = request.RequestId,
                ProviderId = model.ProviderId,
                ModelId = model.ModelId,
                Transport = transport,
                IsFinal = true,
                FinishReason = finish,
                PromptTokens = promptReported > 0 ? promptReported : promptTokens,
                CompletionTokens = completionReported > 0 ? completionReported : (int)((attempt.GeneratedCharacters + 3) / 4),
                Error = streamError,
                ErrorIsRetryable = retryable,
            };
        }
        finally
        {
            if (attempt is not null)
            {
                await attempt.DisposeAsync().ConfigureAwait(false);
            }

            session?.Dispose();
        }
    }

    /// <summary>The model's tool-calling support: what the provider reported (with any run-time downgrade), else the catalog's word.</summary>
    private ToolCallingSupport ToolCallingOf(RoutedModel candidate) =>
        _inventory.Find(candidate.ProviderId, candidate.ModelId)?.Capabilities.ToolCalling
        ?? (_inventory.ToolCallingDowngradeReason(candidate.ProviderId, candidate.ModelId) is not null
            ? ToolCallingSupport.None
            : _catalog.GetCapabilities(candidate.ModelId, candidate.IsLocal).ToolCalling);

    /// <summary>
    /// Copies the conversation with every tool result capped at <see cref="ToolResults.MaxBytes"/>, and adds the chat system
    /// prompt when the runtime supplied none. Messages without results are shared, not copied.
    /// </summary>
    private List<AiChatMessage> CapResults(IReadOnlyList<AiChatMessage> messages, out int resultBytes)
    {
        resultBytes = 0;
        var result = new List<AiChatMessage>(messages.Count + 1);
        if (!messages.Any(m => m.Role == AiChatRole.System))
        {
            result.Add(new AiChatMessage(AiChatRole.System, _templates.ChatSystemPrompt));
        }

        foreach (var message in messages)
        {
            if (!message.Contents.Any(c => c is FunctionResultContent))
            {
                result.Add(message);
                continue;
            }

            var contents = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent functionResult)
                {
                    var capped = ToolResults.Cap(ToolResults.Text(functionResult.Result));
                    resultBytes += capped.Bytes;
                    if (capped.Truncated)
                    {
                        LogResultCapped(_logger, functionResult.CallId, capped.OriginalBytes, capped.Bytes);
                    }

                    contents.Add(new FunctionResultContent(functionResult.CallId, capped.Text));
                }
                else
                {
                    contents.Add(content);
                }
            }

            result.Add(new AiChatMessage(message.Role, contents) { AuthorName = message.AuthorName, MessageId = message.MessageId });
        }

        return result;
    }

    /// <summary>Estimates the prompt: every message's text, tool arguments and results, plus the tool declarations.</summary>
    private static int EstimateTokens(IReadOnlyList<AiChatMessage> messages, IReadOnlyList<AITool> tools)
    {
        var texts = messages.Select(m => string.Concat(m.Contents.Select(c => c switch
        {
            TextContent text => text.Text,
            FunctionCallContent call => ToolJson.ArgumentsJson(call),
            FunctionResultContent result => ToolResults.Text(result.Result),
            _ => string.Empty,
        })));
        var declarations = tools.OfType<AIFunctionDeclaration>().Select(t => t.Name + " " + t.Description + " " + (t.JsonSchema.ValueKind == System.Text.Json.JsonValueKind.Undefined ? string.Empty : t.JsonSchema.GetRawText()));
        return TokenEstimator.Estimate(texts.Concat(declarations));
    }

    [LoggerMessage(EventId = 435, Level = LogLevel.Warning, Message = "Request {RequestId}: {Model} refused tool definitions; its descriptor is downgraded to no tool calling for this session ({Reason})")]
    private static partial void LogToolsDowngraded(ILogger logger, string requestId, string model, string reason);

    [LoggerMessage(EventId = 436, Level = LogLevel.Information, Message = "Tool result {CallId} capped from {OriginalBytes} to {SentBytes} bytes before transport")]
    private static partial void LogResultCapped(ILogger logger, string callId, int originalBytes, int sentBytes);
}
