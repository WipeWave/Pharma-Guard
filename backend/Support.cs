using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace EPrescription.Api;

public static class Json
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}

public static class Val
{
    /// <summary>Returns the first validation error, or null when the DTO is valid.</summary>
    public static string? Check(object dto)
    {
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(dto, new ValidationContext(dto), results, true) ? null : results[0].ErrorMessage;
    }
}

public static class Ext
{
    public static Guid Id(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public static class Tokens
{
    public static string Issue(User u, SecurityKey key, IConfiguration cfg)
    {
        var token = new JwtSecurityToken("eprescription", "eprescription-web",
            new[] {
                new Claim(ClaimTypes.NameIdentifier, u.UserId.ToString()),
                new Claim(ClaimTypes.Name, u.FullName),
                new Claim(ClaimTypes.Role, u.Role.ToString()) },
            expires: DateTime.UtcNow.AddMinutes(cfg.GetValue("Jwt:Minutes", 60)),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
