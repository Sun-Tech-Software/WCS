"""Riallinea i riferimenti al codice nella documentazione e aggiunge il frammento di contenuto.

I documenti .md sotto il percorso indicato citano il codice con link nella forma
[etichetta](percorso/file.ext:riga). Lo strumento gemello, verifica-riferimenti-doc.ps1,
controlla che la riga esista e, se l'etichetta porta un frammento fra guillemet
([Nome.cs:12 «testo atteso»](percorso:12)), che la riga lo contenga ancora. Questo strumento
serve a mettere il frammento dove manca, riallineando prima le ancore che il codice ha
lasciato indietro.

Per ogni ancora senza frammento:

  1. Si raccolgono gli identificatori in backtick nel testo vicino (etichetta, parentesi che
     segue il link, riga successiva). Contano solo quelli "forti": almeno cinque caratteri e
     fuori da una lista di parole troppo comuni (Add, Items, Run...).
  2. Se la riga citata oggi contiene un identificatore forte, si tiene.
  3. Altrimenti si risale la storia del file sorgente (git log --follow) fino alla revisione
     piu' recente in cui la riga citata portava un identificatore forte (prova "forte") o,
     mancando quella, un'istruzione qualsiasi (prova "debole"). Quel contenuto e' la riga
     intesa, e la si cerca nel file di oggi: l'occorrenza piu' vicina alla posizione attesa,
     entro 120 righe per le prove deboli.
  4. Se oggi la riga e' un'istruzione e la storia non prova che sia slittata, si tiene.
  5. Il frammento e' la riga trovata, ripulita: tagliata a 72 caratteri su uno spazio, senza
     parentesi quadre (una quadra spaiata rompe il testo del link in Markdown). Righe generiche
     (return;, break;, graffe, soli tag di commento XML) non danno frammento.

Le ancore che non si risolvono restano com'erano e finiscono nel rapporto. Senza --scrivi
non tocca nulla: stampa gli esiti e, con --rapporto, salva il dettaglio in JSON.

Con --frammenti lo strumento fa il lavoro opposto, sulle ancore che il frammento ce l'hanno
gia': se il frammento non e' piu' sulla riga citata, si cerca nel file di oggi.

  - una sola occorrenza: il numero si riallinea a quella, nel link e nell'etichetta;
  - piu' occorrenze: **non si tocca nulla** e l'ancora finisce nel rapporto come ambigua.
    L'occorrenza piu' vicina e' una scommessa, e su un file con molte righe uguali la perde;
  - nessuna occorrenza: non si tocca nulla, ed e' il caso che vale la pena guardare a mano —
    vuol dire che il codice citato non c'e' piu', quindi la frase attorno all'ancora
    probabilmente dice il falso.

Uso:
  python WCS/tools/riallinea-riferimenti-doc.py [--radice <repo>] [--percorso WCS]
         [--escludi WCS/doc/README.md ...] [--rapporto rapporto.json] [--frammenti] [--scrivi]

Perche' il blame del documento non basta: una rinomina di massa dei percorsi tocca ogni riga
con un'ancora, e il blame porta a quel commit, dove i numeri erano gia' vecchi. La storia del
sorgente, riga per riga, ritrova invece la revisione in cui il numero era giusto.
"""
from __future__ import annotations

import argparse
import collections
import io
import json
import os
import re
import subprocess
import sys

MAX_REVISIONS = 80
FRAGMENT_WIDTH = 72
WEAK_MAX_DISTANCE = 120

ANCHOR = re.compile(r'\[([^\]]*)\]\(([^()\s:]+\.\w+):(\d+)\)')
# La stessa ancora, ma con il frammento gia' scritto fra guillemet nell'etichetta.
FRAGMENT_ANCHOR = re.compile(r'\[([^\]]*?)\s*«([^»]*)»\]\(([^()\s:]+\.\w+):(\d+)\)')
BACKTICK = re.compile(r'`([^`]+)`')
WORD = re.compile(r'[A-Za-z_][A-Za-z0-9_]{2,}')
RANGE_SUFFIX = re.compile(r':(\d+)-(\d+)$')

