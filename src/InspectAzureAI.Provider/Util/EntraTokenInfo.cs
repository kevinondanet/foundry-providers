using System.Text.Json;

namespace InspectAzureAI.Provider.Util;

/// <summary>
/// Diagnostic view of an Entra ID access token: the identity, tenant, audience and expiry read from the
/// JWT payload so a developer can confirm which sign-in (for example <c>az login</c>) produced it. The
/// signature is not validated and the token itself is never exposed. No Python counterpart.
/// </summary>
public sealed record EntraTokenInfo(
    string? Audience,
    string? TenantId,
    string? ObjectId,
    string? UserPrincipalName,
    string? AppId,
    string? Name,
    DateTimeOffset? ExpiresOn,
    IReadOnlyList<string> Scopes)
{
    /// <summary>Decodes the payload of a JWT access token; returns null for opaque or malformed tokens.</summary>
    public static EntraTokenInfo? TryParse(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? Claim(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            DateTimeOffset? expiresOn =
                root.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : null;

            return new EntraTokenInfo(
                Audience: Claim("aud"),
                TenantId: Claim("tid"),
                ObjectId: Claim("oid"),
                UserPrincipalName: Claim("upn") ?? Claim("preferred_username") ?? Claim("unique_name"),
                AppId: Claim("appid") ?? Claim("azp"),
                Name: Claim("name"),
                ExpiresOn: expiresOn,
                Scopes: Claim("scp")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }
}
