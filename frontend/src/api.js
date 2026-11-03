let token = sessionStorage.getItem('token');
let onUnauth = () => {};
export const getToken = () => token;
export const setToken = (t) => { token = t; t ? sessionStorage.setItem('token', t) : sessionStorage.removeItem('token'); };
export const onUnauthorized = (fn) => { onUnauth = fn; };

export async function api(path, { method = 'GET', body } = {}) {
  const r = await fetch('/api' + path, {
    method,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined
  });
  const data = await r.json().catch(() => null);
  if (r.status === 401 && token) { setToken(null); onUnauth(); }
  if (!r.ok) throw Object.assign(new Error(data?.error || `Request failed (${r.status})`), { status: r.status });
  return data;
}

export const fmt = (iso) => (iso ? new Date(iso).toLocaleString() : '');