# Identificatori troppo comuni per valere come prova: una riga che contiene `Add` o `Items`
# non e' per questo la riga che il testo intendeva.
STOP_WORDS = {
    "void", "item", "List", "Guid", "Task", "null", "true", "false", "string", "bool", "async",
    "await", "static", "public", "private", "internal", "return", "this", "new", "var", "Add",
    "Remove", "Run", "Stop", "Items", "Instance", "Count", "Clear", "Set", "Get", "Value", "Key",
    "Name", "Type", "int", "long", "decimal", "object", "class", "using", "Class1", "grep", "Config",
    "catch", "try", "lock", "Status", "Error", "Done", "Ready", "Busy", "Pause", "Auto", "Manual",
    "Automatic", "Inbound", "Outbound", "WCS", "Wcs", "KazamControl", "Kazam", "ORM", "Common",
    "Engine", "Standard", "Interface", "Test", "Editor", "Viewer", "Host", "Navigator",
}
GENERIC_LINES = {
    "break;", "return;", "continue;", "else", "try", "finally", "{", "}", "});", "};", ");",
    "return null;", "return true;", "return false;", "return result;", "default:", "get;", "set;",
}
XML_TAGS = {"summary", "remarks", "param", "returns", "para", "name", "value", "code", "see",
            "cref", "inheritdoc", "exception", "typeparam"}


# --- lettura ------------------------------------------------------------------------------
class Repo:
    def __init__(self, root: str):
        self.root = root
        self._history: dict[str, list[tuple[str, str]]] = {}
        self._content: dict[tuple[str, str] | str, list[str] | None] = {}

    def git(self, *args: str) -> str | None:
        result = subprocess.run(["git", "-C", self.root, *args], capture_output=True,
                                encoding="utf-8", errors="replace")
        return result.stdout if result.returncode == 0 else None

    def history(self, path: str) -> list[tuple[str, str]]:
        """[(hash, percorso a quel commit)] dal piu' recente, seguendo le rinomine."""
        if path not in self._history:
            out = self.git("log", f"--max-count={MAX_REVISIONS}", "--format=@%H", "--name-status",
                           "--follow", "--", path)
            revisions: list[tuple[str, str]] = []
            current = None
            for line in (out or "").split("\n"):
                if line.startswith("@"):
                    current = line[1:]
                elif line and current:
                    revisions.append((current, line.split("\t")[-1]))
                    current = None
            self._history[path] = revisions
        return self._history[path]

    def at_revision(self, commit: str, path: str) -> list[str] | None:
        key = (commit, path)
        if key not in self._content:
            out = self.git("show", f"{commit}:{path}")
            # git show lascia il BOM in testa alla prima riga.
            self._content[key] = out.lstrip("﻿").split("\n") if out is not None else None
        return self._content[key]

    def today(self, path: str) -> list[str] | None:
        if path not in self._content:
            full = os.path.join(self.root, path)
            self._content[path] = read_lines(full) if os.path.isfile(full) else None
        return self._content[path]


def read_lines(path: str) -> list[str]:
    with io.open(path, encoding="utf-8-sig", errors="replace") as handle:
        return handle.read().split("\n")


# --- classificazione delle righe ----------------------------------------------------------
def is_empty_line(line: str | None) -> bool:
    """Vuota, graffa, commento, using: una riga che nessuno cita di proposito, di norma."""
    if line is None:
        return True
    text = line.strip()
    if text in ("", "{", "}", "};", "});", ")", ");", "else", "try", "finally", "#endregion"):
        return True
    return text.startswith(("#region", "#endregion", "///", "//", "using ", "namespace ", "<!--", "*"))


def words(text: str) -> set[str]:
    return set(WORD.findall(text))


def is_distinctive(line: str) -> bool:
    """Vale come frammento: non generica, con una parola di 4+ lettere che non sia un tag XML."""
    text = line.strip()
    if not text or text in GENERIC_LINES:
        return False
    useful = set(re.findall(r'[A-Za-z_][A-Za-z0-9_]{3,}', text)) - XML_TAGS
    return bool(useful)


def make_fragment(line: str) -> str | None:
    if not is_distinctive(line):
        return None
    text = line.strip()
    for stop in ("]", "»", "«"):
        if stop in text:
            text = text.split(stop)[0]
    text = text.rstrip()
    if "[" in text:
        before, after = text.split("[", 1)
        text = before.rstrip() if is_distinctive(before) else after
    if not is_distinctive(text):
        return None
    if len(text) > FRAGMENT_WIDTH:
        cut = text[:FRAGMENT_WIDTH]
        boundary = max(cut.rfind(" "), cut.rfind("("), cut.rfind(","))
        text = (cut[:boundary] if boundary > 20 else cut).rstrip(" ,.(")
    return text or None


