using System.Security.Claims;
using Gateway.Api.Domain;
using Gateway.Api.Persistence;
using Gateway.Api.Quota;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Api.Admin;

public sealed record GrantTokensRequest(long Tokens, string? Note, string? IdempotencyKey);
public sealed record SetTokensRequest(long Tokens, string? Note);

/// <summary>
/// Prepaid token allowances per (user, workspace). Grant adds to the balance (and
/// starts enforcement if it was unlimited), Set overwrites it, Delete returns the user
/// to unlimited (rolling windows still apply). Every change is a ledger row.
/// </summary>
[ApiController]
[Route("api/v1/admin/users/{id:guid}/balance")]
[Authorize(Policy = "OrgAdmin")]
public sealed class AdminBalanceController : ControllerBase
{
    private const long MaxTokens = 1_000_000_000_000; // 1T — sanity bound, not a business limit
    private const int MaxNote = 256;

    private readonly GatewayDbContext _db;
    private readonly ITokenBalanceService _balances;

    public AdminBalanceController(GatewayDbContext db, ITokenBalanceService balances)
    {
        _db = db; _balances = balances;
    }

    private Guid OrgId => Guid.Parse(User.FindFirstValue("org_id")!);
    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/v1/admin/users/{id}/balance[?workspaceId=&limit=]
    [HttpGet]
    public async Task<IActionResult> Get(Guid id, [FromQuery] Guid? workspaceId, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var (membership, error) = await MembershipLookup.ResolveAsync(_db, id, workspaceId, ct);
        if (error is not null) return error;

        var history = await _balances.HistoryAsync(OrgId, membership!.Id, limit, ct);
        return Ok(Shape(membership, membership.TokenBalance, history));
    }

    // POST /api/v1/admin/users/{id}/balance/grant
    [HttpPost("grant")]
    public async Task<IActionResult> Grant(Guid id, [FromQuery] Guid? workspaceId, [FromBody] GrantTokensRequest req, CancellationToken ct)
    {
        if (req.Tokens is <= 0 or > MaxTokens)
            return Invalid("tokens must be between 1 and 1,000,000,000,000.", "invalid_tokens");
        if (Validate(req.Note, req.IdempotencyKey) is { } bad) return bad;

        var (membership, error) = await MembershipLookup.ResolveAsync(_db, id, workspaceId, ct);
        if (error is not null) return error;

        var (balance, entry) = await _balances.GrantAsync(OrgId, membership!, req.Tokens, ActorId, req.Note, req.IdempotencyKey, ct);
        await Audit("tokens_granted", $"targetUser={id} workspace={membership!.WorkspaceId} tokens={req.Tokens} balance={balance}", ct);
        return Ok(Shape(membership, balance, new[] { entry }));
    }

    // PUT /api/v1/admin/users/{id}/balance
    [HttpPut]
    public async Task<IActionResult> Set(Guid id, [FromQuery] Guid? workspaceId, [FromBody] SetTokensRequest req, CancellationToken ct)
    {
        if (req.Tokens is < 0 or > MaxTokens)
            return Invalid("tokens must be between 0 and 1,000,000,000,000.", "invalid_tokens");
        if (Validate(req.Note, null) is { } bad) return bad;

        var (membership, error) = await MembershipLookup.ResolveAsync(_db, id, workspaceId, ct);
        if (error is not null) return error;

        var (balance, entry) = await _balances.SetAsync(OrgId, membership!, req.Tokens, ActorId, req.Note, ct);
        await Audit("tokens_set", $"targetUser={id} workspace={membership!.WorkspaceId} tokens={req.Tokens}", ct);
        return Ok(Shape(membership, balance, new[] { entry }));
    }

    // DELETE /api/v1/admin/users/{id}/balance  -> unlimited (windows still apply)
    [HttpDelete]
    public async Task<IActionResult> Revoke(Guid id, [FromQuery] Guid? workspaceId, CancellationToken ct)
    {
        var (membership, error) = await MembershipLookup.ResolveAsync(_db, id, workspaceId, ct);
        if (error is not null) return error;

        var (balance, entry) = await _balances.RevokeAsync(OrgId, membership!, ActorId, null, ct);
        await Audit("tokens_revoked", $"targetUser={id} workspace={membership!.WorkspaceId}", ct);
        return Ok(Shape(membership, balance, new[] { entry }));
    }

    private static object Shape(Membership m, long? balance, IReadOnlyList<LedgerRow> history) => new
    {
        userId = m.UserId,
        workspaceId = m.WorkspaceId,
        membershipId = m.Id,
        enforced = balance is not null,
        balance,
        history
    };

    private IActionResult? Validate(string? note, string? idempotencyKey)
    {
        if (note is { Length: > MaxNote }) return Invalid($"note must be at most {MaxNote} characters.", "invalid_note");
        if (idempotencyKey is { Length: 0 or > 128 }) return Invalid("idempotencyKey must be 1-128 characters.", "invalid_idempotency_key");
        return null;
    }

    private IActionResult Invalid(string message, string code) => BadRequest(new { error = new { code, message } });

    private async Task Audit(string action, string detail, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = OrgId, UserId = ActorId, Action = action, Detail = detail,
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
        await _db.SaveChangesAsync(ct);
    }
}
