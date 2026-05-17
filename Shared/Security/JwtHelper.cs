using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace PeripheralMonitor.Shared.Security;

public static class JwtHelper
{
    private static readonly string _issuer = "PeripheralMonitor";
    private static readonly string _audience = "Agent";
    // ⚠️ In production: store this in Azure Key Vault / environment variable
    private static readonly byte[] _key = Encoding.UTF8.GetBytes("PeripheralMonitor_SecureKey_32Bytes_32Bytes!!!");

    public static string GenerateToken(string machineName, string macAddress)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, machineName),
            new Claim("mac", macAddress),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _issuer,
                ValidAudience = _audience,
                IssuerSigningKey = new SymmetricSecurityKey(_key)
            };
            return tokenHandler.ValidateToken(token, validationParams, out _);
        }
        catch { return null; }
    }
}