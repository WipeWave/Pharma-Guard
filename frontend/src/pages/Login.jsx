import { useState } from "react";
import { api, setToken } from "../api";
import bg from "../assets/login-bg.jpg";
import { Logo } from "../App.jsx";

export default function Login({ onLogin }) {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [err, setErr] = useState("");
  const [busy, setBusy] = useState(false);

  async function submit(e) {
    e.preventDefault();
    setErr("");
    setBusy(true);
    try {
      const r = await api("/auth/login", {
        method: "POST",
        body: { email, password },
      });
      setToken(r.token);
      onLogin(await api("/auth/me"));
    } catch (x) {
      setErr(x.message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login">
      <aside className="login-side" style={{ backgroundImage: `url(${bg})` }}>
        <div className="login-brand">
          <Logo /> Pharma Guard
        </div>
        <div className="login-copy">
          <h1>Prescriptions that can’t be forged or reused.</h1>
          <p>
            Each one is signed by the doctor who wrote it, verified at the
            pharmacy counter, and locked the moment it’s dispensed.
          </p>
        </div>
      </aside>
      <main className="login-main">
        <form className="login-form" onSubmit={submit}>
          <h2>Welcome back</h2>
          <p className="muted">
            Log in with the account your administrator created for you.
          </p>
          <label>
            Email
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              autoComplete="username"
              autoFocus
              required
            />
          </label>
          <label>
            Password
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
              required
            />
          </label>
          {err && (
            <div className="msg err" role="alert">
              {err}
            </div>
          )}
          <button className="primary" disabled={busy}>
            {busy ? "Logging in…" : "Log in"}
          </button>
        </form>
      </main>
    </div>
  );
}
