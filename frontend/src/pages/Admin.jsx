import { useEffect, useState } from 'react';
import { api, fmt } from '../api';

const blank = { fullName: '', email: '', password: '', role: 'Doctor' };

export default function Admin() {
  const [tab, setTab] = useState('users');
  const [users, setUsers] = useState([]);
  const [audit, setAudit] = useState([]);
  const [f, setF] = useState(blank);
  const [msg, setMsg] = useState(null);

  const load = () => {
    api('/admin/users').then(setUsers).catch((e) => setMsg({ err: e.message }));
    api('/admin/audit').then(setAudit).catch(() => {});
  };
  useEffect(load, []);
  const set = (k) => (e) => setF({ ...f, [k]: e.target.value });

  async function register(e) {
    e.preventDefault(); setMsg(null);
    try {
      await api('/admin/users', { method: 'POST', body: f });
      setMsg({ ok: f.role === 'Doctor' ? 'Doctor registered. A P-256 signing key pair was generated and the private key is stored encrypted.' : 'Pharmacist registered.' });
      setF(blank); load();
    } catch (x) { setMsg({ err: x.message }); }
  }

  async function revoke(u) {
    if (!confirm(`Revoke access for ${u.fullName}? Their sessions end immediately${u.role === 'Doctor' ? ' and their signing key stops being trusted' : ''}.`)) return;
    try { await api(`/admin/users/${u.id}/revoke`, { method: 'POST' }); load(); } catch (x) { setMsg({ err: x.message }); }
  }

  return (
    <>
      <nav className="tabs">
        <button className={tab === 'users' ? 'on' : ''} onClick={() => setTab('users')}>Users and keys</button>
        <button className={tab === 'audit' ? 'on' : ''} onClick={() => setTab('audit')}>Audit log</button>
      </nav>
      {msg?.ok && <div className="msg ok">{msg.ok}</div>}
      {msg?.err && <div className="msg err">{msg.err}</div>}
      {tab === 'users' ? (
        <>
          <section className="panel">
            <h2>Register a user</h2>
            <form className="grid" onSubmit={register}>
              <label>Full name<input value={f.fullName} onChange={set('fullName')} required /></label>
              <label>Email<input type="email" value={f.email} onChange={set('email')} required /></label>
              <label>Temporary password (12+ characters)<input type="password" value={f.password} onChange={set('password')} minLength={12} required /></label>
              <label>Role<select value={f.role} onChange={set('role')}><option>Doctor</option><option>Pharmacist</option></select></label>
              <div className="wide"><button>Register user</button></div>
            </form>
          </section>
          <section className="panel">
            <h2>Registered users</h2>
            {users.length === 0 ? <p className="muted">No doctors or pharmacists yet. Register one above.</p> : (
              <div className="scroll"><table>
                <thead><tr><th>Name</th><th>Role</th><th>Email</th><th>Key fingerprint</th><th>Status</th><th></th></tr></thead>
                <tbody>{users.map((u) => (
                  <tr key={u.id}><td>{u.fullName}</td><td>{u.role}</td><td>{u.email}</td>
                    <td><code>{u.keyFingerprint || '-'}</code></td>
                    <td><span className={'tag ' + (u.isActive ? 'Active' : 'Revoked')}>{u.isActive ? 'Active' : 'Revoked'}</span></td>
                    <td>{u.isActive && <button className="danger" onClick={() => revoke(u)}>Revoke access</button>}</td></tr>))}</tbody>
              </table></div>
            )}
          </section>
        </>
      ) : (
        <section className="panel">
          <h2>Audit log</h2>
          <div className="scroll"><table>
            <thead><tr><th>Time</th><th>User</th><th>Action</th><th>Prescription</th><th>IP</th></tr></thead>
            <tbody>{audit.map((a) => (
              <tr key={a.logId}><td>{fmt(a.timestamp)}</td><td>{a.user}</td><td>{a.action}</td>
                <td><code>{a.prescriptionId ? a.prescriptionId.slice(0, 8) : ''}</code></td><td>{a.ipAddress}</td></tr>))}</tbody>
          </table></div>
        </section>
      )}
    </>
  );
}