def strong_identifiers(context: str) -> set[str]:
    found: set[str] = set()
    for token in BACKTICK.findall(context):
        found |= words(token)
    return {w for w in found if len(w) >= 5 and w not in STOP_WORDS}


def has_identifier(line: str | None, identifiers: set[str]) -> bool:
    return line is not None and bool(words(line) & identifiers)


# --- decisione ------------------------------------------------------------------------------
def intended_line(repo: Repo, path: str, number: int, identifiers: set[str]):
    """(contenuto inteso, revisione, 'forte'|'debole') oppure (None, None, None)."""
    weak = None
    for commit, old_path in repo.history(path):
        lines = repo.at_revision(commit, old_path)
        if lines is None or number > len(lines):
            continue
        line = lines[number - 1]
        if identifiers and has_identifier(line, identifiers):
            return line, commit[:8], "forte"
        if weak is None and not is_empty_line(line):
            weak = (line, commit[:8])
    if weak:
        return weak[0], weak[1], "debole"
    return None, None, None


def find_today(repo: Repo, path: str, text: str, expected: int) -> int | None:
    """Riga (1-based) di oggi uguale a `text` a meno degli spazi, la piu' vicina a `expected`."""
    wanted = text.strip()
    if not wanted:
        return None
    lines = repo.today(path) or []
    candidates = [i + 1 for i, line in enumerate(lines) if line.strip() == wanted]
    return min(candidates, key=lambda i: abs(i - expected)) if candidates else None


def resolve(repo: Repo, anchor: dict) -> dict:
    """Decide riga nuova, fine dell'intervallo e frammento per un'ancora senza frammento."""
    path, number, identifiers = anchor["path"], anchor["line"], anchor["identifiers"]
    lines = repo.today(path)
    current = lines[number - 1] if lines and number <= len(lines) else None
    outcome, new, revision, intended = None, None, None, None

    if lines is None or current is None:
        outcome = "riga inesistente"
    elif has_identifier(current, identifiers):
        outcome, new = ("tenuta" if not is_empty_line(current) else "tenuta-commento"), number
    else:
        intended, revision, strength = intended_line(repo, path, number, identifiers)
        if not is_empty_line(current) and strength != "forte":
            outcome, new, intended, revision = "tenuta", number, None, None
        elif intended is None:
            # La riga e' sempre stata cosi' (namespace, using, commento): citazione voluta.
            if current.strip():
                outcome, new = "tenuta-intestazione", number
            else:
                outcome = "irrisolta"
        elif intended.strip() == current.strip():
            outcome, new = "tenuta", number
        elif strength == "debole" and not is_distinctive(intended):
            outcome = "irrisolta"
        else:
            new = find_today(repo, path, intended, number)
            if new and strength == "debole" and abs(new - number) > WEAK_MAX_DISTANCE:
                new = None
            outcome = ("riallineata" if strength == "forte" else "riallineata-debole") if new else "sparita"

    end = None
    range_match = RANGE_SUFFIX.search(anchor["label"])
    if new and range_match and int(range_match.group(1)) == number:
        old_end = int(range_match.group(2))
        if outcome.startswith("tenuta"):
            end = old_end
        else:
            # Stesso scorrimento della riga iniziale; poi si prova a confermare col contenuto.
            delta = new - number
            end = old_end + delta
            for commit, old_path in repo.history(path):
                old_lines = repo.at_revision(commit, old_path)
                if old_lines and old_end <= len(old_lines) and old_lines[number - 1].strip() == intended.strip():
                    if old_lines[old_end - 1].strip():
                        found = find_today(repo, path, old_lines[old_end - 1], old_end + delta)
                        if found and found >= new:
                            end = found
                    break

    fragment = make_fragment(lines[new - 1]) if new else None
    if new and not fragment:
        outcome, new, end = "frammento-vuoto", None, None
    return {**anchor, "outcome": outcome, "new": new, "end": end, "revision": revision,
            "current": current.strip()[:120] if current is not None else None,
            "intended": intended.strip()[:120] if intended else None, "fragment": fragment}


