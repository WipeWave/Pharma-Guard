# MedSign – Secure Medical E-Prescription System (ECDSA + Replay Mitigation)

React (Vite) + ASP.NET Core 8 minimal API + EF Core (SQLite), ECDSA P-256 / SHA-256 via `System.Security.Cryptography.ECDsa`.

## Deploy on Ubuntu Server (VM)
    unzip eprescription.zip && cd eprescription
    bash deploy/setup.sh
The script installs .NET 8 / Node 20 / nginx, builds both halves, creates a systemd service, a self-signed TLS cert,
and prints the generated admin password. Browse to `https://<vm-ip>`.
**Back up `/etc/eprescription/env` (KeyMaster) and `/opt/eprescription/data`: without KeyMaster the doctors' private keys cannot be decrypted.**

## Local development
    # terminal 1
    cd backend
    export Jwt__Secret=$(openssl rand -hex 32) KeyMaster=$(openssl rand -hex 32) Admin__Password='ChangeMe-12345!' Seed__Demo=true ASPNETCORE_URLS=http://127.0.0.1:5000
    dotnet run
    # terminal 2
    cd frontend && npm install && npm run dev      # http://localhost:5173 (camera works on localhost)
Demo accounts (Seed__Demo=true only): `doctor@demo.local`, `pharmacist.a@demo.local`, `pharmacist.b@demo.local`, password `Demo@Password123`.
Admin: `admin@eprescription.local` / the `Admin__Password` you set.

## Requirement traceability
| Req | Where |
|---|---|
| FR-01 / RBAC | JWT + role policies in `Program.cs`; accounts re-checked on every request (instant revocation) |
| FR-02/03 sign + QR | `POST /api/prescriptions`, `CryptoService.Sign`, `Doctor.jsx` |
| FR-04/05 verify + status | `VerificationService.CoreAsync`, `POST /api/verify` |
| FR-06 dispense | `VerificationService.DispenseAsync` – atomic `UPDATE … WHERE Status='Active'` |
| FR-07 admin | `/api/admin/*`; admin has no prescription endpoints |
| SR-01 P-256 | `ECCurve.NamedCurves.nistP256` |
| SR-02 key secrecy | private key never leaves server; AES-256-GCM at rest, master key from env var |
| SR-03 UUID+timestamp | generated server-side, inside the signed payload |
| SR-04 immutability | single transition endpoint + SQLite trigger `trg_rx_status_immutable` |
| SR-05 validation | DataAnnotations + `[SafeText]` allow-list, EF parameterised queries, React output encoding |
| SR-06 HTTPS | nginx redirect + TLS 1.2/1.3 |

## Test plan (Section 3.6)
1. **Functional**: log in as doctor → issue → QR shown; pharmacist verifies → Confirm dispensing.
2. **Tamper**: use *Paste QR data*; edit one character inside `"p"` (e.g. `"quantity":10` → `100`) → *Security alert: prescription invalid*.
3. **Signature swap / key swap**: replace `"s"` or `"k"` with another doctor's → invalid (key must be registered to the payload's doctor).
4. **Replay**: two browser sessions as Pharmacist A and B. A dispenses; B scans same QR → *Already dispensed* with time and pharmacist.
5. **Negative**: empty fields, truncated QR → graceful error; pharmacist calling `/api/prescriptions` → 403.
6. **Revocation**: admin revokes doctor → doctor's token stops working and previously issued QRs fail with *key revoked*.

## Deliberate design notes (differences from the report text)
* The report mentions both client-side and server-side signing; this build signs **server-side only** (SR-02, section 4.3.1).
* The signature is stored as one base64 `r||s` value instead of separate `SignatureR`/`SignatureS` columns.
* Canonical-JSON risk is removed by signing and transporting the *exact payload string*; the verifier never re-serialises.
* The QR's embedded public key is only trusted if it matches a key registered to the payload's doctor, preventing key substitution.
* Verification is performed by the backend, so it needs connectivity; the "offline verification" claim in the report applies to the maths only (as your Section 5.3.2 already says).
* Prescriptions expire after `Rx:ValidityDays` (default 30) in addition to the status check.
* Tables are created with `EnsureCreated`; switch to EF migrations if you move to PostgreSQL.
