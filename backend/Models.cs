using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace EPrescription.Api;

public enum Role { Doctor, Pharmacist, Administrator }
public enum RxStatus { Active, Completed }   // one-directional state machine (SR-04)

public class User
{
    public Guid UserId { get; set; } = Guid.NewGuid();
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public Role Role { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DoctorKey
{
    public Guid KeyId { get; set; } = Guid.NewGuid();
    public Guid DoctorUserId { get; set; }
    public string PublicKey { get; set; } = "";          // base64 SubjectPublicKeyInfo (embedded in QR)
    public string PublicKeyPem { get; set; } = "";
    public string EncryptedPrivateKey { get; set; } = ""; // AES-256-GCM, master key from environment (SR-02)
    public bool IsRevoked { get; set; }
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
}

public class Prescription
{
    public Guid PrescriptionId { get; set; }
    public Guid DoctorUserId { get; set; }
    public string PatientName { get; set; } = "";
    public string PatientIC { get; set; } = "";
    public string DrugName { get; set; } = "";
    public string Dosage { get; set; } = "";
    public int Quantity { get; set; }
    public string? Notes { get; set; }
    public DateTime IssuanceTimestamp { get; set; }
    public string PayloadJson { get; set; } = "";   // exact string that was signed
    public string Signature { get; set; } = "";     // base64 of r||s (P1363)
    public RxStatus Status { get; set; } = RxStatus.Active;
    public Guid? DispensedByUserId { get; set; }
    public DateTime? DispensedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public long LogId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public Guid? PrescriptionId { get; set; }
    public string IpAddress { get; set; } = "";
    public string? Detail { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// ---- Signed payload (serialized once; the exact string is what gets signed) ----
public record RxPayload(int V, Guid Id, Guid DoctorId, string DoctorName, string PatientName, string PatientIc,
    string DrugName, string Dosage, int Quantity, string? Notes, string IssuedAt, long Ts);
public record QrEnvelope(string P, string S, string K); // payload, signature, public key

// ---- DTOs (SR-05: server-side validation + allow-list for free text) ----
public class SafeTextAttribute : ValidationAttribute
{
    static readonly Regex Allowed = new(@"^[\p{L}\p{N} .,'()/#%+\-:;]*\z", RegexOptions.Compiled);
    protected override ValidationResult? IsValid(object? value, ValidationContext ctx) =>
        value is null || Allowed.IsMatch((string)value)
            ? ValidationResult.Success
            : new ValidationResult($"{ctx.DisplayName} contains disallowed characters.");
}
public class LoginDto
{
    [Required, EmailAddress, StringLength(200)] public string Email { get; set; } = "";
    [Required, StringLength(200)] public string Password { get; set; } = "";
}
public class CreateRxDto
{
    [Required, StringLength(120, MinimumLength = 2), SafeText] public string PatientName { get; set; } = "";
    [Required, StringLength(20, MinimumLength = 6), SafeText] public string PatientIc { get; set; } = "";
    [Required, StringLength(120, MinimumLength = 2), SafeText] public string DrugName { get; set; } = "";
    [Required, StringLength(80, MinimumLength = 1), SafeText] public string Dosage { get; set; } = "";
    [Range(1, 1000)] public int Quantity { get; set; }
    [StringLength(300), SafeText] public string? Notes { get; set; }
}
public class QrDto { [Required, StringLength(4000)] public string QrData { get; set; } = ""; }
public class CreateUserDto
{
    [Required, StringLength(120, MinimumLength = 2), SafeText] public string FullName { get; set; } = "";
    [Required, EmailAddress, StringLength(200)] public string Email { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 12)] public string Password { get; set; } = "";
    [Required] public string Role { get; set; } = "";
}

public record RxView(Guid Id, string DoctorName, string PatientName, string PatientIc, string DrugName,
    string Dosage, int Quantity, string? Notes, string IssuedAt);
public record DispenseInfo(DateTime? DispensedAt, string? Pharmacist);
public record VerifyResult(string Outcome, string Message, RxView? Prescription = null, DispenseInfo? Dispensed = null);
