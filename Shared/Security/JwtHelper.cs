using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace PeripheralMonitor.Shared.Security;

public static class JwtHelper
{
    private const string Issuer   = "PeripheralMonitor";
    private const string Audience = "Agent";

    public static string GenerateAgentToken(
        string machineName, string macAddress, Guid institutionId,
        string secretKey,   int expiryDays = 365)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, machineName),
            new Claim("mac",            macAddress),
            new Claim("institution_id", institutionId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };
        var token = new JwtSecurityToken(
            issuer: Issuer, audience: Audience, claims: claims,
            expires: DateTime.UtcNow.AddDays(expiryDays),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static ClaimsPrincipal? ValidateToken(string token, string secretKey)
    {
        try
        {
            return new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true,
                ValidateLifetime = true, ValidateIssuerSigningKey = true,
                ValidIssuer = Issuer, ValidAudience = Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey))
            }, out _);
        }
        catch { return null; }
    }

    public static Guid? GetInstitutionId(ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue("institution_id"), out var id) ? id : null;
}
