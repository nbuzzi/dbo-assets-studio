// UI language. The source text of the Studio is English; Spanish comes from the dictionary in
// i18n-es.js and is applied to the live DOM (text, title, placeholder, aria-label, alt), including
// content that modules render later. Use t() for text that never reaches the DOM (confirm, prompt).
import ES from './i18n-es.js';

const KEY = 'studio.lang';
const ATTRS = ['title', 'placeholder', 'aria-label', 'alt'];
// User data and code are never translated.
const SKIP = 'textarea,pre,code,script,style,[contenteditable],[data-no-i18n]';

function readLang() { try { return localStorage.getItem(KEY) === 'es' ? 'es' : 'en'; } catch { return 'en'; } }
export const lang = readLang();
export const languages = [{id: 'en', label: 'English'}, {id: 'es', label: 'Español'}];

const exact = new Map(Object.entries(ES.strings || {}));
const patterns = (ES.patterns || []).map(([source, replacement]) => [new RegExp(source), replacement]);

/** Translates one English UI string to the active language (identity in English). */
export function t(text) {
 if (lang === 'en' || text == null) return text;
 const s = String(text), core = s.trim();
 if (!core) return s;
 let out = exact.get(core);
 if (out === undefined) {
  for (const [rx, rep] of patterns) { if (rx.test(core)) { out = core.replace(rx, (...m) => rep.replace(/\$(\d)/g, (_, i) => { const v = m[Number(i)] ?? ''; return exact.get(v) ?? v; })); break; } }
 }
 if (out === undefined) return s;
 return s.slice(0, s.indexOf(core)) + out + s.slice(s.indexOf(core) + core.length);
}

export function setLanguage(id) {
 try { localStorage.setItem(KEY, id === 'es' ? 'es' : 'en'); } catch {}
 location.reload();
}

const done = new WeakMap();   // node -> last value we wrote (avoids loops and re-translation)
function translateText(node) {
 const v = node.nodeValue;
 if (!v || !v.trim() || done.get(node) === v) return;
 const p = node.parentElement;
 if (p && p.closest(SKIP)) return;
 const out = t(v);
 done.set(node, out);
 if (out !== v) node.nodeValue = out;
}
function translateAttrs(el) {
 if (el.closest(SKIP) && !el.matches('textarea,[contenteditable]')) return;
 for (const a of ATTRS) {
  const v = el.getAttribute(a);
  if (!v) continue;
  const mark = '__i18n_' + a;
  if (el[mark] === v) continue;
  const out = t(v);
  el[mark] = out;
  if (out !== v) el.setAttribute(a, out);
 }
}
export function translate(root) {
 if (lang === 'en' || !root) return;
 if (root.nodeType === Node.TEXT_NODE) { translateText(root); return; }
 if (root.nodeType !== Node.ELEMENT_NODE && root.nodeType !== Node.DOCUMENT_NODE) return;
 const el = root.nodeType === Node.DOCUMENT_NODE ? root.documentElement : root;
 translateAttrs(el);
 const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
 for (let n = walker.nextNode(); n; n = walker.nextNode()) n.nodeType === Node.TEXT_NODE ? translateText(n) : translateAttrs(n);
}

if (lang !== 'en') {
 document.documentElement.lang = lang;
 const title = document.title; if (title) document.title = t(title);
 translate(document.body);
 new MutationObserver(records => {
  for (const r of records) {
   if (r.type === 'characterData') translateText(r.target);
   else if (r.type === 'attributes') translateAttrs(r.target);
   else for (const n of r.addedNodes) translate(n);
  }
 }).observe(document.body, {subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ATTRS});
} else document.documentElement.lang = 'en';
