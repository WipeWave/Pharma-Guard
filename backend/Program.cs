using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using EPrescription.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

var jwtSecret = cfg["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.Length < 32)
    throw new InvalidOperationException("Jwt:Secret must be set (>=32 chars) via environment variable Jwt__Secret.");
var jwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 64 * 1024; k.AddServerHeader = false; });
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite(cfg.GetConnectionString("Default") ?? "Data Source=eprescription.db"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<CryptoService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<VerificationService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = "eprescription",
        ValidateAudience = true, ValidAudience = "eprescription-web",
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
        IssuerSigningKey = jwtKey, ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }
    };
    // Revocation takes effect immediately: every request re-checks the account is still active.
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
            var id = ctx.Principal!.Id();
            if (!await db.Users.AnyAsync(u => u.UserId == id && u.IsActive)) ctx.Fail("Account disabled");
        }
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Doctor", p => p.RequireRole("Doctor"))
    .AddPolicy("Pharmacist", p => p.RequireRole("Pharmacist"))
    .AddPolicy("Administrator", p => p.RequireRole("Administrator"));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "x",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
using (var scope = app.Services.CreateScope()) await Seeder.RunAsync(scope.ServiceProvider, cfg);

app.UseExceptionHandler(a => a.Run(async c =>
{
    c.Response.StatusCode = 500;
    await c.Response.WriteAsJsonAsync(new { error = "Internal server error." });
}));
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var dummyHash = BCrypt.Net.BCrypt.HashPassword("timing-equalizer", 12);
var api = app.MapGroup("/api");

// ---------- Auth (FR-01) ----------
api.MapPost("/auth/login", async (LoginDto d, AppDb db, AuditService audit) =>
{
    if (Val.Check(d) is { } err) return Results.BadRequest(new { error = err });
    var email = d.Email.Trim().ToLowerInvariant();
    var u = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
    var ok = BCrypt.Net.BCrypt.Verify(d.Password, u?.PasswordHash ?? dummyHash) && u is { IsActive: true };
    if (!ok)
    {
        await audit.LogAsync(u?.UserId, "LOGIN_FAILED", null, email);
        return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);
    }
    await audit.LogAsync(u!.UserId, "LOGIN_OK");
    return Results.Ok(new { token = Tokens.Issue(u, jwtKey, cfg), fullName = u.FullName, role = u.Role.ToString() });
}).RequireRateLimiting("login");

api.MapGet("/auth/me", async (ClaimsPrincipal p, AppDb db) =>
{
    var id = p.Id();
    var u = await db.Users.FindAsync(id);
    var keyActive = u!.Role == Role.Doctor && await db.DoctorKeys.AnyAsync(k => k.DoctorUserId == id && !k.IsRevoked);
    return Results.Ok(new { fullName = u.FullName, role = u.Role.ToString(), keyActive });
}).RequireAuthorization();

// ---------- Doctor: create + sign (FR-02, FR-03) ----------
api.MapPost("/prescriptions", async (CreateRxDto d, ClaimsPrincipal p, AppDb db, CryptoService crypto, AuditService audit) =>
{
    if (Val.Check(d) is { } err) return Results.BadRequest(new { error = err });
    var docId = p.Id();
    var doc = await db.Users.FindAsync(docId);
    var key = await db.DoctorKeys.Where(k => k.DoctorUserId == docId && !k.IsRevoked)
                                 .OrderByDescending(k => k.IssuedAt).FirstOrDefaultAsync();
    if (key is null)
    {
        await audit.LogAsync(docId, "ISSUE_DENIED_NO_ACTIVE_KEY");
        return Results.Json(new { error = "Your signing key is missing or revoked. Contact the administrator." }, statusCode: 403);
    }

    // UUID + timestamp are generated here, never by the client (SR-03), and are covered by the signature.
    var now = DateTime.UtcNow;
    var id = Guid.NewGuid();
    var payload = new RxPayload(1, id, docId, doc!.FullName, d.PatientName.Trim(), d.PatientIc.Trim(), d.DrugName.Trim(),
        d.Dosage.Trim(), d.Quantity, string.IsNullOrWhiteSpace(d.Notes) ? null : d.Notes.Trim(),
        now.ToString("O"), new DateTimeOffset(now).ToUnixTimeSeconds());
    var json = JsonSerializer.Serialize(payload, Json.Opts);   // the exact string is signed AND transported
    var sig = crypto.Sign(json, key.EncryptedPrivateKey);

    db.Prescriptions.Add(new Prescription {
        PrescriptionId = id, DoctorUserId = docId, PatientName = payload.PatientName, PatientIC = payload.PatientIc,
        DrugName = payload.DrugName, Dosage = payload.Dosage, Quantity = payload.Quantity, Notes = payload.Notes,
        IssuanceTimestamp = now, PayloadJson = json, Signature = sig, Status = RxStatus.Active });
    await db.SaveChangesAsync();
    await audit.LogAsync(docId, "PRESCRIPTION_ISSUED", id);

    var qr = crypto.SealQr(JsonSerializer.Serialize(new QrEnvelope(json, sig, key.PublicKey), Json.Opts));
    return Results.Ok(new { prescriptionId = id, issuedAt = now, qrData = qr });
}).RequireAuthorization("Doctor");

