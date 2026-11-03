import { useEffect, useState } from 'react';
import { QRCodeCanvas } from 'qrcode.react';
import { api, fmt } from '../api';

const empty = { patientName: '', patientIc: '', drugName: '', dosage: '', quantity: 1, notes: '' };

export default function Doctor({ me }) {
  const [f, setF] = useState(empty);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [issued, setIssued] = useState(null);
  const [list, setList] = useState([]);

  const load = () => api('/prescriptions/mine').then(setList).catch(() => {});
  useEffect(() => { load(); }, []);
  const set = (k) => (e) => setF({ ...f, [k]: e.target.value });
  const ready = me.keyActive && f.patientName.trim() && f.patientIc.trim() && f.drugName.trim() && f.dosage.trim() && Number(f.quantity) >= 1;

  async function submit(e) {
    e.preventDefault(); setErr(''); setBusy(true);
    try {
      const r = await api('/prescriptions', { method: 'POST', body: { ...f, quantity: Number(f.quantity), notes: f.notes || null } });
      setIssued({ ...r, patientName: f.patientName, drugName: f.drugName });
      load();
    } catch (x) { setErr(x.message); } finally { setBusy(false); }
  }

  function download() {
    const a = document.createElement('a');
    a.href = document.getElementById('rx-qr').toDataURL('image/png');
    a.download = `prescription-${issued.prescriptionId.slice(0, 8)}.png`;
    a.click();
  }

  if (issued) {
    return (
      <section className="panel center">
        <h2>Prescription signed and issued</h2>
        <p className="muted">Give this QR code to {issued.patientName}. It can be dispensed once.</p>
        <div className="qr"><QRCodeCanvas id="rx-qr" value={issued.qrData} size={360} level="M" marginSize={4} /></div>
        <dl className="kv">
          <dt>Reference</dt><dd>{issued.prescriptionId.slice(0, 8)}</dd>
          <dt>Signed at</dt><dd>{fmt(issued.issuedAt)}</dd>
          <dt>Medication</dt><dd>{issued.drugName}</dd>
        </dl>
        <p className="muted">This prescription is valid for a single dispensing only.</p>
        <div className="row">
          <button onClick={download}>Save QR image</button>
          <button className="ghost" onClick={() => window.print()}>Print</button>
          <button className="ghost" onClick={() => { setIssued(null); setF(empty); }}>Write another prescription</button>
        </div>
      </section>
    );
  }

  return (
    <>
      <section className="panel">
        <h2>New prescription</h2>
        <form onSubmit={submit} className="grid">
          <label>Patient name *<input value={f.patientName} onChange={set('patientName')} maxLength={120} /></label>
          <label>Patient IC / ID *<input value={f.patientIc} onChange={set('patientIc')} maxLength={20} /></label>
          <label>Drug name *<input value={f.drugName} onChange={set('drugName')} maxLength={120} /></label>
          <label>Dosage *<input value={f.dosage} onChange={set('dosage')} maxLength={80} placeholder="e.g. 500 mg, twice daily" /></label>
          <label>Quantity *<input type="number" min="1" max="1000" value={f.quantity} onChange={set('quantity')} /></label>
          <label className="wide">Notes<input value={f.notes} onChange={set('notes')} maxLength={300} /></label>
          {err && <div className="msg err wide">{err}</div>}
          <div className="wide"><button disabled={!ready || busy}>{busy ? 'Signing…' : 'Sign and issue prescription'}</button></div>
        </form>
      </section>
      <section className="panel">
        <h2>Your recent prescriptions</h2>
        {list.length === 0 ? <p className="muted">Nothing issued yet. Fill in the form above to sign your first prescription.</p> : (
          <div className="scroll"><table>
            <thead><tr><th>Issued</th><th>Patient</th><th>Drug</th><th>Qty</th><th>Status</th></tr></thead>
            <tbody>{list.map((r) => (
              <tr key={r.id}><td>{fmt(r.issuedAt)}</td><td>{r.patientName}</td><td>{r.drugName}</td><td>{r.quantity}</td>
                <td><span className={'tag ' + r.status}>{r.status}</span></td></tr>))}</tbody>
          </table></div>
        )}
      </section>
    </>
  );
}
