import { useEffect, useState } from 'react';
import { api, getToken, setToken, onUnauthorized } from './api';
import Login from './pages/Login.jsx';
import Doctor from './pages/Doctor.jsx';
import Pharmacist from './pages/Pharmacist.jsx';
import Admin from './pages/Admin.jsx';

export const Logo = () => (
  <svg width="26" height="26" viewBox="0 0 32 32" aria-hidden="true">
    <defs><linearGradient id="lg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#7fe3a3"/><stop offset="1" stop-color="#0a8f3c"/></linearGradient></defs>
    <rect width="32" height="32" rx="9" fill="url(#lg)"/>
    <path d="M10 16.5l4.2 4.2L22.5 11.5" fill="none" stroke="#02130a" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"/>
  </svg>
);

export default function App() {
  const [me, setMe] = useState(null);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    onUnauthorized(() => setMe(null));
    if (!getToken()) return setReady(true);
    api('/auth/me').then(setMe).catch(() => setToken(null)).finally(() => setReady(true));
  }, []);

  const logout = () => { setToken(null); setMe(null); };
  if (!ready) return null;
  if (!me) return <Login onLogin={setMe} />;

  const View = { Doctor, Pharmacist, Administrator: Admin }[me.role];
  return (
    <>
      <header className="bar">
        <div className="brand"><Logo /> MedSign</div>
        <div className="who">
          {me.role === 'Doctor' && (
            <span className={'key ' + (me.keyActive ? 'ok' : 'bad')}>{me.keyActive ? 'Signing key active' : 'Signing key revoked'}</span>
          )}
          <span>{me.fullName} <small>({me.role})</small></span>
          <button className="ghost" onClick={logout}>Log out</button>
        </div>
      </header>
      <main><View me={me} /></main>
    </>
  );
}