api.MapGet("/prescriptions/mine", async (ClaimsPrincipal p, AppDb db) =>
{
    var id = p.Id();
    return Results.Ok(await db.Prescriptions.AsNoTracking().Where(x => x.DoctorUserId == id)
        .OrderByDescending(x => x.IssuanceTimestamp).Take(50)
        .Select(x => new { id = x.PrescriptionId, x.PatientName, x.DrugName, x.Quantity, status = x.Status.ToString(), issuedAt = x.IssuanceTimestamp })
        .ToListAsync());
}).RequireAuthorization("Doctor");

// ---------- Pharmacist: verify + dispense (FR-04, FR-05, FR-06) ----------
api.MapPost("/verify", async (QrDto d, ClaimsPrincipal p, VerificationService v) =>
    Val.Check(d) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok(await v.VerifyAsync(d.QrData, p.Id())))
    .RequireAuthorization("Pharmacist");

api.MapPost("/prescriptions/dispense", async (QrDto d, ClaimsPrincipal p, VerificationService v) =>
    Val.Check(d) is { } err ? Results.BadRequest(new { error = err }) : Results.Ok(await v.DispenseAsync(d.QrData, p.Id())))
    .RequireAuthorization("Pharmacist");

// ---------- Administrator: users + keys (FR-07). No prescription endpoints exist for this role. ----------
var admin = api.MapGroup("/admin").RequireAuthorization("Administrator");

admin.MapGet("/users", async (AppDb db) =>
{
    var users = await db.Users.AsNoTracking().Where(u => u.Role != Role.Administrator).OrderBy(u => u.FullName).ToListAsync();
    var keys = (await db.DoctorKeys.AsNoTracking().ToListAsync()).GroupBy(k => k.DoctorUserId)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(k => k.IssuedAt).First());
    return Results.Ok(users.Select(u => new {
        id = u.UserId, u.FullName, u.Email, role = u.Role.ToString(), u.IsActive, u.CreatedAt,
        keyFingerprint = keys.TryGetValue(u.UserId, out var k) ? Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(k.PublicKey)))[..16] : null,
        keyRevoked = keys.TryGetValue(u.UserId, out var k2) ? k2.IsRevoked : (bool?)null }));
});

admin.MapPost("/users", async (CreateUserDto d, ClaimsPrincipal p, AppDb db, CryptoService crypto, AuditService audit) =>
{
    if (Val.Check(d) is { } err) return Results.BadRequest(new { error = err });
    if (!Enum.TryParse<Role>(d.Role, out var role) || role == Role.Administrator)
        return Results.BadRequest(new { error = "Role must be Doctor or Pharmacist." });
    var email = d.Email.Trim().ToLowerInvariant();
    if (await db.Users.AnyAsync(u => u.Email == email)) return Results.Json(new { error = "Email already registered." }, statusCode: 409);

    var user = Seeder.NewUser(d.FullName.Trim(), email, d.Password, role);
    db.Users.Add(user);
    if (role == Role.Doctor)
    {
        var (pub, pem, enc) = crypto.GenerateKeyPair();   // P-256 pair generated server-side; private key stored encrypted
        db.DoctorKeys.Add(new DoctorKey { DoctorUserId = user.UserId, PublicKey = pub, PublicKeyPem = pem, EncryptedPrivateKey = enc });
    }
    await db.SaveChangesAsync();
    await audit.LogAsync(p.Id(), "USER_REGISTERED", null, $"{role}:{email}");
    return Results.Ok(new { id = user.UserId });
});

admin.MapPost("/users/{id:guid}/revoke", async (Guid id, ClaimsPrincipal p, AppDb db, AuditService audit) =>
{
    var u = await db.Users.FindAsync(id);
    if (u is null || u.Role == Role.Administrator) return Results.NotFound(new { error = "User not found." });
    u.IsActive = false;
    foreach (var k in await db.DoctorKeys.Where(k => k.DoctorUserId == id).ToListAsync()) k.IsRevoked = true;
    await db.SaveChangesAsync();
    await audit.LogAsync(p.Id(), "ACCESS_REVOKED", null, u.Email);
    return Results.Ok(new { revoked = true });
});

admin.MapGet("/audit", async (AppDb db) =>
{
    var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.UserId, u => u.FullName);
    var rows = await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.LogId).Take(200).ToListAsync();
    return Results.Ok(rows.Select(a => new {
        a.LogId, a.Timestamp, user = a.UserId is { } id && names.TryGetValue(id, out var n) ? n : "-",
        a.Action, a.PrescriptionId, a.IpAddress, a.Detail }));
});

app.Run();
