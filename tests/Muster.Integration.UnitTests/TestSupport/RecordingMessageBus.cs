using Wolverine;

namespace Muster.IntegrationTests.TestSupport;

/// <summary>
/// A no-op <see cref="IMessageBus"/> for unit-constructing services that publish Wolverine messages.
/// Records what was published so a test can assert on it; everything else is inert.
/// </summary>
internal sealed class RecordingMessageBus : IMessageBus
{
    public List<object> Published { get; } = [];

    public string? TenantId { get; set; }

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message is not null)
        {
            Published.Add(message);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null) => ValueTask.CompletedTask;
    public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null) => ValueTask.CompletedTask;
    public ValueTask SendToEndpointAsync<T>(string endpointName, T message, DeliveryOptions? options = null) => ValueTask.CompletedTask;

    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => Task.CompletedTask;
    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => Task.FromResult(default(T)!);
    public Task InvokeAsync(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => Task.CompletedTask;
    public Task<T> InvokeAsync<T>(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) => Task.FromResult(default(T)!);
    public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => Task.CompletedTask;
    public Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null) => Task.FromResult(default(T)!);

    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, CancellationToken cancellation = default) => Empty<TResponse>();
    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, DeliveryOptions options, CancellationToken cancellation = default) => Empty<TResponse>();

    public IDestinationEndpoint EndpointFor(string endpointName) => throw new NotSupportedException();
    public IDestinationEndpoint EndpointFor(Uri uri) => throw new NotSupportedException();
    public IReadOnlyList<Envelope> PreviewSubscriptions(object message) => [];
    public IReadOnlyList<Envelope> PreviewSubscriptions(object message, DeliveryOptions options) => [];

    private static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
