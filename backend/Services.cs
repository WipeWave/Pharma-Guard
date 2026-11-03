using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace EPrescription.Api;

public class AuditService(AppDb db, IHttpContextAccessor http)
{
    public async Task LogAsync(Guid? userId, string action, Guid? rxId = null, string? detail = null)
    {
        db.AuditLogs.Add(new AuditLog {
            UserId = userId, Action = action, PrescriptionId = rxId, Detail = detail,
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "" });
        await db.SaveChangesAsync();
    }
}

/// <summary>Dual-layer check: (1) ECDSA signature + key status + freshness, (2) database state.</summary>
public class VerificationService(AppDb db, CryptoService crypto, IConfiguration cfg, AuditService audit)
{
    public async Task<VerifyResult> VerifyAsync(string qrData, Guid actorId)
    {
        var (r, id) = await CoreAsync(qrData);
        await audit.LogAsync(actorId, "VERIFY_" + r.Outcome.ToUpperInvariant(), id);
        return r;
    }

    public async Task<VerifyResult> DispenseAsync(string qrData, Guid pharmacistId)
    {
        var (r, id) = await CoreAsync(qrData);
        if (r.Outcome != "Valid") { await audit.LogAsync(pharmacistId, "DISPENSE_REJECTED_" + r.Outcome.ToUpperInvariant(), id); return r; }

        var now = DateTime.UtcNow;
        // Atomic conditional transition Active -> Completed (race-condition safe).
        var rows = await db.Prescriptions
            .Where(p => p.PrescriptionId == id && p.Status == RxStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, RxStatus.Completed)
                                      .SetProperty(p => p.DispensedByUserId, (Guid?)pharmacistId)
                                      .SetProperty(p => p.DispensedAt, (DateTime?)now));
        if (rows == 0)
        {
            var (again, _) = await CoreAsync(qrData);
            await audit.LogAsync(pharmacistId, "DISPENSE_REJECTED_REPLAY", id);
            return again;
        }
        await audit.LogAsync(pharmacistId, "PRESCRIPTION_DISPENSED", id);
        return r with { Outcome = "Dispensed", Message = "Dispensing recorded. This prescription is now permanently locked." };
    }

    async Task<(VerifyResult, Guid?)> CoreAsync(string qrData)
    {
        static (VerifyResult, Guid?) Fail(string o, string m, Guid? id = null) => (new VerifyResult(o, m), id);

        QrEnvelope? env; RxPayload? px;
        try { env = JsonSerializer.Deserialize<QrEnvelope>(qrData, Json.Opts); } catch { env = null; }
        if (env is null || string.IsNullOrEmpty(env.P) || string.IsNullOrEmpty(env.S) || string.IsNullOrEmpty(env.K))
            return Fail("Malformed", "This QR code is not a valid prescription.");
        try { px = JsonSerializer.Deserialize<RxPayload>(env.P, Json.Opts); } catch { px = null; }
        if (px is null || px.V != 1 || px.Id == Guid.Empty)
            return Fail("Malformed", "This QR code is not a valid prescription.");

        // Layer 1a: the public key must be one we issued to this doctor (defeats key-substitution).
        var key = await db.DoctorKeys.AsNoTracking().FirstOrDefaultAsync(k => k.PublicKey == env.K && k.DoctorUserId == px.DoctorId);
        if (key is null || !crypto.Verify(env.P, env.S, env.K))
            return Fail("InvalidSignature", "Signature verification failed. The prescription was altered or forged.");

        // Layer 1b: key / account still trusted?
        var doc = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == px.DoctorId);
        if (key.IsRevoked || doc is null || !doc.IsActive)
            return Fail("KeyRevoked", "The prescriber's signing key has been revoked.");

        // Layer 1c: freshness window (timestamp is covered by the signature).
        var days = cfg.GetValue("Rx:ValidityDays", 30);
        if (!DateTimeOffset.TryParse(px.IssuedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var issued)
            || issued > DateTimeOffset.UtcNow.AddMinutes(5))
            return Fail("Malformed", "The prescription timestamp is invalid.");
        if (DateTimeOffset.UtcNow > issued.AddDays(days))
            return Fail("Expired", $"This prescription is older than {days} days and has expired.", px.Id);

        // Layer 2: database state check.
        var rec = await db.Prescriptions.AsNoTracking().FirstOrDefaultAsync(p => p.PrescriptionId == px.Id);
        if (rec is null || rec.Signature != env.S)
            return Fail("NotFound", "No matching prescription exists in the registry.");

        var view = new RxView(px.Id, px.DoctorName, px.PatientName, px.PatientIc, px.DrugName, px.Dosage, px.Quantity, px.Notes, px.IssuedAt);
        if (rec.Status == RxStatus.Completed)
        {
            var who = rec.DispensedByUserId is { } pid ? (await db.Users.FindAsync(pid))?.FullName : null;
            return (new VerifyResult("ReplayDetected", "Replay attack detected. This prescription has already been dispensed.",
                null, new DispenseInfo(rec.DispensedAt, who)), px.Id);
        }
        return (new VerifyResult("Valid", "Signature valid and prescription is active.", view), px.Id);
    }
}
