using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Plaza.PaymentProvider.Data;

namespace Plaza.PaymentProvider;

public sealed record ChargeRequest(string? Token, long AmountCents, string? Currency, string? Reference);

public sealed record ChargeResponse(
    Guid Id, string Status, string? DeclineReason, long AmountCents, string Currency, string Reference, int Attempts);

public static partial class ChargeEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    private const int MaxListed = 100;

    public static IEndpointRouteBuilder MapChargeEndpoints(this IEndpointRouteBuilder app)
    {
        var charges = app.MapGroup("/charges");
        charges.MapPost("/", ChargeAsync);
        charges.MapPut("/by-key/{key}/void", VoidAsync);
        charges.MapGet("/by-key/{key}", GetByKeyAsync);
        charges.MapGet("/", ListByReferenceAsync);
        return app;
    }

    /// <summary>
    /// Charges a token. The <c>Idempotency-Key</c> header is required: the first request with a
    /// key decides the outcome, and every later request with the same key gets that same
    /// outcome back without moving money again. The same key with a different request is 422.
    /// </summary>
    private static async Task<Results<Ok<ChargeResponse>, ValidationProblem, UnprocessableEntity<ProblemDetails>>> ChargeAsync(
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        ChargeRequest request,
        ProviderDbContext db,
        TimeProvider timeProvider,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var errors = Validate(idempotencyKey, request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var reference = request.Reference ?? "";
        var hash = Hash(request.Token!, request.AmountCents, request.Currency!, reference);
        var (status, declineReason) = ChargeSimulator.Decide(request.Token!, request.AmountCents);
        var now = timeProvider.GetUtcNow();

        var charge = new Charge
        {
            Id = Guid.CreateVersion7(now),
            IdempotencyKey = idempotencyKey!,
            RequestHash = hash,
            Reference = reference,
            // A refused non-positive amount is still recorded, as zero, so its key is spent.
            AmountCents = Math.Max(request.AmountCents, 0),
            Currency = request.Currency!,
            Status = status,
            DeclineReason = declineReason,
            Attempts = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (await TryInsertAsync(db, charge, cancellationToken))
        {
            return TypedResults.Ok(ToResponse(charge));
        }

        // The key was used before: answer with what happened then.
        await db.Charges
            .Where(c => c.IdempotencyKey == idempotencyKey)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1), cancellationToken);
        var existing = await db.Charges.AsNoTracking().SingleAsync(c => c.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existing.Status != ChargeStatus.Voided && existing.RequestHash != hash)
        {
            return TypedResults.UnprocessableEntity(new ProblemDetails
            {
                Title = "Idempotency key reused",
                Detail = "This Idempotency-Key was already used with a different request.",
                Status = StatusCodes.Status422UnprocessableEntity,
            });
        }

        http.Response.Headers[ReplayedHeader] = "true";
        return TypedResults.Ok(ToResponse(existing));
    }

    /// <summary>
    /// Cancels the charge made with a key, whatever its state: a successful charge is refunded,
    /// a declined one is left alone, and if no charge exists yet the key is voided so that a
    /// charge arriving later is refused. Safe to repeat.
    /// </summary>
    private static async Task<Results<Ok<ChargeResponse>, ValidationProblem>> VoidAsync(
        string key, ProviderDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!IdempotencyKeyPattern().IsMatch(key))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["key"] = [KeyError] });
        }

        var now = timeProvider.GetUtcNow();
        var tombstone = new Charge
        {
            Id = Guid.CreateVersion7(now),
            IdempotencyKey = key,
            RequestHash = "",
            Reference = "",
            AmountCents = 0,
            Currency = Charge.NoCurrency,
            Status = ChargeStatus.Voided,
            Attempts = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (await TryInsertAsync(db, tombstone, cancellationToken))
        {
            return TypedResults.Ok(ToResponse(tombstone));
        }

        // Conditional update: only a charge that took money can be refunded, and only once.
        await db.Charges
            .Where(c => c.IdempotencyKey == key && c.Status == ChargeStatus.Succeeded)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Status, ChargeStatus.Refunded).SetProperty(c => c.UpdatedAt, now),
                cancellationToken);

        var existing = await db.Charges.AsNoTracking().SingleAsync(c => c.IdempotencyKey == key, cancellationToken);
        return TypedResults.Ok(ToResponse(existing));
    }

    private static async Task<Results<Ok<ChargeResponse>, NotFound>> GetByKeyAsync(
        string key, ProviderDbContext db, CancellationToken cancellationToken)
    {
        var charge = await db.Charges.AsNoTracking().FirstOrDefaultAsync(c => c.IdempotencyKey == key, cancellationToken);
        return charge is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(charge));
    }

    /// <summary>Every charge made for a reference, across all idempotency keys.</summary>
    private static async Task<Results<Ok<List<ChargeResponse>>, ValidationProblem>> ListByReferenceAsync(
        string? reference, ProviderDbContext db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(reference) || reference.Length > MaxReferenceLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reference"] = [$"reference is required, at most {MaxReferenceLength} characters."],
            });
        }

        var charges = await db.Charges.AsNoTracking()
            .Where(c => c.Reference == reference)
            .OrderBy(c => c.CreatedAt)
            .Take(MaxListed)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(charges.ConvertAll(ToResponse));
    }

    private const int MaxTokenLength = 128;
    private const int MaxReferenceLength = 64;
    private const string KeyError = "Idempotency-Key is required: 1 to 128 characters from A-Z a-z 0-9 _ - : .";

    private static Dictionary<string, string[]> Validate(string? idempotencyKey, ChargeRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (idempotencyKey is null || !IdempotencyKeyPattern().IsMatch(idempotencyKey))
        {
            errors[IdempotencyKeyHeader] = [KeyError];
        }

        if (string.IsNullOrEmpty(request.Token) || request.Token.Length > MaxTokenLength)
        {
            errors["token"] = [$"token is required, at most {MaxTokenLength} characters."];
        }

        if (request.Currency is null || !CurrencyPattern().IsMatch(request.Currency))
        {
            errors["currency"] = ["currency must be a three-letter ISO 4217 code."];
        }

        if (request.Reference is { Length: > MaxReferenceLength })
        {
            errors["reference"] = [$"reference can have at most {MaxReferenceLength} characters."];
        }

        return errors;
    }

    /// <summary>Inserts the row unless its idempotency key already exists.</summary>
    private static async Task<bool> TryInsertAsync(ProviderDbContext db, Charge charge, CancellationToken cancellationToken)
    {
        // A repeated key is an expected case, not an error: ON CONFLICT keeps it out of the
        // error log and lets the unique index decide between concurrent requests.
        var status = charge.Status.ToString();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO charges (
                id, idempotency_key, request_hash, reference, amount_cents, currency,
                status, decline_reason, attempts, created_at, updated_at)
            VALUES (
                {charge.Id}, {charge.IdempotencyKey}, {charge.RequestHash}, {charge.Reference}, {charge.AmountCents}, {charge.Currency},
                {status}, {charge.DeclineReason}, {charge.Attempts}, {charge.CreatedAt}, {charge.UpdatedAt})
            ON CONFLICT (idempotency_key) DO NOTHING
            """, cancellationToken);
        return inserted == 1;
    }

    /// <summary>Fingerprint of a request. The token itself is never stored.</summary>
    private static string Hash(string token, long amountCents, string currency, string reference) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{token}\n{amountCents}\n{currency}\n{reference}")));

    private static ChargeResponse ToResponse(Charge charge) => new(
        charge.Id,
        charge.Status.ToString(),
        charge.DeclineReason,
        charge.AmountCents,
        charge.Currency,
        charge.Reference,
        charge.Attempts);

    [GeneratedRegex(@"\A[A-Za-z0-9_\-:.]{1,128}\z")]
    private static partial Regex IdempotencyKeyPattern();

    [GeneratedRegex(@"\A[A-Z]{3}\z")]
    private static partial Regex CurrencyPattern();
}
