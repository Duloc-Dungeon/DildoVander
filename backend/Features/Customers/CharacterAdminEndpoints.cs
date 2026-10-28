using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Kufr.Backend.Features.Customers;

internal static class CharacterAdminEndpoints
{
    internal const int MaximumUploadBytes = 8 * 1024 * 1024;
    internal sealed record PortraitUploadMetadata;

    internal static void MapAdminCharacters(this WebApplication app)
    {
        var group = app.MapGroup("/app/v1/admin/characters").RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            http.Response.Headers.CacheControl = "private, no-store";
            var error = await AuthorizeAsync(http);
            if (error is not null) { return error; }
            try { return await next(invocation); }
            catch (CharacterException exception) { return ApiProblem.Create(http, exception.Status, exception.Code); }
        });
        group.MapGet("", (string? after, CharacterProfileStore store) => Results.Ok(store.ReadAdminCharacters(after)));
        group.MapGet("/{profileId}", (string profileId, CharacterProfileStore store) =>
            Results.Ok(store.ReadAdminCharacter(profileId) ?? throw new CharacterException("character_not_found", 404)));
        group.MapPut("/{profileId}", (string profileId, AdminCharacterWriteRequest request, CharacterProfileStore store) =>
            Results.Ok(store.EditAdminCharacter(profileId, request)));
        group.MapGet("/{profileId}/portrait", (string profileId, long revision, CharacterProfileStore store) =>
            Results.File(store.ReadAdminPortrait(profileId, revision) ?? throw new CharacterException("character_portrait_not_found", 404), "image/webp"));
        group.MapPut("/{profileId}/portrait", async (string profileId, long expectedRevision, HttpContext http, CharacterProfileStore store) =>
        {
            var image = await ReadPortraitUploadAsync(http.Request, http.RequestAborted);
            return Results.Ok(store.ReplaceAdminPortrait(profileId, expectedRevision, image));
        }).WithMetadata(new PortraitUploadMetadata());
    }

    internal static async Task<IResult?> AuthorizeAsync(HttpContext http)
    {
        var accountId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (accountId is null || !http.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim))
        {
            return ApiProblem.Create(http, 401, "account_required");
        }
        var database = http.RequestServices.GetRequiredService<AccountDatabase>();
        if (!await database.Users.AsNoTracking().AnyAsync(user => user.Id == accountId && !user.Disabled, http.RequestAborted))
        {
            return ApiProblem.Create(http, 401, "account_required");
        }
        if (!AccountRoles.IsAdmin(http.User)) { return ApiProblem.Create(http, 403, "account_admin_required"); }
        if (HttpMethods.IsPut(http.Request.Method))
        {
            var recent = await AccountAuthorization.RequireRecentAsync(http);
            if (recent is not null) { return recent; }
            if (http.Request.Headers["X-DV-Confirm"] != "yes")
            {
                return ApiProblem.Create(http, 409, "confirmation_required");
            }
        }
        return null;
    }

    internal static async Task<byte[]> ReadPortraitUploadAsync(HttpRequest request, CancellationToken token)
    {
        var contentType = request.ContentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (contentType is not ("image/png" or "image/jpeg" or "image/webp"))
        {
            throw new CharacterException("invalid_character_portrait", 415);
        }
        if (request.ContentLength > MaximumUploadBytes) { throw new CharacterException("character_portrait_too_large", 413); }
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var count = await request.Body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaximumUploadBytes + 1L - buffer.Length)), token);
            if (count == 0) { break; }
            buffer.Write(chunk, 0, count);
            if (buffer.Length > MaximumUploadBytes) { throw new CharacterException("character_portrait_too_large", 413); }
        }
        return NormalizePortrait(buffer.ToArray(), contentType);
    }

    internal static byte[] NormalizePortrait(byte[] bytes, string contentType)
    {
        if (bytes.Length > MaximumUploadBytes) { throw new CharacterException("character_portrait_too_large", 413); }
        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            var expectedFormat = contentType switch
            {
                "image/png" => SKEncodedImageFormat.Png,
                "image/jpeg" => SKEncodedImageFormat.Jpeg,
                "image/webp" => SKEncodedImageFormat.Webp,
                _ => throw new CharacterException("invalid_character_portrait", 415)
            };
            if (codec is null || codec.EncodedFormat != expectedFormat || codec.FrameCount > 1
                || codec.Info.Width is < 1 or > 4096 || codec.Info.Height is < 1 or > 4096
                || (long)codec.Info.Width * codec.Info.Height > 16_000_000)
            {
                throw new CharacterException("invalid_character_portrait");
            }
            using var bitmap = new SKBitmap(codec.Info);
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            {
                throw new CharacterException("invalid_character_portrait");
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, 86);
            if (encoded is null || encoded.Size > MaximumUploadBytes) { throw new CharacterException("invalid_character_portrait"); }
            return encoded.ToArray();
        }
        catch (ArgumentException) { throw new CharacterException("invalid_character_portrait"); }
    }
}
