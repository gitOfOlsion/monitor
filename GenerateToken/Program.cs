// === REQUIRED USING DIRECTIVES ===
using System;
using System.Linq;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Net.NetworkInformation;
using Microsoft.IdentityModel.Tokens;

Console.WriteLine("=== Peripheral Monitor - JWT Token Generator ===\n");

// Get agent's MAC address
string macAddress = "UNKNOWN";
try
{
    var nic = NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up 
            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
    macAddress = nic?.GetPhysicalAddress().ToString() ?? "UNKNOWN";
}
catch { }

Console.WriteLine($"Machine Name: {Environment.MachineName}");
Console.WriteLine($"MAC Address:  {macAddress}\n");

// Generate JWT (MUST match server's key in Program.cs)
var key = Encoding.UTF8.GetBytes("PeripheralMonitor_SecureKey_32Bytes_32Bytes!!!");
var credentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);

var claims = new[]
{
    new Claim(JwtRegisteredClaimNames.Sub, Environment.MachineName),
    new Claim("mac", macAddress),
    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
    new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
};

var token = new JwtSecurityToken(
    issuer: "PeripheralMonitor",
    audience: "Agent",
    claims: claims,
    expires: DateTime.UtcNow.AddDays(30),
    signingCredentials: credentials
);

var jwt = new JwtSecurityTokenHandler().WriteToken(token);

Console.WriteLine("=== JWT TOKEN (copy this entire line) ===");
Console.WriteLine(jwt);
Console.WriteLine("=== END TOKEN ===\n");
Console.WriteLine("Next: Paste this token into appsettings.local.json as the JwtToken value");