# --- documenti ----------------------------------------------------------------------------
def collect_anchors(root: str, doc: str) -> list[dict]:
    lines = read_lines(doc)
    rel_doc = os.path.relpath(doc, root).replace("\\", "/")
    anchors = []
    for index, line in enumerate(lines):
        for match in ANCHOR.finditer(line):
            label = match.group(1)
            if "«" in label:
                continue
            after = re.split(r'\[[^\]]*\]\(', line[match.end():])[0]
            context = f"{label} {after} {lines[index + 1] if index + 1 < len(lines) else ''}"
            anchors.append({"doc": rel_doc, "doc_line": index + 1, "label": label,
                            "path": match.group(2), "line": int(match.group(3)),
                            "identifiers": strong_identifiers(context)})
    return anchors


def collect_fragment_anchors(root: str, doc: str) -> list[dict]:
    """Le ancore che portano gia' il frammento: [Nome.cs:12 «testo»](percorso:12)."""
    lines = read_lines(doc)
    rel_doc = os.path.relpath(doc, root).replace("\\", "/")
    anchors = []
    for index, line in enumerate(lines):
        for match in FRAGMENT_ANCHOR.finditer(line):
            anchors.append({"doc": rel_doc, "doc_line": index + 1, "label": match.group(1),
                            "fragment": match.group(2), "path": match.group(3),
                            "line": int(match.group(4))})
    return anchors


def resolve_fragment(repo: Repo, anchor: dict) -> dict:
    """Il frammento e' la prova: se non e' piu' sulla riga citata, lo si cerca nel file.

    Una sola occorrenza vale come riallineamento; piu' d'una e' ambigua e si lascia stare,
    perche' «la piu' vicina» e' la scommessa che fa i danni. Nessuna occorrenza vuol dire che
    quel codice non c'e' piu': va guardata a mano, non riallineata.
    """
    path, number, fragment = anchor["path"], anchor["line"], anchor["fragment"]
    lines = repo.today(path)
    if lines is None:
        return {**anchor, "outcome": "file inesistente", "new": None, "found": []}
    if number <= len(lines) and fragment in lines[number - 1]:
        return {**anchor, "outcome": "pertinente", "new": None, "found": [number]}
    found = [i + 1 for i, line in enumerate(lines) if fragment in line]
    if not found:
        return {**anchor, "outcome": "frammento sparito", "new": None, "found": []}
    if len(found) > 1:
        return {**anchor, "outcome": "ambigua", "new": None, "found": found}
    return {**anchor, "outcome": "riallineata", "new": found[0], "found": found}


def rewrite_fragment_document(root: str, rel_doc: str, resolved: list[dict]) -> int:
    """Riscrive solo il numero di riga: il frammento e il resto dell'etichetta non si toccano."""
    full = os.path.join(root, rel_doc)
    with io.open(full, "rb") as handle:
        raw = handle.read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    lines = raw.decode("utf-8-sig").split("\n")
    written = 0
    for item in resolved:
        if not item["new"]:
            continue
        path, old, new, fragment = item["path"], item["line"], item["new"], item["fragment"]

        def replace(match: re.Match) -> str:
            nonlocal written
            label, link_frag, link_path, link_line = (match.group(1), match.group(2),
                                                      match.group(3), int(match.group(4)))
            if link_path != path or link_line != old or link_frag != fragment:
                return match.group(0)
            written += 1
            range_match = RANGE_SUFFIX.search(label)
            if range_match and int(range_match.group(1)) == old:
                delta = new - old
                label = label[:range_match.start()] + f":{new}-{int(range_match.group(2)) + delta}"
            elif re.search(r'(?<![\d-])%d\b' % old, label):
                label = re.sub(r'(?<![\d-])%d\b' % old, str(new), label, count=1)
            return f"[{label} «{link_frag}»]({link_path}:{new})"

        lines[item["doc_line"] - 1] = FRAGMENT_ANCHOR.sub(replace, lines[item["doc_line"] - 1])
    data = "\n".join(lines).encode("utf-8")
    with io.open(full, "wb") as handle:
        handle.write((b"\xef\xbb\xbf" if bom else b"") + data)
    return written


