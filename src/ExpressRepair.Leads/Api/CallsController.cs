using ExpressRepair.Leads.Data;
using ExpressRepair.Leads.Domain;
using ExpressRepair.Leads.Processing;
using ExpressRepair.Leads.Realtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ExpressRepair.Leads.Api;

public sealed record CallDto(
    Guid Id,
    string From,
    string To,
    string? CustomerName,
    string? Appliance,
    string? Brand,
    string? Problem,
    string? Urgency,
    string? PreferredTime,
    string Status,
    string? Error,
    string? Transcript,
    string CallSid,
    string RecordingSid,
    string RecordingUrl,
    int? RecordingDurationSeconds,
    int Attempts,
    DateTime ReceivedAtUtc,
    DateTime? CompletedAtUtc)
{
    public static CallDto FromCall(Call c) => new(
        c.Id, c.From, c.To, c.CustomerName, c.Appliance, c.Brand, c.Problem, c.Urgency, c.PreferredTime,
        c.Status.ToString(), c.Error, c.Transcript, c.CallSid, c.RecordingSid, c.RecordingUrl,
        c.RecordingDurationSeconds, c.Attempts, c.ReceivedAtUtc, c.CompletedAtUtc);
}

public sealed record StatusCounts(int All, int Processing, int Done, int Failed);

public sealed record DashboardStats(int Total, int Processing, int Done, int Failed, int HighUrgency);

public sealed class CallTableQuery
{
    public string? Phone { get; set; }
    public string? Customer { get; set; }
    public string? Appliance { get; set; }
    public string? Brand { get; set; }
    public string? Problem { get; set; }
    public string? Urgency { get; set; }
    public DateTimeOffset? ReceivedFrom { get; set; }
    public DateTimeOffset? ReceivedTo { get; set; }
    public string SortBy { get; set; } = "received";
    public string SortDirection { get; set; } = "desc";
}

public sealed record CallPage(
    IReadOnlyList<CallDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    StatusCounts Counts,
    DashboardStats Stats);

/// <summary>Read/retry API used by the leads page (not called by the phone provider).</summary>
[ApiController]
[Route("api/calls")]
public sealed class CallsController(AppDbContext db, ICallQueue queue, ICallNotifier notifier) : ControllerBase
{
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 100;

    /// <summary>
    /// Paged, newest-first list. <paramref name="search"/> matches phone, name, appliance, brand and problem.
    /// <c>Counts</c> respect the search (for the status tabs); <c>Stats</c> are global (for the KPI cards).
    /// </summary>
    [HttpGet]
    public async Task<CallPage> List(
        [FromQuery] CallTableQuery filters,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] CallStatus? status = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        IQueryable<Call> query = db.Calls.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(c =>
                c.From.Contains(term) ||
                (c.CustomerName != null && c.CustomerName.ToLower().Contains(term)) ||
                (c.Appliance != null && c.Appliance.ToLower().Contains(term)) ||
                (c.Brand != null && c.Brand.ToLower().Contains(term)) ||
                (c.Problem != null && c.Problem.ToLower().Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(filters.Phone))
        {
            var phone = new string(filters.Phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
            query = query.Where(c => c.From.Contains(phone));
        }
        if (!string.IsNullOrWhiteSpace(filters.Customer))
        {
            var term = filters.Customer.Trim().ToLowerInvariant();
            query = query.Where(c => c.CustomerName != null && c.CustomerName.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(filters.Brand))
        {
            var term = filters.Brand.Trim().ToLowerInvariant();
            query = query.Where(c => c.Brand != null && c.Brand.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(filters.Problem))
        {
            var term = filters.Problem.Trim().ToLowerInvariant();
            query = query.Where(c => c.Problem != null && c.Problem.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(filters.Appliance))
            query = query.Where(c => c.Appliance == filters.Appliance);
        if (!string.IsNullOrWhiteSpace(filters.Urgency))
            query = query.Where(c => c.Urgency == filters.Urgency);
        if (filters.ReceivedFrom is { } from)
            query = query.Where(c => c.ReceivedAtUtc >= from.UtcDateTime);
        // Exclusive end: the browser sends midnight after the selected final day.
        if (filters.ReceivedTo is { } to)
            query = query.Where(c => c.ReceivedAtUtc < to.UtcDateTime);

        var counts = ToCounts(await CountByStatusAsync(query, ct));

        if (status is not null)
            query = query.Where(c => c.Status == status);

        var totalCount = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);

        var descending = filters.SortDirection.Equals("desc", StringComparison.OrdinalIgnoreCase);
        var ordered = filters.SortBy.ToLowerInvariant() switch
        {
            "date" => Order(query, c => c.ReceivedAtUtc, descending),
            "phone" => Order(query, c => c.From, descending),
            "customer" => Order(query, c => (c.CustomerName ?? "").ToLower(), descending),
            "appliance" => Order(query, c => c.Appliance ?? "", descending),
            "brand" => Order(query, c => (c.Brand ?? "").ToLower(), descending),
            "problem" => Order(query, c => (c.Problem ?? "").ToLower(), descending),
            "urgency" => Order(query, c => c.Urgency == "high" ? 3 : c.Urgency == "medium" ? 2 : c.Urgency == "low" ? 1 : 0, descending),
            "status" => Order(query, c => c.Status == CallStatus.Done ? "Done" : c.Status == CallStatus.Failed ? "Failed" : "Processing", descending),
            _ => Order(query, c => c.ReceivedAtUtc, descending)
        };
        var calls = await ordered
            .ThenByDescending(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var global = ToCounts(await CountByStatusAsync(db.Calls, ct));
        var highUrgency = await db.Calls.CountAsync(c => c.Status == CallStatus.Done && c.Urgency == "high", ct);

        return new CallPage(
            calls.Select(CallDto.FromCall).ToList(),
            page, pageSize, totalCount, totalPages,
            counts,
            new DashboardStats(global.All, global.Processing, global.Done, global.Failed, highUrgency));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CallDto>> Get(Guid id, CancellationToken ct)
    {
        var call = await db.Calls.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return call is null ? NotFound() : CallDto.FromCall(call);
    }

    /// <summary>Manual retry of a failed call: back to Processing and onto the queue.</summary>
    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var call = await db.Calls.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (call is null)
            return NotFound();
        if (call.Status != CallStatus.Failed)
            return Conflict(new { error = $"Only failed calls can be retried (this one is {call.Status})." });

        call.ResetForRetry();
        await db.SaveChangesAsync(ct);
        queue.Enqueue(call.Id);
        await notifier.CallChangedAsync(call);

        return Accepted(CallDto.FromCall(call));
    }

    private static Task<Dictionary<CallStatus, int>> CountByStatusAsync(IQueryable<Call> query, CancellationToken ct) =>
        query.GroupBy(c => c.Status).Select(g => new { g.Key, Count = g.Count() })
             .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private static IOrderedQueryable<Call> Order<T>(IQueryable<Call> query, Expression<Func<Call, T>> key, bool descending) =>
        descending ? query.OrderByDescending(key) : query.OrderBy(key);

    private static StatusCounts ToCounts(Dictionary<CallStatus, int> byStatus)
    {
        int Get(CallStatus s) => byStatus.GetValueOrDefault(s);
        return new StatusCounts(byStatus.Values.Sum(), Get(CallStatus.Processing), Get(CallStatus.Done), Get(CallStatus.Failed));
    }
}
