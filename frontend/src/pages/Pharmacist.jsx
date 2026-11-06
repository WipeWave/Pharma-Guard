import { useEffect, useRef, useState } from "react";
import { Html5Qrcode } from "html5-qrcode";
import jsQR from "jsqr";
import { api, fmt } from "../api";

const TITLES = {
  Valid: ["ok", "Prescription valid and active"],
  Dispensed: ["ok", "Dispensed and locked"],
  InvalidSignature: ["bad", "Security alert: prescription invalid"],
  KeyRevoked: ["bad", "Security alert: prescriber key revoked"],
  Malformed: ["bad", "Unreadable QR code"],
  NotFound: ["bad", "Prescription not found in the registry"],
  Expired: ["warn", "Prescription expired"],
  ReplayDetected: ["warn", "Already dispensed: replay attempt blocked"],
};

export default function Pharmacist() {
  const [scanning, setScanning] = useState(false);
  const [qr, setQr] = useState("");
  const [res, setRes] = useState(null);
  const [err, setErr] = useState("");
  const [paste, setPaste] = useState("");
  const [busy, setBusy] = useState(false);
  const fileRef = useRef();

  async function decodeQrImage(file) {
    const bmp = await createImageBitmap(file);
    const longest = Math.max(bmp.width, bmp.height);
    const scales = [1];
    if (longest > 1600) scales.unshift(1600 / longest);
    if (longest < 1000) scales.push(1200 / longest, 1800 / longest);
    for (const sc of scales) {
      for (const pad of [0.1, 0]) {
        const w = Math.round(bmp.width * sc),
          h = Math.round(bmp.height * sc);
        const p = Math.round(Math.max(w, h) * pad);
        const c = document.createElement("canvas");
        c.width = w + 2 * p;
        c.height = h + 2 * p;
        const ctx = c.getContext("2d", { willReadFrequently: true });
        ctx.fillStyle = "#fff";
        ctx.fillRect(0, 0, c.width, c.height);
        ctx.imageSmoothingEnabled = sc < 1; // keep module edges sharp when enlarging
        ctx.drawImage(bmp, p, p, w, h);
        const img = ctx.getImageData(0, 0, c.width, c.height);
        const hit = jsQR(img.data, img.width, img.height, {
          inversionAttempts: "attemptBoth",
        });
        if (hit?.data) return hit.data;
      }
    }
    throw new Error("No QR found");
  }

  async function verify(data) {
    setErr("");
    setRes(null);
    setQr(data);
    try {
      setRes(await api("/verify", { method: "POST", body: { qrData: data } }));
    } catch (x) {
      setErr(x.message);
    }
  }

  useEffect(() => {
    if (!scanning) return;
    const s = new Html5Qrcode("reader", {
      formatsToSupport: [Html5QrcodeSupportedFormats.QR_CODE],
      experimentalFeatures: { useBarCodeDetectorIfSupported: true },
    });
    let alive = true;
    s.start(
      { facingMode: "environment" },
      {
        fps: 10,
        qrbox: (w, h) => {
          const m = Math.floor(Math.min(w, h) * 0.85);
          return { width: m, height: m };
        },
      },
      (txt) => {
        if (!alive) return;
        alive = false;
        s.stop()
          .catch(() => {})
          .finally(() => {
            try {
              s.clear();
            } catch {}
            setScanning(false);
            verify(txt);
          });
      },
      () => {},
    ).catch((e) => {
      setErr(
        "Camera unavailable (" +
          (e?.message || e) +
          "). Browsers only allow camera access over HTTPS. Upload a QR image or paste the QR data instead.",
      );
      setScanning(false);
    });
    return () => {
      alive = false;
      try {
        if (s.isScanning) s.stop().catch(() => {});
      } catch {}
    };
  }, [scanning]);

  async function onFile(e) {
    const file = e.target.files[0];
    e.target.value = "";
    if (!file) return;
    setErr("");
    try {
      verify(await decodeQrImage(file));
    } catch {
      setErr(
        "No QR code could be read from that image. Use the saved PNG from the doctor (not a photo of a screen), or try the camera.",
      );
    }
  }

  async function dispense() {
    setBusy(true);
    setErr("");
    try {
      setRes(
        await api("/prescriptions/dispense", {
          method: "POST",
          body: { qrData: qr },
        }),
      );
    } catch (x) {
      setErr(x.message);
    } finally {
      setBusy(false);
    }
  }

  const reset = () => {
    setRes(null);
    setErr("");
    setQr("");
    setPaste("");
  };

  if (res) {
    const [tone, title] = TITLES[res.outcome] || ["bad", res.outcome];
    const p = res.prescription;
    return (
      <section className={"panel result " + tone}>
        <div className="badge" aria-hidden="true">
          {tone === "ok" ? "✓" : tone === "bad" ? "✕" : "!"}
        </div>
        <h2>{title}</h2>
        <p>{res.message}</p>
        {p && (
          <dl className="kv">
            <dt>Patient</dt>
            <dd>
              {p.patientName} ({p.patientIc})
            </dd>
            <dt>Prescribed by</dt>
            <dd>{p.doctorName}</dd>
            <dt>Drug</dt>
            <dd>{p.drugName}</dd>
            <dt>Dosage</dt>
            <dd>{p.dosage}</dd>
            <dt>Quantity</dt>
            <dd>{p.quantity}</dd>
            {p.notes && (
              <>
                <dt>Notes</dt>
                <dd>{p.notes}</dd>
              </>
            )}
            <dt>Issued</dt>
            <dd>{fmt(p.issuedAt)}</dd>
          </dl>
        )}
        {res.dispensed && (
          <dl className="kv">
            <dt>First dispensed</dt>
            <dd>{fmt(res.dispensed.dispensedAt)}</dd>
            <dt>By terminal</dt>
            <dd>{res.dispensed.pharmacist || "unknown"}</dd>
          </dl>
        )}
        {err && <div className="msg err">{err}</div>}
        <div className="row">
          {res.outcome === "Valid" && (
            <button onClick={dispense} disabled={busy}>
              {busy ? "Recording…" : "Confirm dispensing"}
            </button>
          )}
          <button className="ghost" onClick={reset}>
            Scan another prescription
          </button>
        </div>
      </section>
    );
  }

  return (
    <section className="panel">
      <h2>Verify a prescription</h2>
      <div id="reader" style={{ display: scanning ? "block" : "none" }} />
      <div id="reader-file" style={{ display: "none" }} />
      {err && <div className="msg err">{err}</div>}
      <div className="row">
        {!scanning ? (
          <button
            onClick={() => {
              setErr("");
              setScanning(true);
            }}
          >
            Scan with camera
          </button>
        ) : (
          <button className="ghost" onClick={() => setScanning(false)}>
            Stop camera
          </button>
        )}
        <button className="ghost" onClick={() => fileRef.current.click()}>
          Upload QR image
        </button>
        <input
          ref={fileRef}
          type="file"
          accept="image/*"
          hidden
          onChange={onFile}
        />
      </div>
      <details>
        <summary>Paste QR data instead</summary>
        <textarea
          rows={4}
          value={paste}
          onChange={(e) => setPaste(e.target.value)}
          placeholder='{"p":"…","s":"…","k":"…"}'
        />
        <button
          className="ghost"
          disabled={!paste.trim()}
          onClick={() => verify(paste.trim())}
        >
          Verify
        </button>
      </details>
    </section>
  );
}