def rewrite_document(root: str, rel_doc: str, resolved: list[dict]) -> int:
    full = os.path.join(root, rel_doc)
    with io.open(full, "rb") as handle:
        raw = handle.read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    lines = raw.decode("utf-8-sig").split("\n")
    written = 0
    for item in resolved:
        if not item["new"]:
            continue
        path, old, new, end, fragment = item["path"], item["line"], item["new"], item["end"], item["fragment"]

        def replace(match: re.Match) -> str:
            nonlocal written
            label, link_path, link_line = match.group(1), match.group(2), int(match.group(3))
            if link_path != path or link_line != old or "«" in label:
                return match.group(0)
            written += 1
            range_match = RANGE_SUFFIX.search(label)
            if range_match and int(range_match.group(1)) == old and end:
                label = label[:range_match.start()] + f":{new}-{end}"
            elif re.search(r'\b%d\b' % old, label):
                label = re.sub(r'(?<![\d-])%d\b' % old, str(new), label, count=1)
            return f"[{label} «{fragment}»]({link_path}:{new})"

        lines[item["doc_line"] - 1] = ANCHOR.sub(replace, lines[item["doc_line"] - 1])
    data = "\n".join(lines).encode("utf-8")
    with io.open(full, "wb") as handle:
        handle.write((b"\xef\xbb\xbf" if bom else b"") + data)
    return written


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--radice", default=os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..")),
                        help="radice del repository (default: due livelli sopra questo script)")
    parser.add_argument("--percorso", default="WCS", help="cartella da scandire, relativa alla radice (default: WCS)")
    parser.add_argument("--escludi", nargs="*", default=[], help="documenti da saltare, relativi alla radice")
    parser.add_argument("--rapporto", help="file JSON in cui salvare l'esito di ogni ancora")
    parser.add_argument("--frammenti", action="store_true",
                        help="lavora sulle ancore che il frammento ce l'hanno gia': riallinea il numero di riga "
                             "dove il frammento compare una volta sola, e lascia stare le altre")
    parser.add_argument("--scrivi", action="store_true", help="riscrive i documenti (senza, stampa soltanto)")
    args = parser.parse_args(argv)

    root = os.path.abspath(args.radice)
    repo = Repo(root)
    folder = args.percorso if os.path.isabs(args.percorso) else os.path.join(root, args.percorso)
    excluded = {os.path.normpath(os.path.join(root, e)) for e in args.escludi}
    docs = sorted(os.path.join(d, f) for d, _, files in os.walk(folder) for f in files
                  if f.endswith(".md") and os.sep + "obj" + os.sep not in d and os.sep + "bin" + os.sep not in d)
    docs = [d for d in docs if os.path.normpath(d) not in excluded]

    outcomes: collections.Counter = collections.Counter()
    report: list[dict] = []
    for doc in docs:
        if args.frammenti:
            resolved = [resolve_fragment(repo, a) for a in collect_fragment_anchors(root, doc)]
        else:
            resolved = [resolve(repo, a) for a in collect_anchors(root, doc)]
        for item in resolved:
            outcomes[item["outcome"]] += 1
            if not args.frammenti:
                item["identifiers"] = sorted(item["identifiers"])
        report.extend(resolved)
        if args.scrivi and any(item["new"] for item in resolved):
            rel = os.path.relpath(doc, root).replace("\\", "/")
            count = (rewrite_fragment_document(root, rel, resolved) if args.frammenti
                     else rewrite_document(root, rel, resolved))
            print(f"  {os.path.relpath(doc, root)}: {count} ancore riscritte")

    print(f"Documenti esaminati: {len(docs)}")
    print(f"Ancore {'con' if args.frammenti else 'senza'} frammento: {len(report)}")
    for outcome, count in sorted(outcomes.items()):
        print(f"  {count:5d}  {outcome}")
    unresolved = [r for r in report if not r["new"]]
    if unresolved and not args.rapporto:
        print("Ancore lasciate come sono (usare --rapporto per il dettaglio):")
        for r in unresolved[:20]:
            print(f"  {r['doc']}:{r['doc_line']} [{r['label']}] -> {r['path']}:{r['line']} ({r['outcome']})")
        if len(unresolved) > 20:
            print(f"  ... e altre {len(unresolved) - 20}")
    if args.rapporto:
        with io.open(args.rapporto, "w", encoding="utf-8") as handle:
            handle.write(json.dumps(report, ensure_ascii=False, indent=1))
        print(f"Rapporto scritto in {args.rapporto}")
    if not args.scrivi:
        print("Nessun documento modificato: aggiungere --scrivi per riscrivere.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
