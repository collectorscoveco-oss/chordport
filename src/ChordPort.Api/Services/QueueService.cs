using ChordPort.Api.Domain;
namespace ChordPort.Api.Services;
public sealed class QueueService
{
    private readonly List<QueueRequest> _requests = [];
    public IReadOnlyList<QueueRequest> GetAll() => _requests;
    public QueueRequest Add(string songId)
    {
        var request = new QueueRequest(Guid.NewGuid().ToString("N"), songId, "Pending request", "Local catalog", DateTimeOffset.UtcNow);
        _requests.Add(request); return request;
    }
    public bool Remove(string id) => _requests.RemoveAll(item => item.Id == id) > 0;
}
