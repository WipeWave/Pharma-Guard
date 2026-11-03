using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EPrescription.Api;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<DoctorKey> DoctorKeys => Set<DoctorKey>();
    public DbSet<Prescription> Prescriptions => Set<Prescription>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<DateTime>().HaveConversion<UtcConv>();
        b.Properties<DateTime?>().HaveConversion<UtcNullConv>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e => {
            e.HasKey(x => x.UserId);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Role).HasConversion<string>();
        });
        b.Entity<DoctorKey>(e => {
            e.HasKey(x => x.KeyId);
            e.HasIndex(x => x.PublicKey).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.DoctorUserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Prescription>(e => {
            e.HasKey(x => x.PrescriptionId);
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => x.DoctorUserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.DoctorUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.DispensedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AuditLog>(e => { e.HasKey(x => x.LogId); e.HasIndex(x => x.Timestamp); });
    }
}

public class UtcConv() : ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
public class UtcNullConv() : ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

public static class Seeder
{
    public static async Task RunAsync(IServiceProvider sp, IConfiguration cfg)
    {
        var db = sp.GetRequiredService<AppDb>();
        var crypto = sp.GetRequiredService<CryptoService>();
        await db.Database.EnsureCreatedAsync();

        // Database-level guarantee for SR-04: a Completed prescription can never change state again.
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TRIGGER IF NOT EXISTS trg_rx_status_immutable
            BEFORE UPDATE OF Status ON Prescriptions
            WHEN OLD.Status = 'Completed' AND NEW.Status <> 'Completed'
            BEGIN SELECT RAISE(ABORT, 'Completed prescriptions are immutable'); END;");

        if (await db.Users.AnyAsync()) return;
        var pw = cfg["Admin:Password"];
        if (string.IsNullOrEmpty(pw) || pw.Length < 12)
            throw new InvalidOperationException("Set Admin__Password (>=12 chars) for first-run admin creation.");
        db.Users.Add(NewUser("System Administrator", cfg["Admin:Email"] ?? "admin@eprescription.local", pw, Role.Administrator));

        if (cfg.GetValue<bool>("Seed:Demo"))
        {
            const string demo = "Demo@Password123";
            var dr = NewUser("Dr. Aisha Rahman", "doctor@demo.local", demo, Role.Doctor);
            db.Users.AddRange(dr, NewUser("Pharmacist A", "pharmacist.a@demo.local", demo, Role.Pharmacist),
                                  NewUser("Pharmacist B", "pharmacist.b@demo.local", demo, Role.Pharmacist));
            var (pub, pem, enc) = crypto.GenerateKeyPair();
            db.DoctorKeys.Add(new DoctorKey { DoctorUserId = dr.UserId, PublicKey = pub, PublicKeyPem = pem, EncryptedPrivateKey = enc });
        }
        await db.SaveChangesAsync();
    }

    public static User NewUser(string name, string email, string password, Role role) => new()
    {
        FullName = name, Email = email.Trim().ToLowerInvariant(), Role = role,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, 12)
    };
}
