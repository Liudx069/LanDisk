using System.Collections.Concurrent;
using LanDisk.Core.Security;
using LanDisk.Core.Storage;

namespace LanDisk.Server.Services;

/// <summary>
/// A prepared archive that the browser can fetch with a plain GET.
///
/// The selection is validated once, when the ticket is issued, and the resulting plan is stored
/// server side. The browser then downloads a URL, which means the archive streams straight to disk
/// instead of being buffered into a JavaScript Blob. Tickets are single use, short lived, and bound
/// to the device that created them, so possessing the URL alone is not enough to read anything.
/// </summary>
public sealed class BatchTicket
{
    public required string Id { get; init; }
    public required string OwnerId { get; init; }
    public required string ShareId { get; init; }
    public required string ArchiveName { get; init; }
    public required BatchZipPlan Plan { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.Now.AddMinutes(5);
    public bool Consumed { get; set; }
}

public sealed class BatchTicketStore
{
    private readonly ConcurrentDictionary<string, BatchTicket> _tickets = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxTickets = 64;

    public BatchTicket Issue(string ownerId, string shareId, string archiveName, BatchZipPlan plan)
    {
        Prune();

        var ticket = new BatchTicket
        {
            Id = SecretTokens.NewToken(),
            OwnerId = ownerId,
            ShareId = shareId,
            ArchiveName = archiveName,
            Plan = plan,
            ExpiresAt = DateTimeOffset.Now.Add(Lifetime)
        };

        _tickets[ticket.Id] = ticket;

        if (_tickets.Count > MaxTickets)
        {
            foreach (var pair in _tickets.OrderBy(p => p.Value.CreatedAt).Take(_tickets.Count - MaxTickets).ToList())
            {
                _tickets.TryRemove(pair.Key, out _);
            }
        }

        return ticket;
    }

    public BatchTicket? Take(string ticketId, string ownerId)
    {
        if (string.IsNullOrEmpty(ticketId)) return null;
        if (!_tickets.TryGetValue(ticketId, out BatchTicket? ticket)) return null;

        if (ticket.ExpiresAt < DateTimeOffset.Now || ticket.Consumed)
        {
            _tickets.TryRemove(ticketId, out _);
            return null;
        }

        if (!string.Equals(ticket.OwnerId, ownerId, StringComparison.Ordinal))
        {
            // A different device presenting someone else's ticket is refused without consuming it.
            return null;
        }

        ticket.Consumed = true;
        _tickets.TryRemove(ticketId, out _);
        return ticket;
    }

    private void Prune()
    {
        foreach (var pair in _tickets.ToList())
        {
            if (pair.Value.ExpiresAt < DateTimeOffset.Now) _tickets.TryRemove(pair.Key, out _);
        }
    }

    public int Count => _tickets.Count;
}
