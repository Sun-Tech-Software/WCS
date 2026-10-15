// Rende un documento Markdown di WCS/doc in HTML (diagrammi mermaid dal CDN) da stampare in PDF con Edge headless.
// Uso: npm install marked && node rendi-pdf.js <sorgente.md> <uscita.html>
// poi: msedge --headless=new --disable-gpu --virtual-time-budget=20000 --no-pdf-header-footer --print-to-pdf=<uscita.pdf> file:///<uscita.html>
const fs = require('fs');
const path = require('path');
const { marked } = require('marked');

const src = process.argv[2];
const out = process.argv[3];
const md = fs.readFileSync(src, 'utf8').replace(/^﻿/, '');

const renderer = new marked.Renderer();
const baseCode = renderer.code.bind(renderer);
renderer.code = function (token) {
  if (token.lang === 'mermaid') return `<pre class="mermaid">${token.text.replace(/</g, '&lt;').replace(/>/g, '&gt;')}</pre>\n`;
  return baseCode(token);
};
// I link relativi agli altri documenti del repo non hanno senso nel PDF: restano testo.
renderer.link = function (token) { return token.tokens ? this.parser.parseInline(token.tokens) : token.text; };

const body = marked.parse(md, { renderer, gfm: true });
const docTitle = (md.match(/^# (.+)$/m) || [, 'WCS'])[1].replace(/\s+—.*$/, '');
const lang = /^# (How|The|A|An) /m.test(md) ? 'en' : 'it';

const html = `<!doctype html>
<html lang="${lang}"><head><meta charset="utf-8">
<title>${docTitle}</title>
<style>
  @page { size: A4; margin: 18mm 16mm 20mm 16mm; }
  html { font-size: 10.5pt; }
  body { font-family: Calibri, "Segoe UI", Arial, sans-serif; color: #1F2A37; line-height: 1.45; max-width: 178mm; margin: 0 auto; }
  h1 { font-family: Cambria, Georgia, serif; font-size: 24pt; color: #1F2A37; margin: 0 0 6mm; line-height: 1.15; }
  h2 { font-family: Cambria, Georgia, serif; font-size: 16pt; color: #2F4B7A; margin: 10mm 0 3mm; page-break-after: avoid; }
  h3 { font-size: 12pt; color: #2E7D6B; margin: 6mm 0 2mm; page-break-after: avoid; }
  p { margin: 0 0 3mm; }
  p.cover { font-size: 11pt; color: #6B7785; }
  code { font-family: Consolas, "Courier New", monospace; font-size: 9pt; background: #F1F3F6; padding: 0 2px; border-radius: 2px; }
  pre { background: #F1F3F6; padding: 3mm; border-radius: 3px; font-size: 8.5pt; line-height: 1.35; overflow-x: hidden; white-space: pre-wrap; page-break-inside: avoid; }
  pre code { background: none; padding: 0; font-size: inherit; }
  pre.mermaid { background: none; padding: 2mm 0; text-align: center; page-break-inside: avoid; }
  pre.mermaid svg { max-width: 100%; height: auto; }
  table { border-collapse: collapse; width: 100%; margin: 2mm 0 4mm; font-size: 9pt; }
  tr { page-break-inside: avoid; }
  th, td { border: 1px solid #D9E0E8; padding: 1.5mm 2mm; vertical-align: top; text-align: left; }
  th { background: #E8EDF3; }
  td code, th code { font-size: 8.5pt; }
  ul, ol { margin: 0 0 3mm; padding-left: 6mm; }
  li { margin-bottom: 1.2mm; }
  strong { color: #1F2A37; }
  hr { border: 0; border-top: 1px solid #D9E0E8; margin: 6mm 0; }
</style>
</head><body>
${body}
<script src="https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js"></script>
<script>
  mermaid.initialize({ startOnLoad: false, theme: 'base', securityLevel: 'loose',
    themeVariables: { primaryColor: '#D9E0E8', primaryBorderColor: '#4A6FA5', primaryTextColor: '#1F2A37',
      lineColor: '#4A6FA5', secondaryColor: '#F4F6F8', tertiaryColor: '#FFFFFF', fontFamily: 'Calibri, Segoe UI, Arial', fontSize: '13px' },
    flowchart: { htmlLabels: true, curve: 'basis' }, sequence: { useMaxWidth: true } });
  mermaid.run({ querySelector: 'pre.mermaid' }).then(() => { document.body.dataset.ready = '1'; })
    .catch(e => { document.body.dataset.ready = 'error'; document.body.insertAdjacentHTML('afterbegin', '<pre style="color:red">' + e + '</pre>'); });
</script>
</body></html>`;
fs.writeFileSync(out, html);
console.log('scritto', out, (fs.statSync(out).size / 1024).toFixed(0) + ' KB');
